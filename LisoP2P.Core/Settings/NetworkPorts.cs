namespace LisoP2P.Core.Settings;

public static class NetworkPorts
{
    public const int DefaultDiscoveryPort = 47100;
    public const int DefaultSessionPort = 47101;
    public const int DefaultMediaPort = 47102;

    /// <summary>
    /// Ports below 1024 need elevation on Windows and belong to services the user did not choose,
    /// so the settings screen refuses them rather than failing to bind later.
    /// </summary>
    public const int MinPort = 1024;

    public const int MaxPort = 65535;

    public static bool IsValid(int port) => port is >= MinPort and <= MaxPort;
}
