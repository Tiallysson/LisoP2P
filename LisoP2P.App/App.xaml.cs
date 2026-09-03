using System.IO;
using System.Windows;
using LisoP2P.App.ViewModels;
using LisoP2P.Core;
using LisoP2P.Media;
using LisoP2P.Net;
using LisoP2P.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LisoP2P.App;

public partial class App : Application
{
    private IServiceProvider _services = null!;
    private IDiscoveryService? _discovery;
    private ISessionManager? _sessionManager;
    private IScreenShareSession? _screenShare;
    private IVoiceSession? _voice;

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
        services.AddSingleton<IMediaLogger>(_ => new FileMediaLogger(Path.Combine(identityDirectory, "logs", "media.log")));
        services.AddSingleton<IScreenCapture, DxgiScreenCapture>();
        services.AddSingleton<ICapturePipeline, CapturePipeline>();
        services.AddSingleton<IMediaSender, UdpMediaSender>();
        services.AddSingleton<IMediaReceiver, UdpMediaReceiver>();
        services.AddSingleton<IScreenShareSession, ScreenShareSession>();
        services.AddSingleton<IAudioCapture, WasapiAudioCapture>();
        services.AddSingleton<IAudioPlayback, WasapiAudioPlayback>();
        services.AddSingleton<IAudioDeviceCatalog, WasapiAudioDeviceCatalog>();
        services.AddSingleton<IVoiceSession, VoiceSession>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<CaptureTestViewModel>();
        services.AddTransient<CaptureTestWindow>();
        services.AddSingleton<Func<CaptureTestWindow>>(provider => provider.GetRequiredService<CaptureTestWindow>);
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        _discovery = _services.GetRequiredService<IDiscoveryService>();
        await _discovery.StartAsync(CancellationToken.None);

        _sessionManager = _services.GetRequiredService<ISessionManager>();
        await _sessionManager.StartListeningAsync(CancellationToken.None);

        _screenShare = _services.GetRequiredService<IScreenShareSession>();
        await _screenShare.StartAsync(CancellationToken.None);

        _voice = _services.GetRequiredService<IVoiceSession>();
        await _voice.StartAsync(CancellationToken.None);

        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            await _services.GetRequiredService<ICapturePipeline>().DisposeAsync();
            _services.GetRequiredService<IScreenCapture>().Dispose();
            MediaFoundationRuntime.Shutdown();
        }

        if (_voice is not null)
        {
            await _voice.DisposeAsync();
        }

        if (_screenShare is not null)
        {
            await _screenShare.DisposeAsync();
        }

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
            MediaPort = ParseIntArg(args, "--media-port") ?? ResolveDefaultMediaPort(args),
        };
    }

    /// <summary>
    /// Two instances on the same machine are told apart by --session-port; deriving the media
    /// port from it keeps the second instance from fighting the first for the UDP port.
    /// </summary>
    private static int ResolveDefaultMediaPort(string[] args)
    {
        var sessionPort = ParseIntArg(args, "--session-port") ?? NetworkOptions.DefaultSessionPort;

        return sessionPort == NetworkOptions.DefaultSessionPort
            ? NetworkOptions.DefaultMediaPort
            : sessionPort + 1;
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
