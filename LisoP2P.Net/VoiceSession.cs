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

    private sealed class RemoteVoice
    {
        public required IAudioDecoder Decoder { get; init; }
        public required JitterBuffer Buffer { get; init; }
    }

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

    /// <summary>
    /// One jitter buffer per sender. Sequence numbers are per sender, so folding several sources
    /// into a single buffer would make every packet look out of order to the one before it.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, RemoteVoice> _remotes = new();

    private readonly ConcurrentDictionary<Guid, IPeerSession> _observed = new();
    private readonly IAudioMixer _mixer;
    private readonly object _sync = new();

    private Timer? _statsTimer;
    private IAudioEncoder? _encoder;
    private List<IPEndPoint> _destinations = [];
    private List<PeerId> _targets = [];
    private AudioSettings _settings = new();
    private DateTimeOffset _lastPeerPacketAt;
    private int _packetsReceived;
    private bool _active;
    private bool _transmitting;

    public bool IsActive => _active;
    public bool IsTransmitting => _transmitting;
    public bool IsReceiving => !_remotes.IsEmpty;
    public bool IsPeerSpeaking => DateTimeOffset.UtcNow - _lastPeerPacketAt < SpeakingWindow;

    public IReadOnlyList<PeerId> Targets
    {
        get
        {
            lock (_sync)
            {
                return [.. _targets];
            }
        }
    }

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
        Func<AudioSettings, IAudioDecoder>? decoderFactory = null,
        IAudioMixer? mixer = null)
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
        _mixer = mixer ?? new AudioMixer(ResolveBuffer);
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

    public async Task StartVoiceAsync(IReadOnlyCollection<PeerId> targets, AudioSettings settings, CancellationToken ct)
    {
        if (_active)
        {
            await StopVoiceAsync().ConfigureAwait(false);
        }

        var connected = targets.Where(IsConnected).ToList();

        if (connected.Count == 0)
        {
            throw new InvalidOperationException("Nenhum destinatário conectado para receber a voz.");
        }

        lock (_sync)
        {
            _settings = settings;
            _targets = connected;
            _destinations = MediaEndpoints.ResolveAll(_discovery, connected);
            _encoder = _encoderFactory(settings);
            _active = true;
        }

        _playback.SetSource(PullPlayback);
        _playback.Start(settings);

        await AnnounceVoiceStartAsync(connected).ConfigureAwait(false);

        _logger.Info($"Voz ativa com {connected.Count} peer(s) ({settings.Mode}).");
        Log?.Invoke($"Voz ativa com {connected.Count} peer(s).");
        StateChanged?.Invoke();
    }

    public async Task UpdateTargetsAsync(IReadOnlyCollection<PeerId> targets)
    {
        if (!_active)
        {
            return;
        }

        var connected = targets.Where(IsConnected).ToList();

        if (connected.Count == 0)
        {
            await StopVoiceAsync().ConfigureAwait(false);
            return;
        }

        List<PeerId> added;

        lock (_sync)
        {
            added = [.. connected.Where(peer => !_targets.Contains(peer))];
            _targets = connected;
            _destinations = MediaEndpoints.ResolveAll(_discovery, connected);
        }

        if (added.Count > 0)
        {
            await AnnounceVoiceStartAsync(added).ConfigureAwait(false);
        }

        StateChanged?.Invoke();
    }

    public async Task StopVoiceAsync()
    {
        List<PeerId> targets;

        lock (_sync)
        {
            if (!_active)
            {
                return;
            }

            targets = _targets;
            _active = false;
            _targets = [];
            _destinations = [];
        }

        SetTransmitting(false);

        lock (_sync)
        {
            _encoder?.Dispose();
            _encoder = null;
        }

        foreach (var target in targets)
        {
            if (_sessionManager.Sessions.TryGetValue(target, out var session))
            {
                await SendAsync(session, MessageType.VoiceStop, []).ConfigureAwait(false);
            }
        }

        if (!IsReceiving)
        {
            _playback.Stop();
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

        // Releasing push-to-talk has to stop the capture device, not merely stop sending: a mic
        // that keeps running is both a battery cost and a privacy surprise.
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

    private bool IsConnected(PeerId peer) =>
        _sessionManager.Sessions.TryGetValue(peer, out var session) && session.State == SessionState.Connected;

    private IJitterBuffer? ResolveBuffer(PeerId peer) =>
        _remotes.TryGetValue(peer.Value, out var remote) ? remote.Buffer : null;

    private float[]? PullPlayback() => _mixer.MixNextFrame();

    private void OnFrameCaptured(AudioFrame frame)
    {
        IAudioEncoder? encoder;
        List<IPEndPoint> destinations;

        lock (_sync)
        {
            if (!_transmitting || !_active)
            {
                return;
            }

            encoder = _encoder;
            destinations = _destinations;
        }

        if (encoder is null || destinations.Count == 0)
        {
            return;
        }

        try
        {
            // One encode, N sends - the same Opus frame goes to every member.
            _sender.SendAudio(encoder.Encode(frame), destinations);
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

    private void OnAudioPacketReceived(PeerId sender, uint sequenceNumber, byte[] opusData)
    {
        var remote = EnsureRemote(sender);

        if (remote is null)
        {
            return;
        }

        Interlocked.Increment(ref _packetsReceived);
        _lastPeerPacketAt = DateTimeOffset.UtcNow;
        remote.Buffer.Push(sequenceNumber, opusData, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>
    /// Audio is always 48 kHz mono in 20 ms frames on the wire, so a buffer can be built on the
    /// first packet even when the VoiceStart that announced it was lost.
    /// </summary>
    private RemoteVoice? EnsureRemote(PeerId sender)
    {
        if (_remotes.TryGetValue(sender.Value, out var existing))
        {
            return existing;
        }

        AudioSettings settings;

        lock (_sync)
        {
            settings = _settings;
        }

        return AddRemote(sender, settings with
        {
            SampleRate = AudioSettings.DefaultSampleRate,
            Channels = AudioSettings.DefaultChannels,
            FrameSamples = AudioSettings.DefaultFrameSamples,
        });
    }

    private RemoteVoice? AddRemote(PeerId sender, AudioSettings settings)
    {
        IAudioDecoder decoder;

        try
        {
            decoder = _decoderFactory(settings);
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao iniciar o decoder de áudio.", ex);
            Log?.Invoke($"Falha ao iniciar o decoder de áudio: {ex.Message}");
            return null;
        }

        var created = new RemoteVoice { Decoder = decoder, Buffer = new JitterBuffer(decoder) };
        var stored = _remotes.GetOrAdd(sender.Value, created);

        if (!ReferenceEquals(stored, created))
        {
            decoder.Dispose();
            return stored;
        }

        RefreshMixerSources();

        if (!_playback.IsRunning)
        {
            _playback.SetSource(PullPlayback);
            _playback.Start(settings);
        }

        StateChanged?.Invoke();
        return stored;
    }

    private void RemoveRemote(PeerId sender)
    {
        if (!_remotes.TryRemove(sender.Value, out var remote))
        {
            return;
        }

        remote.Decoder.Dispose();
        RefreshMixerSources();

        if (_remotes.IsEmpty && !_active)
        {
            _playback.Stop();
        }

        StateChanged?.Invoke();
    }

    private void RefreshMixerSources() =>
        _mixer.SetActiveSources([.. _remotes.Keys.Select(id => new PeerId(id))]);

    private void OnSessionOpened(IPeerSession session)
    {
        if (!_observed.TryAdd(session.RemoteId.Value, session))
        {
            return;
        }

        session.MessageReceived += envelope => OnMessageReceived(session, envelope);

        bool isTarget;

        lock (_sync)
        {
            isTarget = _active && _targets.Contains(session.RemoteId);
        }

        if (isTarget)
        {
            _ = AnnounceVoiceStartAsync([session.RemoteId]);
        }
    }

    private async Task AnnounceVoiceStartAsync(IReadOnlyCollection<PeerId> targets)
    {
        AudioSettings settings;

        lock (_sync)
        {
            settings = _settings;
        }

        var payload = VoicePayloadCodec.Encode(new VoicePayload
        {
            SampleRate = settings.SampleRate,
            Channels = settings.Channels,
            FrameSamples = settings.FrameSamples,
        });

        foreach (var target in targets)
        {
            if (_sessionManager.Sessions.TryGetValue(target, out var session))
            {
                await SendAsync(session, MessageType.VoiceStart, payload).ConfigureAwait(false);
            }
        }
    }

    private void OnSessionClosed(PeerId id)
    {
        _observed.TryRemove(id.Value, out _);
        RemoveRemote(id);

        bool wasTarget;
        List<PeerId> remaining;

        lock (_sync)
        {
            wasTarget = _active && _targets.Contains(id);
            remaining = [.. _targets.Where(peer => peer != id)];
        }

        if (wasTarget)
        {
            _ = UpdateTargetsAsync(remaining);
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
                RemoveRemote(session.RemoteId);
                break;
        }
    }

    private void HandleVoiceStart(IPeerSession session, Envelope envelope)
    {
        if (!VoicePayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        if (_remotes.ContainsKey(session.RemoteId.Value))
        {
            return;
        }

        var settings = new AudioSettings
        {
            SampleRate = payload.SampleRate,
            Channels = payload.Channels,
            FrameSamples = payload.FrameSamples,
        };

        if (AddRemote(session.RemoteId, settings) is null)
        {
            return;
        }

        _logger.Info($"Recebendo voz de {session.RemoteNickname}: {payload.SampleRate}Hz {payload.FrameSamples} amostras.");
        Log?.Invoke($"Recebendo voz de {session.RemoteNickname}.");
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

        var remotes = _remotes.Values.ToArray();

        StatsUpdated.Invoke(new VoiceStats(
            remotes.Sum(r => r.Buffer.Depth),
            remotes.Sum(r => r.Buffer.ConcealedFrames),
            remotes.Sum(r => r.Buffer.LateDiscards),
            remotes.Sum(r => r.Buffer.OverflowDiscards),
            _sender.AudioPacketsSent,
            Volatile.Read(ref _packetsReceived),
            remotes.Length));
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

        foreach (var id in _remotes.Keys.ToArray())
        {
            RemoveRemote(new PeerId(id));
        }

        _capture.Dispose();
        _playback.Dispose();
    }
}
