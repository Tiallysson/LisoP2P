using System.Collections.Concurrent;
using System.Net;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;

namespace LisoP2P.Net;

public sealed class VoiceSession : IVoiceSession
{
    private static readonly TimeSpan SpeakingWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(1);

    private readonly NetworkOptions _options;
    private readonly IIdentityStore _identity;
    private readonly IDiscoveryService _discovery;
    private readonly ISessionManager _sessionManager;
    private readonly IMediaSender _sender;
    private readonly IMediaReceiver _receiver;
    private readonly IAudioCapture _capture;
    private readonly IAudioPlayback _playback;
    private readonly IMediaLogger _logger;
    private readonly Func<AudioSettings, IAudioEncoder> _encoderFactory;
    private readonly Func<AudioSettings, IAudioDecoder> _decoderFactory;

    private readonly ConcurrentDictionary<Guid, IPeerSession> _observed = new();
    private readonly object _sync = new();

    private Timer? _statsTimer;
    private IAudioEncoder? _encoder;
    private IAudioDecoder? _decoder;
    private JitterBuffer? _jitter;
    private IPEndPoint? _destination;
    private AudioSettings _settings = new();
    private PeerId? _target;
    private PeerId? _receivingFrom;
    private DateTimeOffset _lastPeerPacketAt;
    private int _packetsReceived;
    private bool _active;
    private bool _transmitting;

    public bool IsActive => _active;
    public bool IsTransmitting => _transmitting;
    public bool IsReceiving => _receivingFrom is not null;
    public bool IsPeerSpeaking => DateTimeOffset.UtcNow - _lastPeerPacketAt < SpeakingWindow;
    public PeerId? Target => _target;

    public float Volume
    {
        get => _playback.Volume;
        set => _playback.Volume = value;
    }

    public event Action? StateChanged;
    public event Action<VoiceStats>? StatsUpdated;
    public event Action<string>? Log;

    public VoiceSession(
        NetworkOptions options,
        IIdentityStore identity,
        IDiscoveryService discovery,
        ISessionManager sessionManager,
        IMediaSender sender,
        IMediaReceiver receiver,
        IAudioCapture capture,
        IAudioPlayback playback,
        IMediaLogger logger,
        Func<AudioSettings, IAudioEncoder>? encoderFactory = null,
        Func<AudioSettings, IAudioDecoder>? decoderFactory = null)
    {
        _options = options;
        _identity = identity;
        _discovery = discovery;
        _sessionManager = sessionManager;
        _sender = sender;
        _receiver = receiver;
        _capture = capture;
        _playback = playback;
        _logger = logger;
        _encoderFactory = encoderFactory ?? (settings => new OpusAudioEncoder(settings));
        _decoderFactory = decoderFactory ?? (settings => new OpusAudioDecoder(settings));
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sessionManager.SessionOpened += OnSessionOpened;
        _sessionManager.SessionClosed += OnSessionClosed;

        foreach (var session in _sessionManager.Sessions.Values)
        {
            OnSessionOpened(session);
        }

        _receiver.AudioPacketReceived += OnAudioPacketReceived;
        _capture.FrameCaptured += OnFrameCaptured;
        _capture.Failed += OnCaptureFailed;

        _statsTimer = new Timer(_ => PublishStats(), null, StatsInterval, StatsInterval);

        return Task.CompletedTask;
    }

