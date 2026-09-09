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

    private readonly ConcurrentDictionary<PeerId, IPeerSession> _observed = new();
    private readonly Channel<DecodableFrame> _decodeQueue = Channel.CreateBounded<DecodableFrame>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _decodeLoop;
    private Timer? _statsTimer;
    private IVideoDecoder? _decoder;
    private List<IPEndPoint> _destinations = [];
    private List<PeerId> _targets = [];
    private CaptureSettings _baseSettings = new();
    private int _monitorIndex;
    private bool _sharing;
    private int _shareWidth;
    private int _shareHeight;
    private int _shareFps;
    private int _shareBitrateKbps;
    private PeerId? _watchingFrom;
    private DateTimeOffset _lastKeyframeRequestAt;
    private int _decodedCount;
    private int _keyframeRequests;
    private bool _seenKeyframe;

    public bool IsSharing => _sharing;
    public bool IsWatching => _watchingFrom is not null;
    public PeerId? WatchingFrom => _watchingFrom;
    public int RemoteWidth { get; private set; }
    public int RemoteHeight { get; private set; }
    public int BitrateKbps => _shareBitrateKbps;
    public int Fps => _shareFps;

    public IReadOnlyList<PeerId> SharingWith
    {
        get
        {
            lock (_sync)
            {
                return [.. _targets];
            }
        }
    }

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

    public async Task StartSharingAsync(
        IReadOnlyCollection<PeerId> targets,
        int monitorIndex,
        CaptureSettings settings,
        CancellationToken ct)
    {
        if (IsSharing)
        {
            await StopSharingAsync().ConfigureAwait(false);
        }

        var connected = targets.Where(IsConnected).ToList();

        if (connected.Count == 0)
        {
            throw new InvalidOperationException("Nenhum destinatário conectado para receber a tela.");
        }

        var monitor = _pipeline.AvailableMonitors.FirstOrDefault(m => m.Index == monitorIndex)
            ?? throw new InvalidOperationException("Monitor indisponível para captura.");

        var (width, height) = CapturePipeline.ScaleToTarget(
            monitor.Resolution.Width,
            monitor.Resolution.Height,
            settings.TargetHeight);

        var step = BitrateLadder.SelectFor(connected.Count);
        var effective = settings with { TargetBitrateKbps = step.BitrateKbps, TargetFps = step.Fps };

        lock (_sync)
        {
            _baseSettings = settings;
            _monitorIndex = monitorIndex;
            _targets = connected;
            _destinations = MediaEndpoints.ResolveAll(_discovery, connected);
            _sharing = true;
            _shareWidth = width;
            _shareHeight = height;
            _shareFps = step.Fps;
            _shareBitrateKbps = step.BitrateKbps;
        }

        _pipeline.FrameReady += OnFrameReady;

        await AnnounceStartAsync(connected).ConfigureAwait(false);

        _logger.Info($"Compartilhando {width}x{height}@{step.Fps} a {step.BitrateKbps}kbps com {connected.Count} peer(s).");
        Log?.Invoke($"Compartilhando tela com {connected.Count} peer(s).");

        try
        {
            await _pipeline.StartAsync(monitorIndex, effective, ct).ConfigureAwait(false);
        }
        catch
        {
            _pipeline.FrameReady -= OnFrameReady;

            lock (_sync)
            {
                _sharing = false;
                _targets = [];
                _destinations = [];
            }

            throw;
        }

        StateChanged?.Invoke();
    }

    public async Task UpdateTargetsAsync(IReadOnlyCollection<PeerId> targets)
    {
        if (!IsSharing)
        {
            return;
        }

        var connected = targets.Where(IsConnected).ToList();

        if (connected.Count == 0)
        {
            await StopSharingAsync().ConfigureAwait(false);
            return;
        }

        List<PeerId> added;
        bool stepChanged;
        CaptureSettings effective;
        int monitorIndex;

        var step = BitrateLadder.SelectFor(connected.Count);

        lock (_sync)
        {
            added = [.. connected.Where(peer => !_targets.Contains(peer))];
            stepChanged = step.BitrateKbps != _shareBitrateKbps || step.Fps != _shareFps;

            _targets = connected;
            _destinations = MediaEndpoints.ResolveAll(_discovery, connected);
            _shareFps = step.Fps;
            _shareBitrateKbps = step.BitrateKbps;

            effective = _baseSettings with { TargetBitrateKbps = step.BitrateKbps, TargetFps = step.Fps };
            monitorIndex = _monitorIndex;
        }

        if (stepChanged)
        {
            // The ladder moves fps as well as bitrate, and the encoder negotiates both at start, so
            // restarting the pipeline is what makes the new step real. It also emits the fresh IDR
            // every receiver needs before it can follow the change.
            _logger.Info($"Degrau de bitrate: {connected.Count} receptor(es), {step.BitrateKbps}kbps@{step.Fps}.");

            await _pipeline.StopAsync().ConfigureAwait(false);
            await AnnounceStartAsync(connected).ConfigureAwait(false);

            try
            {
                await _pipeline.StartAsync(monitorIndex, effective, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error("Falha ao reconfigurar a captura para o novo degrau.", ex);
                await StopSharingAsync().ConfigureAwait(false);
                return;
            }
        }
        else if (added.Count > 0)
        {
            await AnnounceStartAsync(added).ConfigureAwait(false);
            _pipeline.RequestKeyframe();
        }

        StateChanged?.Invoke();
    }

    public async Task StopSharingAsync()
    {
        List<PeerId> targets;

        lock (_sync)
        {
            if (!_sharing)
            {
                return;
            }

            targets = _targets;
            _sharing = false;
            _targets = [];
            _destinations = [];
        }

        _pipeline.FrameReady -= OnFrameReady;

        await _pipeline.StopAsync().ConfigureAwait(false);

        foreach (var target in targets)
        {
            if (_sessionManager.Sessions.TryGetValue(target, out var session))
            {
                await SendAsync(session, MessageType.ScreenShareStop, []).ConfigureAwait(false);
            }
        }

        _logger.Info("Compartilhamento encerrado.");
        Log?.Invoke("Compartilhamento encerrado.");
        StateChanged?.Invoke();
    }

    private bool IsConnected(PeerId peer) =>
        _sessionManager.Sessions.TryGetValue(peer, out var session) && session.State == SessionState.Connected;

    private async Task AnnounceStartAsync(IReadOnlyCollection<PeerId> targets)
    {
        int width, height, fps;

        lock (_sync)
        {
            width = _shareWidth;
            height = _shareHeight;
            fps = _shareFps;
        }

        if (width <= 0 || height <= 0 || fps <= 0)
        {
            return;
        }

        var payload = ScreenSharePayloadCodec.Encode(new ScreenSharePayload
        {
            Width = width,
            Height = height,
            Fps = fps,
        });

        foreach (var target in targets)
        {
            if (_sessionManager.Sessions.TryGetValue(target, out var session))
            {
                await SendAsync(session, MessageType.ScreenShareStart, payload).ConfigureAwait(false);
            }
        }
    }

    private void OnFrameReady(EncodedFrame frame)
    {
        List<IPEndPoint> destinations;

        lock (_sync)
        {
            destinations = _destinations;
        }

        if (destinations.Count > 0)
        {
            _sender.SendFrame(frame, destinations);
        }
    }

    private void OnSessionOpened(IPeerSession session)
    {
        if (!_observed.TryAdd(session.RemoteId, session))
        {
            return;
        }

        session.MessageReceived += envelope => OnMessageReceived(session, envelope);

        bool isTarget;

        lock (_sync)
        {
            isTarget = _sharing && _targets.Contains(session.RemoteId);
        }

        if (isTarget)
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
        try
        {
            await AnnounceStartAsync([session.RemoteId]).ConfigureAwait(false);
            _pipeline.RequestKeyframe();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao reanunciar o compartilhamento.", ex);
        }
    }

    private void OnSessionClosed(PeerId id)
    {
        _observed.TryRemove(id, out _);

        bool wasTarget;
        List<PeerId> remaining;

        lock (_sync)
        {
            wasTarget = _sharing && _targets.Contains(id);
            remaining = [.. _targets.Where(peer => peer != id)];
        }

        if (wasTarget)
        {
            _ = UpdateTargetsAsync(remaining);
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
                bool isTarget;

                lock (_sync)
                {
                    isTarget = _sharing && _targets.Contains(session.RemoteId);
                }

                if (isTarget)
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

        lock (_sync)
        {
            // Only one member shares at a time in this phase, so a second sender is ignored instead
            // of opening a decoder per sender.
            if (_watchingFrom is not null && _watchingFrom != session.RemoteId)
            {
                return;
            }

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
        }

        _receiver.Reset(session.RemoteId);

        _logger.Info($"Recebendo tela de {session.RemoteNickname}: {payload.Width}x{payload.Height}@{payload.Fps}.");
        Log?.Invoke($"Recebendo a tela de {session.RemoteNickname}.");

        RequestKeyframe();
        StateChanged?.Invoke();
    }

    private void StopWatching()
    {
        PeerId? source;

        lock (_sync)
        {
            source = _watchingFrom;
            _decoder?.Dispose();
            _decoder = null;
            _watchingFrom = null;
            _seenKeyframe = false;
        }

        if (source is not null)
        {
            _receiver.Reset(source);
        }

        _logger.Info("Exibição de tela remota encerrada.");
        StateChanged?.Invoke();
    }

    private void OnFrameReassembled(PeerId sender, DecodableFrame frame)
    {
        if (_watchingFrom != sender)
        {
            return;
        }

        _decodeQueue.Writer.TryWrite(frame);
    }

    private void OnFrameDropped(PeerId sender)
    {
        if (_watchingFrom == sender)
        {
            RequestKeyframe();
        }
    }

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
                    SenderId = _identity.Id.PublicKeyBytes,
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

        int bitrate, fps, receivers;

        lock (_sync)
        {
            bitrate = _shareBitrateKbps;
            fps = _shareFps;
            receivers = _targets.Count;
        }

        StatsUpdated.Invoke(new ScreenShareStats(
            decoded,
            _receiver.DroppedFrames,
            _receiver.PendingFrames,
            Volatile.Read(ref _keyframeRequests),
            name,
            bitrate,
            fps,
            receivers));
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
