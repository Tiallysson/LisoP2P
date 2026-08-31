namespace LisoP2P.Net;

public sealed class NetworkOptions
{
    public int DiscoveryPort { get; init; } = 47100;
    public int SessionPort { get; init; } = 47101;
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(8);
}