    public async Task StartVoiceAsync(PeerId target, AudioSettings settings, CancellationToken ct)
    {
        if (_active)
        {
            await StopVoiceAsync().ConfigureAwait(false);
        }

        var peer = _discovery.Peers.FirstOrDefault(p => p.Id == target)
            ?? throw new InvalidOperationException("O peer não está mais visível na rede.");

        if (!_sessionManager.Sessions.TryGetValue(target, out var session) || session.State != SessionState.Connected)
        {
            throw new InvalidOperationException("A sessão com o peer não está conectada.");
        }

        lock (_sync)
        {
            _settings = settings;
            _target = target;
            _destination = new IPEndPoint(peer.Address, ResolveMediaPort(peer));
            _encoder = _encoderFactory(settings);
            _active = true;
        }

        _playback.SetSource(PullPlayback);
        _playback.Start(settings);

        await SendAsync(session, MessageType.VoiceStart, VoicePayloadCodec.Encode(new VoicePayload
        {
            SampleRate = settings.SampleRate,
            Channels = settings.Channels,
            FrameSamples = settings.FrameSamples,
        })).ConfigureAwait(false);

        _logger.Info($"Voz ativa com {peer.Nickname} em {_destination} ({settings.Mode}).");
        Log?.Invoke($"Voz ativa com {peer.Nickname}.");
        StateChanged?.Invoke();
    }

    public async Task StopVoiceAsync()
    {
        PeerId? target;

        lock (_sync)
        {
            if (!_active)
            {
                return;
            }

            target = _target;
            _active = false;
            _target = null;
            _destination = null;
        }

        SetTransmitting(false);
        _playback.Stop();

        lock (_sync)
        {
            _encoder?.Dispose();
            _encoder = null;
        }

        if (target is not null && _sessionManager.Sessions.TryGetValue(target, out var session))
        {
            await SendAsync(session, MessageType.VoiceStop, []).ConfigureAwait(false);
        }

        _logger.Info("Voz encerrada.");
        Log?.Invoke("Voz encerrada.");
        StateChanged?.Invoke();
    }

    public void SetTransmitting(bool transmitting)
    {
        lock (_sync)
        {
            if (_transmitting == transmitting)
            {
                return;
            }

            if (transmitting && !_active)
            {
                return;
            }

            _transmitting = transmitting;
        }

        if (transmitting)
        {
            _capture.Start(_settings);
        }
        else
        {
            _capture.Stop();
        }

        StateChanged?.Invoke();
    }

    public void UpdateDevices(string? inputDeviceId, string? outputDeviceId, AudioCaptureMode mode)
    {
        bool wasTransmitting;
        AudioSettings settings;

        lock (_sync)
        {
            _settings = _settings with
            {
                InputDeviceId = inputDeviceId,
                OutputDeviceId = outputDeviceId,
                Mode = mode,
            };

            settings = _settings;
            wasTransmitting = _transmitting;
        }

        if (wasTransmitting)
        {
            _capture.Stop();
            _capture.Start(settings);
        }

        if (_playback.IsRunning)
        {
            _playback.Stop();
            _playback.SetSource(PullPlayback);
            _playback.Start(settings);
        }
    }

    private float[]? PullPlayback()
    {
        var jitter = _jitter;

        return jitter?.Pull();
    }

    private void OnFrameCaptured(AudioFrame frame)
    {
        IAudioEncoder? encoder;
        IPEndPoint? destination;

        lock (_sync)
        {
            if (!_transmitting || !_active)
            {
                return;
            }

            encoder = _encoder;
            destination = _destination;
        }

        if (encoder is null || destination is null)
        {
            return;
        }

        try
        {
            _sender.SendAudio(encoder.Encode(frame), destination);
        }
        catch (Exception ex)
        {
            _logger.Error("Erro ao codificar áudio.", ex);
        }
    }

    private void OnCaptureFailed(Exception error)
    {
        _logger.Error("Captura de áudio interrompida.", error);
        Log?.Invoke($"Captura de áudio interrompida: {error.Message}");
        SetTransmitting(false);
    }

    private void OnAudioPacketReceived(uint sequenceNumber, byte[] opusData)
    {
        var jitter = _jitter;

        if (jitter is null || _receivingFrom is null)
        {
            return;
        }

        Interlocked.Increment(ref _packetsReceived);
        _lastPeerPacketAt = DateTimeOffset.UtcNow;
        jitter.Push(sequenceNumber, opusData, DateTimeOffset.UtcNow.UtcTicks);
    }

