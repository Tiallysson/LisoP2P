using LisoP2P.Core;
using LisoP2P.Core.Protocol;

namespace LisoP2P.Net;

public interface IPeerSession : IAsyncDisposable
{
    PeerId RemoteId { get; }
    string RemoteNickname { get; }
    SessionState State { get; }
    TimeSpan? RoundTripTime { get; }

    event Action<SessionState>? StateChanged;
    event Action<Envelope>? MessageReceived;
    event Action<string>? RemoteNicknameChanged;

    Task SendAsync(Envelope envelope, CancellationToken ct);
}
