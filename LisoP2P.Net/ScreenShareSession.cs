using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;

namespace LisoP2P.Net;

public sealed class ScreenShareSession : IScreenShareSession
{
    private static readonly TimeSpan KeyframeRequestInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(1);

    private readonly NetworkOptions _options;
    private readonly IIdentityStore _identity;
    private readonly IDiscoveryService _discovery;
    private readonly ISessionManager _sessionManager;
    private readonly ICapturePipeline _pipeline;
    private readonly IMediaSender _sender;
    private readonly IMediaReceiver _receiver;
    private readonly IMediaLogger _logger;
    private readonly Func<int, int, IVideoDecoder> _decoderFactory;

    private readonly ConcurrentDictionary<Guid, IPeerSession> _observed = new();
    private readonly Channel<DecodableFrame> _decodeQueue = Channel.CreateBounded<DecodableFrame>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _decodeLoop;
    private Timer? _statsTimer;
    private IVideoDecoder? _decoder;
    private IPEndPoint? _destination;
    private PeerId? _sharingWith;
    private int _shareWidth;
    private int _shareHeight;
    private int _shareFps;
    private PeerId? _watchingFrom;
    private DateTimeOffset _lastKeyframeRequestAt;
    private int _decodedCount;
    private int _keyframeRequests;
    private bool _seenKeyframe;

    public bool IsSharing => _sharingWith is not null;
    public bool IsWatching => _watchingFrom is not null;
    public PeerId? SharingWith => _sharingWith;
    public PeerId? WatchingFrom => _watchingFrom;
    public int RemoteWidth { get; private set; }
    public int RemoteHeight { get; private set; }

    public event Action<PreviewFrame>? RemoteFrameReady;
    public event Action? StateChanged;
    public event Action<ScreenShareStats>? StatsUpdated;
    public event Action<string>? Log;

    public ScreenShareSession(
        NetworkOptions options,
        IIdentityStore identity,
        IDiscoveryService discovery,
        ISessionManager sessionManager,
        ICapturePipeline pipeline,
        IMediaSender sender,
        IMediaReceiver receiver,
        IMediaLogger logger,
        Func<int, int, IVideoDecoder>? decoderFactory = null)
    {
        _options = options;
        _identity = identity;
        _discovery = discovery;
        _sessionManager = sessionManager;
        _pipeline = pipeline;
        _sender = sender;
        _receiver = receiver;
        _logger = logger;
        _decoderFactory = decoderFactory ?? ((width, height) =>
            VideoDecoderFactory.Create(width, height, message => Log?.Invoke(message), logger));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _sessionManager.SessionOpened += OnSessionOpened;
        _sessionManager.SessionClosed += OnSessionClosed;

        foreach (var session in _sessionManager.Sessions.Values)
        {
            OnSessionOpened(session);
        }

        _receiver.FrameReassembled += OnFrameReassembled;
        _receiver.FrameDropped += OnFrameDropped;

        await _receiver.StartAsync(_options.MediaPort, _cts.Token).ConfigureAwait(false);

        _decodeLoop = Task.Run(() => DecodeLoopAsync(_cts.Token), CancellationToken.None);
        _statsTimer = new Timer(_ => PublishStats(), null, StatsInterval, StatsInterval);

        _logger.Info($"Sessão de compartilhamento pronta: porta de mídia {_options.MediaPort}.");
    }

    public async Task StartSharingAsync(PeerId target, int monitorIndex, CaptureSettings settings, CancellationToken ct)
    {
        if (IsSharing)
        {
            await StopSharingAsync().ConfigureAwait(false);
        }

        var peer = _discovery.Peers.FirstOrDefault(p => p.Id == target)
            ?? throw new InvalidOperationException("O peer não está mais visível na rede.");

        if (!_sessionManager.Sessions.TryGetValue(target, out var session) || session.State != SessionState.Connected)
        {
            throw new InvalidOperationException("A sessão com o peer não está conectada.");
        }

        var monitor = _pipeline.AvailableMonitors.FirstOrDefault(m => m.Index == monitorIndex)
            ?? throw new InvalidOperationException("Monitor indisponível para captura.");

        var (width, height) = CapturePipeline.ScaleToTarget(
            monitor.Resolution.Width,
            monitor.Resolution.Height,
            settings.TargetHeight);

        _destination = new IPEndPoint(peer.Address, ResolveMediaPort(peer));
        _sharingWith = target;
        _shareWidth = width;
        _shareHeight = height;
        _shareFps = settings.TargetFps;

        _pipeline.FrameReady += OnFrameReady;

        await SendAsync(session, MessageType.ScreenShareStart, ScreenSharePayloadCodec.Encode(new ScreenSharePayload
        {
            Width = width,
            Height = height,
            Fps = settings.TargetFps,
        })).ConfigureAwait(false);

        _logger.Info($"Compartilhando {width}x{height}@{settings.TargetFps} com {peer.Nickname} em {_destination}.");
        Log?.Invoke($"Compartilhando tela com {peer.Nickname}.");

        try
        {
            await _pipeline.StartAsync(monitorIndex, settings, ct).ConfigureAwait(false);
        }
        catch
        {
            _pipeline.FrameReady -= OnFrameReady;
            _sharingWith = null;
            _destination = null;
            throw;
        }

        StateChanged?.Invoke();
    }

