using System.Net;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.Tests;

internal sealed class StubIdentityStore(Guid id, string nickname) : IIdentityStore
{
    public PeerId Id { get; } = new(id);
    public string Nickname { get; private set; } = nickname;

    public event Action<string>? NicknameChanged;

    public void SetNickname(string value)
    {
        Nickname = value;
        NicknameChanged?.Invoke(value);
    }
}

internal sealed class FakeDiscoveryService : IDiscoveryService
{
    public IReadOnlyCollection<DiscoveredPeer> Peers => [];

#pragma warning disable CS0067 // required by IDiscoveryService, unused by tests that only need PeerLost wiring
    public event Action<DiscoveredPeer>? PeerAppeared;
    public event Action<DiscoveredPeer>? PeerUpdated;
#pragma warning restore CS0067
    public event Action<PeerId>? PeerLost;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public void RaisePeerLost(PeerId id) => PeerLost?.Invoke(id);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakePeerSession : IPeerSession
{
    public PeerId RemoteId { get; init; } = new(Guid.Empty);
    public string RemoteNickname => "peer";
    public SessionState State { get; set; } = SessionState.Connected;
    public TimeSpan? RoundTripTime => null;
    public List<Envelope> Sent { get; } = [];

#pragma warning disable CS0067
    public event Action<SessionState>? StateChanged;
    public event Action<string>? RemoteNicknameChanged;
#pragma warning restore CS0067
    public event Action<Envelope>? MessageReceived;

    public Task SendAsync(Envelope envelope, CancellationToken ct)
    {
        Sent.Add(envelope);
        return Task.CompletedTask;
    }

    public void Receive(MessageType type, byte[] payload) => MessageReceived?.Invoke(new Envelope
    {
        Version = ProtocolCodec.CurrentVersion,
        Type = type,
        SenderId = RemoteId.Value,
        TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Payload = payload,
    });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSessionManager : ISessionManager
{
    private readonly Dictionary<PeerId, IPeerSession> _sessions = [];

    public IReadOnlyDictionary<PeerId, IPeerSession> Sessions => _sessions;

    public event Action<IPeerSession>? SessionOpened;
    public event Action<PeerId>? SessionClosed;

    public Task StartListeningAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IPeerSession> ConnectAsync(DiscoveredPeer peer, CancellationToken ct) =>
        throw new NotSupportedException();

    public void Open(IPeerSession session)
    {
        _sessions[session.RemoteId] = session;
        SessionOpened?.Invoke(session);
    }

    public void Close(PeerId id)
    {
        _sessions.Remove(id);
        SessionClosed?.Invoke(id);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeDiscoveryWithPeers : IDiscoveryService
{
    public List<DiscoveredPeer> Known { get; } = [];

    public IReadOnlyCollection<DiscoveredPeer> Peers => Known;

#pragma warning disable CS0067
    public event Action<DiscoveredPeer>? PeerAppeared;
    public event Action<DiscoveredPeer>? PeerUpdated;
    public event Action<PeerId>? PeerLost;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMediaSender : IMediaSender
{
    public List<(EncodedFrame Frame, IPEndPoint Destination)> Sent { get; } = [];
    public List<(byte[] Data, IPEndPoint Destination)> AudioSent { get; } = [];

    public uint FramesSent => (uint)Sent.Count;
    public uint AudioPacketsSent => (uint)AudioSent.Count;
    public long BytesSent => Sent.Sum(entry => (long)entry.Frame.Data.Length);

    public void SendFrame(EncodedFrame frame, IPEndPoint destination) => Sent.Add((frame, destination));

    public void SendAudio(ReadOnlySpan<byte> opusData, IPEndPoint destination) =>
        AudioSent.Add((opusData.ToArray(), destination));

    public void Dispose()
    {
    }
}

internal sealed class FakeMediaReceiver : IMediaReceiver
{
    public int PendingFrames => 0;
    public int DroppedFrames { get; set; }
    public IPAddress? ExpectedSource { get; set; }
    public int Resets { get; private set; }
    public int StartedPort { get; private set; }

    public event Action<DecodableFrame>? FrameReassembled;
    public event Action? FrameDropped;
    public event Action<uint, byte[]>? AudioPacketReceived;

    public Task StartAsync(int mediaPort, CancellationToken ct)
    {
        StartedPort = mediaPort;
        return Task.CompletedTask;
    }

    public void Reset() => Resets++;

    public void Emit(DecodableFrame frame) => FrameReassembled?.Invoke(frame);

    public void Drop() => FrameDropped?.Invoke();

    public void EmitAudio(uint sequenceNumber, byte[] opusData) =>
        AudioPacketReceived?.Invoke(sequenceNumber, opusData);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
