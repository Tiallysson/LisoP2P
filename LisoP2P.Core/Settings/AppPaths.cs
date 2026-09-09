namespace LisoP2P.Core.Settings;

public static class AppPaths
{
    public const string ProductFolder = "LisoP2P";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductFolder);

    /// <summary>
    /// Where identity.json, settings.json, chat.db and the logs live.
    /// <para>
    /// Two dev instances on one machine are told apart by --session-port alone; without nesting
    /// they would share identity.json, come up with the same key pair, and filter each other out
    /// of discovery as "self". The nesting keys off the <b>flag</b>, never off the effective port:
    /// a port changed in settings.json must not move the identity file and hand the user a new
    /// identity.
    /// </para>
    /// </summary>
    public static string ResolveDataDirectory(string root, IReadOnlyList<string> args)
    {
        var sessionPort = PortResolver.ParsePort(args, PortResolver.SessionPortArg);

        return sessionPort is { } port && port != NetworkPorts.DefaultSessionPort
            ? Path.Combine(root, port.ToString())
            : root;
    }

    public static string ResolveDataDirectory(IReadOnlyList<string> args) =>
        ResolveDataDirectory(DefaultRoot, args);
}