    public async Task StopSharingAsync()
    {
        var target = _sharingWith;

        if (target is null)
        {
            return;
        }

        _sharingWith = null;
        _destination = null;
        _pipeline.FrameReady -= OnFrameReady;

        await _pipeline.StopAsync().ConfigureAwait(false);

        if (_sessionManager.Sessions.TryGetValue(target, out var session))
        {
            await SendAsync(session, MessageType.ScreenShareStop, []).ConfigureAwait(false);
        }

        _logger.Info("Compartilhamento encerrado.");
        Log?.Invoke("Compartilhamento encerrado.");
        StateChanged?.Invoke();
    }

    private int ResolveMediaPort(DiscoveredPeer peer) =>
        peer.MediaPort is > 0 and <= 65535 ? peer.MediaPort : NetworkOptions.DefaultMediaPort;

    private void OnFrameReady(EncodedFrame frame)
    {
        var destination = _destination;

        if (destination is not null)
        {
            _sender.SendFrame(frame, destination);
        }
    }

    private void OnSessionOpened(IPeerSession session)
    {
        if (!_observed.TryAdd(session.RemoteId.Value, session))
        {
            return;
        }

        session.MessageReceived += envelope => OnMessageReceived(session, envelope);

        if (_sharingWith == session.RemoteId)
        {
            _ = ResendShareStartAsync(session);
        }
    }

    /// <summary>
    /// A viewer that joins - or reconnects - while the sender is already sharing would otherwise
    /// wait for the next natural GOP boundary staring at a corrupt picture.
    /// </summary>
    private async Task ResendShareStartAsync(IPeerSession session)
    {
        if (_shareWidth <= 0 || _shareHeight <= 0 || _shareFps <= 0)
        {
            return;
        }

        try
        {
            await SendAsync(session, MessageType.ScreenShareStart, ScreenSharePayloadCodec.Encode(new ScreenSharePayload
            {
                Width = _shareWidth,
                Height = _shareHeight,
                Fps = _shareFps,
            })).ConfigureAwait(false);

            _pipeline.RequestKeyframe();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao reanunciar o compartilhamento.", ex);
        }
    }

    private void OnSessionClosed(PeerId id)
    {
        _observed.TryRemove(id.Value, out _);

        if (_sharingWith == id)
        {
            _ = StopSharingAsync();
        }

        if (_watchingFrom == id)
        {
            StopWatching();
        }
    }

