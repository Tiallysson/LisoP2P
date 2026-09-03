namespace LisoP2P.Net;

public sealed class NetworkOptions
{
    public const int DefaultDiscoveryPort = 47100;
    public const int DefaultSessionPort = 47101;
    public const int DefaultMediaPort = 47102;

    public int DiscoveryPort { get; init; } = DefaultDiscoveryPort;
    public int SessionPort { get; init; } = DefaultSessionPort;
    public int MediaPort { get; init; } = DefaultMediaPort;
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(8);
}
