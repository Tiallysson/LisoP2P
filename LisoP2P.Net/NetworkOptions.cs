namespace LisoP2P.Net;

public sealed class NetworkOptions
{
    public const int DefaultDiscoveryPort = 47100;
    public const int DefaultSessionPort = 47101;

    public int DiscoveryPort { get; init; } = DefaultDiscoveryPort;
    public int SessionPort { get; init; } = DefaultSessionPort;
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(8);
}
