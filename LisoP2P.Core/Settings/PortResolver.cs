namespace LisoP2P.Core.Settings;

public sealed record ResolvedPorts(int DiscoveryPort, int SessionPort, int MediaPort);

/// <summary>
/// Command line beats settings.json beats the compiled default. The order exists because the fase
/// 0/1 acceptance test runs two instances on one machine, told apart only by --session-port; a
/// settings file must never be able to override that.
/// </summary>
public static class PortResolver
{
    public const string DiscoveryPortArg = "--discovery-port";
    public const string SessionPortArg = "--session-port";
    public const string MediaPortArg = "--media-port";

    public static ResolvedPorts Resolve(AppSettings? stored, IReadOnlyList<string> args)
    {
        var settings = (stored ?? new AppSettings()).Normalized();

        var discovery = ParsePort(args, DiscoveryPortArg);
        var session = ParsePort(args, SessionPortArg);
        var media = ParsePort(args, MediaPortArg);

        var sessionPort = session ?? settings.SessionPort;

        return new ResolvedPorts(
            discovery ?? settings.DiscoveryPort,
            sessionPort,
            media ?? ResolveMediaPort(session, settings));
    }

    /// <summary>
    /// A second instance on the same machine is distinguished by --session-port alone, so its
    /// media port is derived from it: without this the two would fight over the same UDP socket.
    /// Only the command line derives — a session port that came from settings.json keeps the media
    /// port that came from settings.json.
    /// </summary>
    private static int ResolveMediaPort(int? sessionFromArgs, AppSettings settings)
    {
        if (sessionFromArgs is not { } session || session == settings.SessionPort)
        {
            return settings.MediaPort;
        }

        var derived = session + 1;
        return NetworkPorts.IsValid(derived) ? derived : settings.MediaPort;
    }

    public static int? ParsePort(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out var value) && NetworkPorts.IsValid(value))
            {
                return value;
            }
        }

        return null;
    }
}
