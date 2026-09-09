using LisoP2P.App.ViewModels;
using LisoP2P.Core;

namespace LisoP2P.App.Services;

/// <summary>
/// What the main window is allowed to ask of the application: the view model it should bind to
/// (which changes when the network stack is rebuilt), the windows it can open, and the request to
/// apply a new set of ports.
/// </summary>
public interface IAppShell
{
    MainViewModel MainViewModel { get; }
    NotificationCenter Notifications { get; }

    /// <summary>Raised after a port change replaced the network stack and the view models with it.</summary>
    event Action? Rebuilt;

    CaptureTestWindow CreateCaptureTestWindow();
    RoomWindow CreateRoomWindow();
    SettingsWindow CreateSettingsWindow();

    /// <summary>
    /// Tears down and rebuilds every service bound to a port. Open sessions are closed by this —
    /// the settings screen warns before calling it.
    /// </summary>
    Task<bool> RestartNetworkAsync();

    /// <summary>
    /// Applies audio device choices to the live voice session. Devices are the one setting that
    /// takes effect without a restart — switching the output mid-call is an acceptance item.
    /// </summary>
    void ApplyAudioDevices(string? inputDeviceId, string? outputDeviceId, AudioCaptureMode mode);
}
