using System.Threading.Channels;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;

namespace LisoP2P.Net;

public sealed class PeerSession : IPeerSession
{
    public delegate Task<Stream> Connector(CancellationToken ct);

    private const int HandshakeTimeoutSeconds = 5;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    ];
    private static readonly TimeSpan ReconnectSteadyDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxReconnectDuration = TimeSpan.FromMinutes(5);

    private readonly IIdentityStore _identity;
    private readonly IDiscoveryService? _discovery;
    private readonly Connector? _connector;
    private readonly Channel<Envelope> _sendQueue = Channel.CreateUnbounded<Envelope>();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TaskCompletionSource _handshakeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Stream? _stream;
    private Task? _runTask;
    private DateTimeOffset _lastPongAt;

    public PeerId RemoteId { get; private set; } = new(Guid.Empty);
    public string RemoteNickname { get; private set; } = "";
    public SessionState State { get; private set; } = SessionState.Connecting;
    public TimeSpan? RoundTripTime { get; private set; }

    public event Action<SessionState>? StateChanged;
    public event Action<Envelope>? MessageReceived;

    private PeerSession(IIdentityStore identity, IDiscoveryService? discovery, Connector? connector)
    {
        _identity = identity;
        _discovery = discovery;
        _connector = connector;
    }

    public static PeerSession CreateOutbound(
        PeerId targetId, Connector connector, IIdentityStore identity, IDiscoveryService discovery)
    {
        var session = new PeerSession(identity, discovery, connector) { RemoteId = targetId };
        session._runTask = Task.Run(() => session.RunOutboundAsync(session._lifetimeCts.Token));
        return session;
    }

    public static PeerSession CreateInbound(Stream stream, IIdentityStore identity)
    {
        var session = new PeerSession(identity, discovery: null, connector: null) { _stream = stream };
        session._runTask = Task.Run(() => session.RunInboundAsync(session._lifetimeCts.Token));
        return session;
    }

    /// <summary>Completes once the initial handshake has finished (successfully or not).</summary>
    public Task WaitForHandshakeAsync() => _handshakeCompletion.Task;

    public Task SendAsync(Envelope envelope, CancellationToken ct) => EnqueueAsync(envelope, ct);

    private async Task RunOutboundAsync(CancellationToken lifetimeCt)
    {
        if (!await TryConnectOnceAsync(lifetimeCt, announceStates: true).ConfigureAwait(false))
        {
            _handshakeCompletion.TrySetResult();
            SetState(SessionState.Closed);
            return;
        }

        _handshakeCompletion.TrySetResult();

        while (!lifetimeCt.IsCancellationRequested)
        {
            SetState(SessionState.Connected);
            await RunConnectedLoopAsync(lifetimeCt).ConfigureAwait(false);
            CleanupConnection();

            if (lifetimeCt.IsCancellationRequested)
            {
                break;
            }

            if (!await ReconnectLoopAsync(lifetimeCt).ConfigureAwait(false))
            {
                SetState(SessionState.Closed);
                return;
            }
        }
    }

    private async Task RunInboundAsync(CancellationToken lifetimeCt)
    {
        try
        {
            SetState(SessionState.Handshaking);
            if (!await PerformHandshakeAsync(_stream!, isInitiator: false, lifetimeCt).ConfigureAwait(false))
            {
                _handshakeCompletion.TrySetResult();
                SetState(SessionState.Closed);
                return;
            }

            _handshakeCompletion.TrySetResult();
            SetState(SessionState.Connected);
            await RunConnectedLoopAsync(lifetimeCt).ConfigureAwait(false);
        }
        finally
        {
            CleanupConnection();
            SetState(SessionState.Closed);
        }
    }

    private async Task<bool> TryConnectOnceAsync(CancellationToken ct, bool announceStates)
    {
        if (announceStates)
        {
            SetState(SessionState.Connecting);
        }

        try
        {
            var stream = await _connector!(ct).ConfigureAwait(false);

            if (announceStates)
            {
                SetState(SessionState.Handshaking);
            }

            if (!await PerformHandshakeAsync(stream, isInitiator: true, ct).ConfigureAwait(false))
            {
                await SafeDisposeAsync(stream).ConfigureAwait(false);
                return false;
            }

            _stream = stream;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ReconnectLoopAsync(CancellationToken lifetimeCt)
    {
        SetState(SessionState.Reconnecting);

        var deadline = DateTimeOffset.UtcNow + MaxReconnectDuration;
        var lost = false;
        void OnPeerLost(PeerId id)
        {
            if (id == RemoteId)
            {
                lost = true;
            }
        }

        if (_discovery is not null)
        {
            _discovery.PeerLost += OnPeerLost;
        }

        try
        {
            var delayIndex = 0;
            while (!lifetimeCt.IsCancellationRequested && !lost && DateTimeOffset.UtcNow < deadline)
            {
                var delay = delayIndex < ReconnectDelays.Length ? ReconnectDelays[delayIndex++] : ReconnectSteadyDelay;
                try
                {
                    await Task.Delay(delay, lifetimeCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                if (lost)
                {
                    return false;
                }

                if (await TryConnectOnceAsync(lifetimeCt, announceStates: false).ConfigureAwait(false))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (_discovery is not null)
            {
                _discovery.PeerLost -= OnPeerLost;
            }
        }
    }

    private async Task<bool> PerformHandshakeAsync(Stream stream, bool isInitiator, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));

        try
        {
            if (isInitiator)
            {
                await SendHandshakeEnvelopeAsync(stream, MessageType.Hello, timeoutCts.Token).ConfigureAwait(false);

                var reply = await FrameReader.ReadAsync(stream, timeoutCts.Token).ConfigureAwait(false);
                if (reply is null || reply.Type != MessageType.HelloAck)
                {
                    return false;
                }

                if (!HelloPayloadCodec.TryDecode(reply.Payload, out var payload) || payload is null)
                {
                    return false;
                }

                if (payload.ProtocolVersion != HelloPayloadCodec.CurrentProtocolVersion)
                {
                    await TrySendDisconnectAsync(stream, ct).ConfigureAwait(false);
                    return false;
                }

                RemoteNickname = payload.Nickname;
                return true;
            }
            else
            {
                var hello = await FrameReader.ReadAsync(stream, timeoutCts.Token).ConfigureAwait(false);
                if (hello is null || hello.Type != MessageType.Hello)
                {
                    return false;
                }

                if (!HelloPayloadCodec.TryDecode(hello.Payload, out var payload) || payload is null)
                {
                    return false;
                }

                if (payload.ProtocolVersion != HelloPayloadCodec.CurrentProtocolVersion)
                {
                    await TrySendDisconnectAsync(stream, ct).ConfigureAwait(false);
                    return false;
                }

                RemoteId = new PeerId(hello.SenderId);
                RemoteNickname = payload.Nickname;

                await SendHandshakeEnvelopeAsync(stream, MessageType.HelloAck, timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async Task RunConnectedLoopAsync(CancellationToken lifetimeCt)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCt);
        _lastPongAt = DateTimeOffset.UtcNow;

        var readTask = ReadLoopAsync(connectionCts.Token);
        var writeTask = WriteLoopAsync(connectionCts.Token);
        var keepaliveTask = KeepaliveLoopAsync(connectionCts.Token);

        await Task.WhenAny(readTask, writeTask, keepaliveTask).ConfigureAwait(false);
        await connectionCts.CancelAsync().ConfigureAwait(false);

        await SafeAwait(readTask).ConfigureAwait(false);
        await SafeAwait(writeTask).ConfigureAwait(false);
        await SafeAwait(keepaliveTask).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Envelope? envelope;
            try
            {
                envelope = await FrameReader.ReadAsync(_stream!, ct).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (envelope is null)
            {
                return;
            }

            switch (envelope.Type)
            {
                case MessageType.Ping:
                    await EnqueueAsync(BuildPongEnvelope(envelope.TimestampUnixMs), ct).ConfigureAwait(false);
                    break;
                case MessageType.Pong:
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    RoundTripTime = TimeSpan.FromMilliseconds(Math.Max(0, nowMs - envelope.TimestampUnixMs));
                    _lastPongAt = DateTimeOffset.UtcNow;
                    break;
                case MessageType.Disconnect:
                    return;
                default:
                    MessageReceived?.Invoke(envelope);
                    break;
            }
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await FrameWriter.WriteAsync(_stream!, envelope, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);

                if (DateTimeOffset.UtcNow - _lastPongAt > PongTimeout)
                {
                    return;
                }

                await EnqueueAsync(BuildPingEnvelope(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnqueueAsync(Envelope envelope, CancellationToken ct)
    {
        try
        {
            await _sendQueue.Writer.WriteAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private Envelope BuildPingEnvelope() => new()
    {
        Version = ProtocolCodec.CurrentVersion,
        Type = MessageType.Ping,
        SenderId = _identity.Id.Value,
        TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private Envelope BuildPongEnvelope(long originalTimestampMs) => new()
    {
        Version = ProtocolCodec.CurrentVersion,
        Type = MessageType.Pong,
        SenderId = _identity.Id.Value,
        TimestampUnixMs = originalTimestampMs,
    };

    private async Task SendHandshakeEnvelopeAsync(Stream stream, MessageType type, CancellationToken ct)
    {
        var envelope = new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = type,
            SenderId = _identity.Id.Value,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = HelloPayloadCodec.Encode(new HelloPayload
            {
                Nickname = _identity.Nickname,
                ProtocolVersion = HelloPayloadCodec.CurrentProtocolVersion,
            }),
        };
        await FrameWriter.WriteAsync(stream, envelope, ct).ConfigureAwait(false);
    }

    private async Task TrySendDisconnectAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            var envelope = new Envelope
            {
                Version = ProtocolCodec.CurrentVersion,
                Type = MessageType.Disconnect,
                SenderId = _identity.Id.Value,
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await FrameWriter.WriteAsync(stream, envelope, ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void CleanupConnection()
    {
        var stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            _ = SafeDisposeAsync(stream);
        }
    }

    private static async Task SafeDisposeAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void SetState(SessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        _sendQueue.Writer.TryComplete();

        if (_runTask is not null)
        {
            await SafeAwait(_runTask).ConfigureAwait(false);
        }

        CleanupConnection();
        _lifetimeCts.Dispose();
        SetState(SessionState.Closed);
    }
}
