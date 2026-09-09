using LisoP2P.Core.Settings;

namespace LisoP2P.Net;

public sealed class NetworkOptions
{
    public const int DefaultDiscoveryPort = NetworkPorts.DefaultDiscoveryPort;
    public const int DefaultSessionPort = NetworkPorts.DefaultSessionPort;
    public const int DefaultMediaPort = NetworkPorts.DefaultMediaPort;

    public int DiscoveryPort { get; init; } = DefaultDiscoveryPort;
    public int SessionPort { get; init; } = DefaultSessionPort;
    public int MediaPort { get; init; } = DefaultMediaPort;
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public static NetworkOptions From(ResolvedPorts ports) => new()
    {
        DiscoveryPort = ports.DiscoveryPort,
        SessionPort = ports.SessionPort,
        MediaPort = ports.MediaPort,
    };
}