    private void OnMessageReceived(IPeerSession session, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.ScreenShareStart:
                HandleShareStart(session, envelope);
                break;
            case MessageType.ScreenShareStop:
                if (_watchingFrom == session.RemoteId)
                {
                    StopWatching();
                }

                break;
            case MessageType.KeyframeRequest:
                if (_sharingWith == session.RemoteId)
                {
                    _pipeline.RequestKeyframe();
                }

                break;
        }
    }

    private void HandleShareStart(IPeerSession session, Envelope envelope)
    {
        if (!ScreenSharePayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        var peer = _discovery.Peers.FirstOrDefault(p => p.Id == session.RemoteId);

        lock (_sync)
        {
            _decoder?.Dispose();
            _decoder = null;

            try
            {
                _decoder = _decoderFactory(payload.Width, payload.Height);
            }
            catch (Exception ex)
            {
                _logger.Error("Falha ao iniciar o decoder.", ex);
                Log?.Invoke($"Falha ao iniciar o decoder: {ex.Message}");
                return;
            }

            RemoteWidth = payload.Width;
            RemoteHeight = payload.Height;
            _watchingFrom = session.RemoteId;
            _seenKeyframe = false;
            _receiver.ExpectedSource = peer?.Address;
            _receiver.Reset();
        }

        _logger.Info($"Recebendo tela de {session.RemoteNickname}: {payload.Width}x{payload.Height}@{payload.Fps}.");
        Log?.Invoke($"Recebendo a tela de {session.RemoteNickname}.");

        RequestKeyframe();
        StateChanged?.Invoke();
    }

    private void StopWatching()
    {
        lock (_sync)
        {
            _decoder?.Dispose();
            _decoder = null;
            _watchingFrom = null;
            _seenKeyframe = false;
            _receiver.ExpectedSource = null;
        }

        _receiver.Reset();
        _logger.Info("Exibição de tela remota encerrada.");
        StateChanged?.Invoke();
    }

    private void OnFrameReassembled(DecodableFrame frame)
    {
        if (_watchingFrom is null)
        {
            return;
        }

        _decodeQueue.Writer.TryWrite(frame);
    }

    private void OnFrameDropped() => RequestKeyframe();

    /// <summary>
    /// Keyframe recovery goes over the TCP session because it has to arrive; it is debounced so a
    /// burst of lost fragments does not turn into a burst of IDR frames.
    /// </summary>
    private void RequestKeyframe()
    {
        var source = _watchingFrom;

        if (source is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            if (now - _lastKeyframeRequestAt < KeyframeRequestInterval)
            {
                return;
            }

            _lastKeyframeRequestAt = now;
        }

        Interlocked.Increment(ref _keyframeRequests);

        if (_sessionManager.Sessions.TryGetValue(source, out var session))
        {
            _ = SendAsync(session, MessageType.KeyframeRequest, []);
        }
    }

    private async Task DecodeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _decodeQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                DecodeOne(frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DecodeOne(DecodableFrame frame)
    {
        PreviewFrame? decoded;

        lock (_sync)
        {
            var decoder = _decoder;

            if (decoder is null)
            {
                return;
            }

            if (!_seenKeyframe)
            {
                if (!frame.IsKeyframe)
                {
                    RequestKeyframe();
                    return;
                }

                _seenKeyframe = true;
            }

            try
            {
                decoded = decoder.Decode(frame);
            }
            catch (Exception ex)
            {
                _logger.Error("Erro ao decodificar.", ex);
                _seenKeyframe = false;
                RequestKeyframe();
                return;
            }
        }

        if (decoded is not { } picture)
        {
            return;
        }

        Interlocked.Increment(ref _decodedCount);
        RemoteFrameReady?.Invoke(picture);
    }

    private async Task SendAsync(IPeerSession session, MessageType type, byte[] payload)
    {
        try
        {
            await session.SendAsync(
                new Envelope
                {
                    Version = ProtocolCodec.CurrentVersion,
                    Type = type,
                    SenderId = _identity.Id.Value,
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Payload = payload,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha ao enviar {type}.", ex);
        }
    }

    private void PublishStats()
    {
        if (StatsUpdated is null)
        {
            return;
        }

        var decoded = Interlocked.Exchange(ref _decodedCount, 0);
        var name = _decoder?.Name ?? "-";

        StatsUpdated.Invoke(new ScreenShareStats(
            decoded,
            _receiver.DroppedFrames,
            _receiver.PendingFrames,
            Volatile.Read(ref _keyframeRequests),
            name));
    }

    public async ValueTask DisposeAsync()
    {
        _sessionManager.SessionOpened -= OnSessionOpened;
        _sessionManager.SessionClosed -= OnSessionClosed;
        _receiver.FrameReassembled -= OnFrameReassembled;
        _receiver.FrameDropped -= OnFrameDropped;

        await StopSharingAsync().ConfigureAwait(false);

        if (_statsTimer is not null)
        {
            await _statsTimer.DisposeAsync().ConfigureAwait(false);
            _statsTimer = null;
        }

        _decodeQueue.Writer.TryComplete();
        _cts?.Cancel();

        if (_decodeLoop is not null)
        {
            try
            {
                await _decodeLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _receiver.DisposeAsync().ConfigureAwait(false);
        _sender.Dispose();

        lock (_sync)
        {
            _decoder?.Dispose();
            _decoder = null;
        }

        _cts?.Dispose();
        _cts = null;
    }
}