    private void OnSessionOpened(IPeerSession session)
    {
        if (!_observed.TryAdd(session.RemoteId.Value, session))
        {
            return;
        }

        session.MessageReceived += envelope => OnMessageReceived(session, envelope);

        if (_active && _target == session.RemoteId)
        {
            _ = ResendVoiceStartAsync(session);
        }
    }

    private async Task ResendVoiceStartAsync(IPeerSession session)
    {
        await SendAsync(session, MessageType.VoiceStart, VoicePayloadCodec.Encode(new VoicePayload
        {
            SampleRate = _settings.SampleRate,
            Channels = _settings.Channels,
            FrameSamples = _settings.FrameSamples,
        })).ConfigureAwait(false);
    }

    private void OnSessionClosed(PeerId id)
    {
        _observed.TryRemove(id.Value, out _);

        if (_target == id)
        {
            _ = StopVoiceAsync();
        }

        if (_receivingFrom == id)
        {
            StopReceiving();
        }
    }

    private void OnMessageReceived(IPeerSession session, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.VoiceStart:
                HandleVoiceStart(session, envelope);
                break;
            case MessageType.VoiceStop:
                if (_receivingFrom == session.RemoteId)
                {
                    StopReceiving();
                }

                break;
        }
    }

    private void HandleVoiceStart(IPeerSession session, Envelope envelope)
    {
        if (!VoicePayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        var settings = new AudioSettings
        {
            SampleRate = payload.SampleRate,
            Channels = payload.Channels,
            FrameSamples = payload.FrameSamples,
        };

        lock (_sync)
        {
            _jitter = null;
            _decoder?.Dispose();

            try
            {
                _decoder = _decoderFactory(settings);
            }
            catch (Exception ex)
            {
                _decoder = null;
                _logger.Error("Falha ao iniciar o decoder de áudio.", ex);
                Log?.Invoke($"Falha ao iniciar o decoder de áudio: {ex.Message}");
                return;
            }

            _jitter = new JitterBuffer(_decoder);
            _receivingFrom = session.RemoteId;
            _packetsReceived = 0;
        }

        if (!_playback.IsRunning)
        {
            _playback.SetSource(PullPlayback);
            _playback.Start(settings);
        }

        _logger.Info($"Recebendo voz de {session.RemoteNickname}: {payload.SampleRate}Hz {payload.FrameSamples} amostras.");
        Log?.Invoke($"Recebendo voz de {session.RemoteNickname}.");
        StateChanged?.Invoke();
    }

    private void StopReceiving()
    {
        lock (_sync)
        {
            _receivingFrom = null;
            _jitter = null;
            _decoder?.Dispose();
            _decoder = null;
        }

        if (!_active)
        {
            _playback.Stop();
        }

        _logger.Info("Recepção de voz encerrada.");
        StateChanged?.Invoke();
    }

    private int ResolveMediaPort(DiscoveredPeer peer) =>
        peer.MediaPort is > 0 and <= 65535 ? peer.MediaPort : NetworkOptions.DefaultMediaPort;

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

        var jitter = _jitter;

        StatsUpdated.Invoke(new VoiceStats(
            jitter?.Depth ?? 0,
            jitter?.ConcealedFrames ?? 0,
            jitter?.LateDiscards ?? 0,
            jitter?.OverflowDiscards ?? 0,
            _sender.AudioPacketsSent,
            Volatile.Read(ref _packetsReceived)));
    }

    public async ValueTask DisposeAsync()
    {
        _sessionManager.SessionOpened -= OnSessionOpened;
        _sessionManager.SessionClosed -= OnSessionClosed;
        _receiver.AudioPacketReceived -= OnAudioPacketReceived;
        _capture.FrameCaptured -= OnFrameCaptured;
        _capture.Failed -= OnCaptureFailed;

        await StopVoiceAsync().ConfigureAwait(false);

        if (_statsTimer is not null)
        {
            await _statsTimer.DisposeAsync().ConfigureAwait(false);
            _statsTimer = null;
        }

        StopReceiving();
        _capture.Dispose();
        _playback.Dispose();
    }
}
