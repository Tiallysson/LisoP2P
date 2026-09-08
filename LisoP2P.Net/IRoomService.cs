using LisoP2P.Core;

namespace LisoP2P.Net;

public interface IRoomService : IAsyncDisposable
{
    RoomId? CurrentRoom { get; }
    string RoomName { get; }
    bool IsInRoom { get; }

    /// <summary>Every known member, including this peer.</summary>
    IReadOnlyList<RoomMember> Members { get; }

    IReadOnlyList<PeerId> RemoteMemberIds { get; }

    event Action? MembersChanged;
    event Action<string>? Log;

    Task StartAsync(CancellationToken ct);
    RoomId CreateRoom(string name);
    Task InviteAsync(PeerId peer, CancellationToken ct);
    Task LeaveAsync();

    /// <summary>Announces push-to-talk state to the room. Never inferred from media packets.</summary>
    Task SetSpeakingAsync(bool speaking);

    void SetSharingScreen(PeerId peer, bool sharing);

    /// <summary>
    /// Clears speakers whose "started" signal was never renewed, and renews our own. Driven by an
    /// internal timer in production; called directly by tests against an injected clock.
    /// </summary>
    void Tick();
}
