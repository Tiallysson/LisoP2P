using System.Collections.Concurrent;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;

namespace LisoP2P.Net;

public sealed class RoomService : IRoomService
{
    /// <summary>
    /// How long a "started speaking" signal stays valid without renewal. Covers the case where the
    /// remote app dies with push-to-talk held down and never sends "stopped".
    /// </summary>
    public static readonly TimeSpan SpeakingTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan SpeakingRenewInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly IIdentityStore _identity;
    private readonly IDiscoveryService _discovery;
    private readonly ISessionManager _sessionManager;
    private readonly Func<DateTimeOffset> _clock;

    private readonly Dictionary<PeerId, RoomMember> _members = [];
    private readonly Dictionary<PeerId, DateTimeOffset> _speakingSince = [];
    private readonly ConcurrentDictionary<PeerId, IPeerSession> _observed = new();
    private readonly object _sync = new();

    private Timer? _timer;
    private RoomId? _roomId;
    private string _roomName = "";
    private bool _selfSpeaking;
    private DateTimeOffset _selfSpeakingAnnouncedAt;

    public RoomId? CurrentRoom
    {
        get
        {
            lock (_sync)
            {
                return _roomId;
            }
        }
    }

    public string RoomName
    {
        get
        {
            lock (_sync)
            {
                return _roomName;
            }
        }
    }

    public bool IsInRoom => CurrentRoom is not null;

    public IReadOnlyList<RoomMember> Members
    {
        get
        {
            lock (_sync)
            {
                return [.. _members.Values];
            }
        }
    }

    public IReadOnlyList<PeerId> RemoteMemberIds
    {
        get
        {
            lock (_sync)
            {
                return [.. _members.Values.Where(m => !m.IsSelf).Select(m => m.Id)];
            }
        }
    }

    public event Action? MembersChanged;
    public event Action<string>? Log;

    public RoomService(
        IIdentityStore identity,
        IDiscoveryService discovery,
        ISessionManager sessionManager,
        Func<DateTimeOffset>? clock = null)
    {
        _identity = identity;
        _discovery = discovery;
        _sessionManager = sessionManager;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sessionManager.SessionOpened += OnSessionOpened;
        _sessionManager.SessionClosed += OnSessionClosed;

        foreach (var session in _sessionManager.Sessions.Values)
        {
            OnSessionOpened(session);
        }

        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
        return Task.CompletedTask;
    }

    public RoomId CreateRoom(string name)
    {
        var room = RoomId.New();
        var sanitized = NicknameRules.Sanitize(name);

        lock (_sync)
        {
            _roomId = room;
            _roomName = sanitized.Length > 0 ? sanitized : "Sala";
            _members.Clear();
            _speakingSince.Clear();
            _members[_identity.Id] = CreateSelfMember();
        }

        MembersChanged?.Invoke();
        return room;
    }

    public async Task InviteAsync(PeerId peer, CancellationToken ct)
    {
        if (CurrentRoom is null)
        {
            CreateRoom("Sala");
        }

        if (!_sessionManager.Sessions.TryGetValue(peer, out var session))
        {
            var discovered = _discovery.Peers.FirstOrDefault(p => p.Id == peer)
                ?? throw new InvalidOperationException("O peer não está mais visível na rede.");

            session = await _sessionManager.ConnectAsync(discovered, ct).ConfigureAwait(false);
        }

        await SendAsync(session, MessageType.RoomInvite, EncodeMemberList()).ConfigureAwait(false);
    }

    public async Task LeaveAsync()
    {
        List<IPeerSession> targets;

        lock (_sync)
        {
            if (_roomId is null)
            {
                return;
            }

            targets = ResolveSessionsLocked();
            _roomId = null;
            _roomName = "";
            _members.Clear();
            _speakingSince.Clear();
            _selfSpeaking = false;
        }

        foreach (var session in targets)
        {
            await SendAsync(session, MessageType.RoomLeave, []).ConfigureAwait(false);
        }

        MembersChanged?.Invoke();
    }

