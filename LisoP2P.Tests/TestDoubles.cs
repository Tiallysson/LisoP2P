using System.Net;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.Tests;

/// <summary>Deterministic peer ids: fase 6 keys are 32 bytes, so tests spell them out.</summary>
internal static class TestIds
{
    public static PeerId New() => From((byte)Random.Shared.Next(1, 256));

    public static PeerId From(byte seed)
    {
        var bytes = new byte[PeerId.PublicKeySize];
        Array.Fill(bytes, seed);
        return new PeerId(bytes);
    }
}

internal sealed class StubIdentityStore(PeerId id, string nickname) : IIdentityStore
{
    public PeerId Id { get; } = id;
    public string Nickname { get; private set; } = nickname;
    public string Fingerprint => PeerFingerprint.For(Id);

    public PeerIdentity Current => new()
    {
        PublicKey = Id.PublicKeyBytes,
        Fingerprint = Fingerprint,
        Nickname = Nickname,
    };

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
    public PeerId RemoteId { get; init; } = TestIds.From(0);
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
        SenderId = RemoteId.PublicKeyBytes,
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

    public uint FramesSent { get; private set; }
    public uint AudioPacketsSent { get; private set; }
    public long BytesSent => Sent.Sum(entry => (long)entry.Frame.Data.Length);

    public void SendFrame(EncodedFrame frame, IReadOnlyCollection<IPEndPoint> destinations)
    {
        foreach (var destination in destinations)
        {
            Sent.Add((frame, destination));
        }

        FramesSent++;
    }

    public void SendAudio(ReadOnlySpan<byte> opusData, IReadOnlyCollection<IPEndPoint> destinations)
    {
        var data = opusData.ToArray();

        foreach (var destination in destinations)
        {
            AudioSent.Add((data, destination));
        }

        AudioPacketsSent++;
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeMediaReceiver : IMediaReceiver
{
    public int PendingFrames => 0;
    public int DroppedFrames { get; set; }
    public int Resets { get; private set; }
    public int StartedPort { get; private set; }
    public List<PeerId> ResetSenders { get; } = [];

    public event Action<PeerId, DecodableFrame>? FrameReassembled;
    public event Action<PeerId>? FrameDropped;
    public event Action<PeerId, uint, byte[]>? AudioPacketReceived;

    public Task StartAsync(int mediaPort, CancellationToken ct)
    {
        StartedPort = mediaPort;
        return Task.CompletedTask;
    }

    public void Reset() => Resets++;

    public void Reset(PeerId sender)
    {
        Resets++;
        ResetSenders.Add(sender);
    }

    public void Emit(PeerId sender, DecodableFrame frame) => FrameReassembled?.Invoke(sender, frame);

    public void Drop(PeerId sender) => FrameDropped?.Invoke(sender);

    public void EmitAudio(PeerId sender, uint sequenceNumber, byte[] opusData) =>
        AudioPacketReceived?.Invoke(sender, sequenceNumber, opusData);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
