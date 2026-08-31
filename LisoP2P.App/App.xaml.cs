using System.IO;
using System.Windows;
using LisoP2P.App.ViewModels;
using LisoP2P.Core;
using LisoP2P.Net;
using LisoP2P.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LisoP2P.App;

public partial class App : Application
{
    private IServiceProvider _services = null!;
    private IDiscoveryService? _discovery;
    private ISessionManager? _sessionManager;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = ParseNetworkOptions(e.Args);
        var identityDirectory = GetIdentityDirectory(options);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IIdentityStore>(_ => new FileIdentityStore(identityDirectory));
        services.AddSingleton<IChatStore>(_ => new SqliteChatStore(Path.Combine(identityDirectory, "chat.db")));
        services.AddSingleton<IDiscoveryService, DiscoveryService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<ManualPeerConnector>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        _discovery = _services.GetRequiredService<IDiscoveryService>();
        await _discovery.StartAsync(CancellationToken.None);

        _sessionManager = _services.GetRequiredService<ISessionManager>();
        await _sessionManager.StartListeningAsync(CancellationToken.None);

        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_sessionManager is not null)
        {
            await _sessionManager.DisposeAsync();
        }

        if (_discovery is not null)
        {
            await _discovery.DisposeAsync();
        }

        base.OnExit(e);
    }

    private static string GetIdentityDirectory(NetworkOptions options)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LisoP2P");
        return options.SessionPort == NetworkOptions.DefaultSessionPort
            ? root
            : Path.Combine(root, options.SessionPort.ToString());
    }

    private static NetworkOptions ParseNetworkOptions(string[] args)
    {
        return new NetworkOptions
        {
            DiscoveryPort = ParseIntArg(args, "--discovery-port") ?? 47100,
            SessionPort = ParseIntArg(args, "--session-port") ?? 47101,
        };
    }

    private static int? ParseIntArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out var value))
            {
                return value;
            }
        }

        return null;
    }
}
