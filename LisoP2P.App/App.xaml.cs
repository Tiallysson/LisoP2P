using System.IO;
using System.Windows;
using LisoP2P.App.Services;
using LisoP2P.App.ViewModels;
using LisoP2P.Core;
using LisoP2P.Core.Diagnostics;
using LisoP2P.Core.Settings;
using LisoP2P.Media;
using LisoP2P.Net;
using LisoP2P.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LisoP2P.App;

public partial class App : Application, IAppShell
{
    private ServiceProvider _root = null!;
    private AppHost _host = null!;
    private FileAppLogger _log = null!;
    private FileAppSettingsStore _settings = null!;
    private FileIdentityStore _identity = null!;
    private NotificationCenter _notifications = null!;
    private string[] _args = [];
    private string _dataDirectory = "";

    public MainViewModel MainViewModel => _host.Services.GetRequiredService<MainViewModel>();
    public NotificationCenter Notifications => _notifications;

    public event Action? Rebuilt;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Welcome and the port-conflict screen are dialogs shown before the main window exists;
        // with the default OnLastWindowClose the app would quit the moment one of them closes.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _args = e.Args;
        _dataDirectory = AppPaths.ResolveDataDirectory(_args);

        // The single-file executable may be the first thing that ever ran on this machine, so the
        // data folder is created here rather than assumed to exist from a previous `dotnet run`.
        Directory.CreateDirectory(_dataDirectory);

        _log = new FileAppLogger(Path.Combine(_dataDirectory, "app.log"));
        _notifications = new NotificationCenter(_log);

        try
        {
            _identity = new FileIdentityStore(_dataDirectory);
            _settings = new FileAppSettingsStore(_dataDirectory);
        }
        catch (Exception ex)
        {
            _log.Error("Falha ao carregar identidade ou configurações.", ex);
            MessageBox.Show(
                $"Não foi possível ler os arquivos em {_dataDirectory}.\n\n{ex.Message}",
                "LisoP2P",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _log.Info($"Iniciando. Dados em {_dataDirectory}. Fingerprint {_identity.Fingerprint}.");

        _root = BuildRootServices();

        if (_identity.IsFirstRun && !ShowWelcome())
        {
            Shutdown();
            return;
        }

        if (!await StartNetworkAsync().ConfigureAwait(true))
        {
            Shutdown();
            return;
        }

        if (_identity.MigratedFromLegacyIdentity)
        {
            _notifications.ShowPersistent(
                NotificationKey.IdentityMigrated,
                "Sua identidade foi recriada nesta versão: a chave antiga (fases 0-5) não pode ser " +
                "convertida. Os peers que já conheciam você verão um novo contato.",
                "Entendi",
                () => _notifications.Dismiss(NotificationKey.IdentityMigrated));
        }

        var window = new MainWindow(this);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private ServiceProvider BuildRootServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IIdentityStore>(_identity);
        services.AddSingleton<IAppSettingsStore>(_settings);
        services.AddSingleton<IAppLogger>(_log);
        services.AddSingleton<IErrorPresenter>(_notifications);
        services.AddSingleton(_notifications);
        services.AddSingleton<IAppShell>(this);
        services.AddSingleton<IChatStore>(_ => new SqliteChatStore(Path.Combine(_dataDirectory, "chat.db")));
        services.AddSingleton<IMediaLogger>(_ => new FileMediaLogger(Path.Combine(_dataDirectory, "logs", "media.log")));
        services.AddSingleton<IScreenCapture, DxgiScreenCapture>();
        services.AddSingleton<ICapturePipeline, CapturePipeline>();
        services.AddSingleton<IAudioDeviceCatalog, WasapiAudioDeviceCatalog>();
        services.AddTransient<CaptureTestViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Resolves the ports, refuses to continue while one of them is taken, and brings the network
    /// stack up. Returns false only when the user chose to give up on the port conflict screen.
    /// </summary>
    private async Task<bool> StartNetworkAsync()
    {
        while (true)
        {
            var ports = PortResolver.Resolve(_settings.Current, _args);
            var conflicts = PortProbe.CheckAll(ports.DiscoveryPort, ports.SessionPort, ports.MediaPort)
                .Where(check => !check.IsAvailable)
                .ToList();

            if (conflicts.Count == 0)
            {
                _host = AppHost.Build(_root, NetworkOptions.From(ports));

                try
                {
                    await _host.StartAsync(CancellationToken.None).ConfigureAwait(true);
                    _log.Info($"Rede ativa: descoberta {ports.DiscoveryPort}, sessão {ports.SessionPort}, mídia {ports.MediaPort}.");
                    return true;
                }
                catch (Exception ex)
                {
                    // The probe is a snapshot, not a reservation: another process can take the
                    // port between the check and the real bind.
                    _log.Error("Falha ao subir a rede.", ex);
                    await _host.DisposeAsync().ConfigureAwait(true);

                    if (!ShowPortConflict([new PortCheck(ports.SessionPort, "rede", false, ex.Message)]))
                    {
                        return false;
                    }

                    continue;
                }
            }

            foreach (var conflict in conflicts)
            {
                _log.Error($"Porta de {conflict.Role} {conflict.Port} indisponível ({conflict.Detail}).");
            }

            if (!ShowPortConflict(conflicts))
            {
                return false;
            }
        }
    }

    private bool ShowPortConflict(IReadOnlyList<PortCheck> conflicts)
    {
        var window = new StartupErrorWindow(conflicts, OpenSettingsDialog);
        window.ShowDialog();
        return window.ShouldRetry;
    }

    private bool ShowWelcome()
    {
        var window = new WelcomeWindow(new WelcomeViewModel(_identity, _settings));
        return window.ShowDialog() == true;
    }

    private void OpenSettingsDialog()
    {
        var window = CreateSettingsWindow();
        window.ShowDialog();
    }

    public CaptureTestWindow CreateCaptureTestWindow() =>
        new(_root.GetRequiredService<CaptureTestViewModel>());

    public SettingsWindow CreateSettingsWindow() =>
        new(_root.GetRequiredService<SettingsViewModel>());

    public void ApplyAudioDevices(string? inputDeviceId, string? outputDeviceId, AudioCaptureMode mode)
    {
        try
        {
            _host.Services.GetRequiredService<IVoiceSession>().UpdateDevices(inputDeviceId, outputDeviceId, mode);
        }
        catch (Exception ex)
        {
            _log.Error("Falha ao aplicar os dispositivos de áudio.", ex);
            _notifications.ShowTransient($"Não foi possível trocar o dispositivo de áudio: {ex.Message}", ErrorSeverity.Warning);
        }
    }

    public async Task<bool> RestartNetworkAsync()
    {
        _log.Info("Reiniciando a rede após mudança de portas.");

        await _host.DisposeAsync().ConfigureAwait(true);

        if (!await StartNetworkAsync().ConfigureAwait(true))
        {
            Shutdown();
            return false;
        }

        Rebuilt?.Invoke();
        _notifications.ShowTransient("Rede reiniciada com as novas portas.", ErrorSeverity.Info);
        return true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        if (_root is not null)
        {
            await _root.GetRequiredService<ICapturePipeline>().DisposeAsync();
            _root.GetRequiredService<IScreenCapture>().Dispose();
            MediaFoundationRuntime.Shutdown();
            await _root.DisposeAsync();
        }

        _log?.Info("Encerrado.");

        base.OnExit(e);
    }
}
