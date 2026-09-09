using System.Collections.Concurrent;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;

namespace LisoP2P.Net;

/// <summary>
/// There is no multicast between direct P2P connections, so a room broadcast is literally the same
/// message sent once per open session. Third parties never relay chat in this phase — only the
/// member list is gossiped — so every peer sends to every other peer it holds a session with.
/// </summary>
public sealed class RoomChatRouter : IRoomChatRouter
{
    private const int RecentMessageCapacity = 256;

    private readonly IIdentityStore _identity;
    private readonly ISessionManager _sessionManager;
    private readonly IRoomService _rooms;

    private readonly ConcurrentDictionary<PeerId, IPeerSession> _observed = new();
    private readonly HashSet<Guid> _recent = [];
    private readonly Queue<Guid> _recentOrder = new();
    private readonly object _sync = new();

    public event Action<PeerId, ChatMessagePayload>? MessageReceived;

    public RoomChatRouter(IIdentityStore identity, ISessionManager sessionManager, IRoomService rooms)
    {
        _identity = identity;
        _sessionManager = sessionManager;
        _rooms = rooms;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sessionManager.SessionOpened += OnSessionOpened;
        _sessionManager.SessionClosed += id => _observed.TryRemove(id, out _);

        foreach (var session in _sessionManager.Sessions.Values)
        {
            OnSessionOpened(session);
        }

        return Task.CompletedTask;
    }

    public async Task<Guid> BroadcastAsync(RoomId room, string text, CancellationToken ct)
    {
        var messageId = Guid.NewGuid();

        var payload = ChatMessagePayloadCodec.Encode(new ChatMessagePayload
        {
            MessageId = messageId,
            Text = text,
            RoomId = room.Value,
        });

        var envelope = new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = MessageType.ChatMessage,
            SenderId = _identity.Id.PublicKeyBytes,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload,
        };

        Remember(messageId);

        foreach (var peer in _rooms.RemoteMemberIds)
        {
            if (!_sessionManager.Sessions.TryGetValue(peer, out var session))
            {
                continue;
            }

            try
            {
                await session.SendAsync(envelope, ct).ConfigureAwait(false);
            }
            catch
            {
                // A member that is momentarily unreachable must not block delivery to the others.
            }
        }

        return messageId;
    }

    private void OnSessionOpened(IPeerSession session)
    {
        if (!_observed.TryAdd(session.RemoteId, session))
        {
            return;
        }

        session.MessageReceived += envelope => OnMessageReceived(session, envelope);
    }

    private void OnMessageReceived(IPeerSession session, Envelope envelope)
    {
        if (envelope.Type != MessageType.ChatMessage)
        {
            return;
        }

        if (!ChatMessagePayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        if (payload.RoomId == Guid.Empty || _rooms.CurrentRoom?.Value != payload.RoomId)
        {
            return;
        }

        if (!Remember(payload.MessageId))
        {
            return;
        }

        MessageReceived?.Invoke(session.RemoteId, payload);
    }

    /// <summary>Returns false when the message was already seen.</summary>
    private bool Remember(Guid messageId)
    {
        lock (_sync)
        {
            if (!_recent.Add(messageId))
            {
                return false;
            }

            _recentOrder.Enqueue(messageId);

            while (_recentOrder.Count > RecentMessageCapacity)
            {
                _recent.Remove(_recentOrder.Dequeue());
            }

            return true;
        }
    }
}
