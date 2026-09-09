using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class RoomMemberListPayloadCodec
{
    public const int MaxMembers = 16;
    public const int MaxRoomNameLength = 32;

    public static byte[] Encode(RoomMemberListPayload payload)
    {
        if (payload.Members.Count > MaxMembers)
        {
            throw new ArgumentException($"Member list exceeds {MaxMembers} entries.", nameof(payload));
        }

        return MessagePackSerializer.Serialize(payload);
    }

    /// <summary>
    /// Normalizes as it decodes: nicknames are sanitized, empty and duplicate ids are dropped, and
    /// an oversized list is rejected outright. The member list arrives from an open socket, so the
    /// rest of the app must never see an id or nickname it would not have produced itself.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out RoomMemberListPayload? payload)
    {
        payload = null;

        RoomMemberListPayload? decoded;

        try
        {
            decoded = MessagePackSerializer.Deserialize<RoomMemberListPayload>(data.ToArray());
        }
        catch
        {
            return false;
        }

        // A bare nil deserializes to a null payload, and MessagePack maps nil onto reference
        // properties too, so nothing off the wire can be assumed non-null just because the type
        // declares an initializer.
        if (decoded is null)
        {
            return false;
        }

        var incoming = decoded.Members ?? [];

        if (decoded.RoomId == Guid.Empty || incoming.Count > MaxMembers)
        {
            return false;
        }

        var seen = new HashSet<PeerId>();
        var members = new List<RoomMemberInfo>(incoming.Count);

        foreach (var member in incoming)
        {
            if (member is null || !PeerId.IsValidKey(member.PeerId))
            {
                continue;
            }

            var id = new PeerId(member.PeerId);

            if (!seen.Add(id))
            {
                continue;
            }

            var nickname = NicknameRules.Sanitize(member.Nickname);

            members.Add(new RoomMemberInfo
            {
                PeerId = member.PeerId,
                Nickname = nickname.Length > 0 ? nickname : NicknameRules.FallbackFor(id),
            });
        }

        var name = NicknameRules.Sanitize(decoded.RoomName);

        payload = new RoomMemberListPayload
        {
            RoomId = decoded.RoomId,
            Members = members,
            RoomName = name.Length > MaxRoomNameLength ? name[..MaxRoomNameLength] : name,
        };

        return true;
    }
}
