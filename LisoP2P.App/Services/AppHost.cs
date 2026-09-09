using LisoP2P.App.ViewModels;
using LisoP2P.Core;
using LisoP2P.Core.Diagnostics;
using LisoP2P.Core.Settings;
using LisoP2P.Media;
using LisoP2P.Net;
using LisoP2P.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LisoP2P.App.Services;

/// <summary>
/// Everything that is bound to a set of ports. Changing a port in Configurações → Rede means
/// tearing this down and building it again — sockets are opened in constructors and the sessions
/// on them cannot survive the move — while identity, settings, chat history, the capture pipeline
/// and the notification banner live in the root provider and outlive the restart.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public IServiceProvider Services => _provider;
    public NetworkOptions Options { get; }

    private AppHost(ServiceProvider provider, NetworkOptions options)
    {
        _provider = provider;
        Options = options;
    }

    public static AppHost Build(IServiceProvider root, NetworkOptions options)
    {
        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddSingleton(root.GetRequiredService<IIdentityStore>());
        services.AddSingleton(root.GetRequiredService<IAppSettingsStore>());
        services.AddSingleton(root.GetRequiredService<IAppLogger>());
        services.AddSingleton(root.GetRequiredService<IErrorPresenter>());
        services.AddSingleton(root.GetRequiredService<IChatStore>());
        services.AddSingleton(root.GetRequiredService<IMediaLogger>());
        services.AddSingleton(root.GetRequiredService<IScreenCapture>());
        services.AddSingleton(root.GetRequiredService<ICapturePipeline>());
        services.AddSingleton(root.GetRequiredService<IAudioDeviceCatalog>());

        services.AddSingleton<IDiscoveryService, DiscoveryService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<ManualPeerConnector>();
        services.AddSingleton<IMediaSender, UdpMediaSender>();
        services.AddSingleton<IMediaReceiver, UdpMediaReceiver>();
        services.AddSingleton<IScreenShareSession, ScreenShareSession>();
        services.AddSingleton<IAudioCapture, WasapiAudioCapture>();
        services.AddSingleton<IAudioPlayback, WasapiAudioPlayback>();
        services.AddSingleton<IVoiceSession, VoiceSession>();
        services.AddSingleton<IRoomService, RoomService>();
        services.AddSingleton<IRoomChatRouter, RoomChatRouter>();
        services.AddSingleton<MainViewModel>();

        return new AppHost(services.BuildServiceProvider(), options);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _provider.GetRequiredService<IDiscoveryService>().StartAsync(ct).ConfigureAwait(true);
        await _provider.GetRequiredService<ISessionManager>().StartListeningAsync(ct).ConfigureAwait(true);
        await _provider.GetRequiredService<IScreenShareSession>().StartAsync(ct).ConfigureAwait(true);
        await _provider.GetRequiredService<IVoiceSession>().StartAsync(ct).ConfigureAwait(true);
        await _provider.GetRequiredService<IRoomService>().StartAsync(ct).ConfigureAwait(true);
        await _provider.GetRequiredService<IRoomChatRouter>().StartAsync(ct).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        // Reverse of StartAsync: the room announces its exit over sessions that must still be open.
        await SafeAsync(_provider.GetRequiredService<IRoomService>().DisposeAsync()).ConfigureAwait(true);
        await SafeAsync(_provider.GetRequiredService<IVoiceSession>().DisposeAsync()).ConfigureAwait(true);
        await SafeAsync(_provider.GetRequiredService<IScreenShareSession>().DisposeAsync()).ConfigureAwait(true);
        await SafeAsync(_provider.GetRequiredService<ISessionManager>().DisposeAsync()).ConfigureAwait(true);
        await SafeAsync(_provider.GetRequiredService<IDiscoveryService>().DisposeAsync()).ConfigureAwait(true);

        _provider.GetRequiredService<MainViewModel>().Dispose();

        await _provider.DisposeAsync().ConfigureAwait(true);
    }

    private static async ValueTask SafeAsync(ValueTask task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch
        {
            // Shutting down is best effort: one service that refuses to stop must not strand the
            // others, least of all during a port change the user is waiting on.
        }
    }
}
