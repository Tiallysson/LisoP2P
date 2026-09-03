using System.Net;
using System.Net.Sockets;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;

namespace LisoP2P.Net;

public sealed class ManualPeerConnector(NetworkOptions options, IIdentityStore identity)
{
    public void Connect(IPAddress target)
    {
        var payload = new AnnouncePayload
        {
            Nickname = identity.Nickname,
            SessionPort = options.SessionPort,
            MediaPort = options.MediaPort,
        };

        var envelope = new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = MessageType.Announce,
            SenderId = identity.Id.Value,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = AnnouncePayloadCodec.Encode(payload),
        };

        var data = ProtocolCodec.Encode(envelope);

        using var socket = new UdpClient();
        socket.Send(data, data.Length, new IPEndPoint(target, options.DiscoveryPort));
    }
}
