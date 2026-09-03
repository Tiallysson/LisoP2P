using System.Net;

namespace LisoP2P.Core;

public sealed class DiscoveredPeer
{
    public DiscoveredPeer(
        PeerId id,
        string nickname,
        IPAddress address,
        int sessionPort,
        int mediaPort,
        DateTimeOffset lastSeen)
    {
        Id = id;
        Nickname = nickname;
        Address = address;
        SessionPort = sessionPort;
        MediaPort = mediaPort;
        LastSeen = lastSeen;
    }

    public PeerId Id { get; }
    public string Nickname { get; set; }
    public IPAddress Address { get; set; }
    public int SessionPort { get; set; }
    public int MediaPort { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
