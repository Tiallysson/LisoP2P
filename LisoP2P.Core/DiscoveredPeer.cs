using System.Net;

namespace LisoP2P.Core;

public sealed class DiscoveredPeer
{
    public DiscoveredPeer(PeerId id, string nickname, IPAddress address, int sessionPort, DateTimeOffset lastSeen)
    {
        Id = id;
        Nickname = nickname;
        Address = address;
        SessionPort = sessionPort;
        LastSeen = lastSeen;
    }

    public PeerId Id { get; }
    public string Nickname { get; set; }
    public IPAddress Address { get; set; }
    public int SessionPort { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
