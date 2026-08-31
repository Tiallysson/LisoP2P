using System.Windows;
using LisoP2P.App.ViewModels;
using LisoP2P.Core;
using LisoP2P.Net;
using Microsoft.Extensions.DependencyInjection;

namespace LisoP2P.App;

public partial class App : Application
{
    private IServiceProvider _services = null!;
    private IDiscoveryService? _discovery;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = ParseNetworkOptions(e.Args);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IIdentityStore, FileIdentityStore>();
        services.AddSingleton<IDiscoveryService, DiscoveryService>();
        services.AddSingleton<ManualPeerConnector>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        _discovery = _services.GetRequiredService<IDiscoveryService>();
        await _discovery.StartAsync(CancellationToken.None);

        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_discovery is not null)
        {
            await _discovery.DisposeAsync();
        }

        base.OnExit(e);
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