    public async Task SetSpeakingAsync(bool speaking)
    {
        List<IPeerSession> targets;
        byte[] payload;

        lock (_sync)
        {
            if (_roomId is null || _selfSpeaking == speaking)
            {
                return;
            }

            _selfSpeaking = speaking;
            _selfSpeakingAnnouncedAt = _clock();

            if (_members.TryGetValue(_identity.Id, out var self))
            {
                self.IsSpeaking = speaking;
            }

            targets = ResolveSessionsLocked();
            payload = RoomSpeakingPayloadCodec.Encode(new RoomSpeakingPayload
            {
                RoomId = _roomId.Value,
                IsSpeaking = speaking,
            });
        }

        foreach (var session in targets)
        {
            await SendAsync(session, MessageType.RoomSpeaking, payload).ConfigureAwait(false);
        }

        MembersChanged?.Invoke();
    }

    public void SetSharingScreen(PeerId peer, bool sharing)
    {
        var changed = false;

        lock (_sync)
        {
            foreach (var member in _members.Values)
            {
                var value = member.Id == peer && sharing;

                if (member.IsSharingScreen != value)
                {
                    member.IsSharingScreen = value;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            MembersChanged?.Invoke();
        }
    }

    public void Tick()
    {
        var changed = false;
        var renew = false;
        List<IPeerSession> targets = [];
        byte[]? payload = null;

        lock (_sync)
        {
            if (_roomId is null)
            {
                return;
            }

            var now = _clock();

            foreach (var (id, since) in _speakingSince.ToArray())
            {
                if (now - since < SpeakingTimeout)
                {
                    continue;
                }

                _speakingSince.Remove(id);

                if (_members.TryGetValue(id, out var member) && member.IsSpeaking)
                {
                    member.IsSpeaking = false;
                    changed = true;
                }
            }

            if (_selfSpeaking && now - _selfSpeakingAnnouncedAt >= SpeakingRenewInterval)
            {
                _selfSpeakingAnnouncedAt = now;
                renew = true;
                targets = ResolveSessionsLocked();
                payload = RoomSpeakingPayloadCodec.Encode(new RoomSpeakingPayload
                {
                    RoomId = _roomId.Value,
                    IsSpeaking = true,
                });
            }
        }

        if (renew && payload is not null)
        {
            foreach (var session in targets)
            {
                _ = SendAsync(session, MessageType.RoomSpeaking, payload);
            }
        }

        if (changed)
        {
            MembersChanged?.Invoke();
        }
    }

    private void OnSessionOpened(IPeerSession session)
    {
        if (!_observed.TryAdd(session.RemoteId, session))
        {
            return;
        }

        session.MessageReceived += envelope => OnMessageReceived(session, envelope);
        session.StateChanged += state => OnPeerStateChanged(session.RemoteId, state);

        var isMember = false;

        lock (_sync)
        {
            if (_members.TryGetValue(session.RemoteId, out var member))
            {
                member.ConnectionState = session.State;
                isMember = true;
            }
        }

        if (isMember)
        {
            // A member that reconnected has no idea what changed while it was gone.
            _ = SendAsync(session, MessageType.RoomMemberList, EncodeMemberList());
            MembersChanged?.Invoke();
        }
    }

    private void OnPeerStateChanged(PeerId peer, SessionState state)
    {
        var changed = false;

        lock (_sync)
        {
            if (_members.TryGetValue(peer, out var member) && member.ConnectionState != state)
            {
                member.ConnectionState = state;
                changed = true;
            }
        }

        if (changed)
        {
            MembersChanged?.Invoke();
        }
    }

    private void OnSessionClosed(PeerId id)
    {
        _observed.TryRemove(id, out _);
        _ = RemoveMemberAndPropagateAsync(id);
    }

    private void OnMessageReceived(IPeerSession session, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.RoomInvite:
                _ = HandleInviteAsync(session, envelope);
                break;
            case MessageType.RoomJoin:
            case MessageType.RoomMemberList:
                _ = HandleMemberListAsync(session, envelope);
                break;
            case MessageType.RoomLeave:
                _ = RemoveMemberAndPropagateAsync(session.RemoteId);
                break;
            case MessageType.RoomSpeaking:
                HandleSpeaking(session, envelope);
                break;
        }
    }

    /// <summary>
    /// Invitations are accepted automatically: the app is meant for a LAN or a Radmin network the
    /// user already trusts, and a confirmation dialog would leave the inviter waiting with no
    /// feedback. The room only ever appears after someone on the network deliberately invited us.
    /// </summary>
    private async Task HandleInviteAsync(IPeerSession session, Envelope envelope)
    {
        if (!RoomMemberListPayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        lock (_sync)
        {
            if (_roomId?.Value != payload.RoomId)
            {
                _roomId = new RoomId(payload.RoomId);
                _roomName = payload.RoomName.Length > 0 ? payload.RoomName : "Sala";
                _members.Clear();
                _speakingSince.Clear();
                _members[_identity.Id] = CreateSelfMember();
            }
        }

        MergeMembers(payload, session);
        Log?.Invoke($"Entrou na sala \"{RoomName}\" a convite de {session.RemoteNickname}.");

        await SendAsync(session, MessageType.RoomJoin, EncodeMemberList()).ConfigureAwait(false);
        await AnnounceToOthersAsync(session.RemoteId).ConfigureAwait(false);

        MembersChanged?.Invoke();
    }

    private async Task HandleMemberListAsync(IPeerSession session, Envelope envelope)
    {
        if (!RoomMemberListPayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        lock (_sync)
        {
            if (_roomId?.Value != payload.RoomId)
            {
                return;
            }
        }

        if (!MergeMembers(payload, session))
        {
            MembersChanged?.Invoke();
            return;
        }

        // Only a merge that actually taught us something is retransmitted. The member set grows
        // monotonically until someone leaves, so this terminates instead of echoing forever.
        await AnnounceToOthersAsync(session.RemoteId).ConfigureAwait(false);
        MembersChanged?.Invoke();
    }

    private void HandleSpeaking(IPeerSession session, Envelope envelope)
    {
        if (!RoomSpeakingPayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        var changed = false;

        lock (_sync)
        {
            if (_roomId?.Value != payload.RoomId || !_members.TryGetValue(session.RemoteId, out var member))
            {
                return;
            }

            if (payload.IsSpeaking)
            {
                _speakingSince[session.RemoteId] = _clock();
            }
            else
            {
                _speakingSince.Remove(session.RemoteId);
            }

            if (member.IsSpeaking != payload.IsSpeaking)
            {
                member.IsSpeaking = payload.IsSpeaking;
                changed = true;
            }
        }

        if (changed)
        {
            MembersChanged?.Invoke();
        }
    }

    /// <summary>
    /// Additive by design: a member list never removes anyone. A peer that is slow to learn about a
    /// newcomer would otherwise evict live members every time it gossiped its stale view. Removal
    /// happens only on an explicit RoomLeave or a session that closed for good.
    /// </summary>
    private bool MergeMembers(RoomMemberListPayload payload, IPeerSession source)
    {
        var learned = new List<PeerId>();

        lock (_sync)
        {
            foreach (var info in payload.Members)
            {
                var id = new PeerId(info.PeerId);

                if (id == _identity.Id)
                {
                    continue;
                }

                if (_members.TryGetValue(id, out var existing))
                {
                    if (!string.Equals(existing.Nickname, info.Nickname, StringComparison.Ordinal))
                    {
                        existing.Nickname = info.Nickname;
                    }

                    continue;
                }

                _members[id] = new RoomMember
                {
                    Id = id,
                    Nickname = info.Nickname,
                    ConnectionState = _sessionManager.Sessions.TryGetValue(id, out var open)
                        ? open.State
                        : SessionState.Connecting,
                };

                learned.Add(id);
            }

            // The sender is a member by virtue of having spoken to us about the room.
            if (!_members.ContainsKey(source.RemoteId))
            {
                _members[source.RemoteId] = new RoomMember
                {
                    Id = source.RemoteId,
                    Nickname = NicknameRules.Sanitize(source.RemoteNickname) is { Length: > 0 } name
                        ? name
                        : NicknameRules.FallbackFor(source.RemoteId),
                    ConnectionState = source.State,
                };

                learned.Add(source.RemoteId);
            }
        }

        foreach (var peer in learned)
        {
            ConnectToMember(peer);
        }

        return learned.Count > 0;
    }

    /// <summary>
    /// Mesh membership means every member holds a session with every other one, so learning about a
    /// peer is also the trigger to dial it.
    /// </summary>
    private void ConnectToMember(PeerId peer)
    {
        if (peer == _identity.Id || _sessionManager.Sessions.ContainsKey(peer))
        {
            return;
        }

        var discovered = _discovery.Peers.FirstOrDefault(p => p.Id == peer);

        if (discovered is null)
        {
            Log?.Invoke($"Membro {peer} ainda não foi descoberto na rede.");
            return;
        }

        _ = ConnectQuietlyAsync(discovered);
    }

    private async Task ConnectQuietlyAsync(DiscoveredPeer peer)
    {
        try
        {
            await _sessionManager.ConnectAsync(peer, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Falha ao conectar com {peer.Nickname}: {ex.Message}");
        }
    }

    private async Task AnnounceToOthersAsync(PeerId except)
    {
        List<IPeerSession> targets;

        lock (_sync)
        {
            targets = ResolveSessionsLocked(except);
        }

        var payload = EncodeMemberList();

        foreach (var session in targets)
        {
            await SendAsync(session, MessageType.RoomMemberList, payload).ConfigureAwait(false);
        }
    }

    private async Task RemoveMemberAndPropagateAsync(PeerId peer)
    {
        List<IPeerSession> targets;

        lock (_sync)
        {
            if (_roomId is null || !_members.Remove(peer))
            {
                return;
            }

            _speakingSince.Remove(peer);
            targets = ResolveSessionsLocked(peer);
        }

        var payload = EncodeMemberList();

        foreach (var session in targets)
        {
            await SendAsync(session, MessageType.RoomMemberList, payload).ConfigureAwait(false);
        }

        MembersChanged?.Invoke();
    }

    private RoomMember CreateSelfMember() => new()
    {
        Id = _identity.Id,
        Nickname = NicknameRules.Sanitize(_identity.Nickname) is { Length: > 0 } name
            ? name
            : NicknameRules.FallbackFor(_identity.Id),
        ConnectionState = SessionState.Connected,
        IsSelf = true,
    };

    private List<IPeerSession> ResolveSessionsLocked(PeerId? except = null)
    {
        var sessions = new List<IPeerSession>();

        foreach (var member in _members.Values)
        {
            if (member.IsSelf || (except is not null && member.Id == except))
            {
                continue;
            }

            if (_sessionManager.Sessions.TryGetValue(member.Id, out var session))
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    private byte[] EncodeMemberList()
    {
        lock (_sync)
        {
            return RoomMemberListPayloadCodec.Encode(new RoomMemberListPayload
            {
                RoomId = _roomId?.Value ?? Guid.Empty,
                RoomName = _roomName,
                Members = [.. _members.Values.Select(m => new RoomMemberInfo
                {
                    PeerId = m.Id.PublicKeyBytes,
                    Nickname = m.Nickname,
                })],
            });
        }
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
            Log?.Invoke($"Falha ao enviar {type}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sessionManager.SessionOpened -= OnSessionOpened;
        _sessionManager.SessionClosed -= OnSessionClosed;

        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _timer = null;
        }

        await LeaveAsync().ConfigureAwait(false);
    }
}
