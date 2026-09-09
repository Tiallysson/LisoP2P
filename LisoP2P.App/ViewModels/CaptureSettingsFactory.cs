using LisoP2P.Core.Settings;
using LisoP2P.Media;

namespace LisoP2P.App.ViewModels;

public static class CaptureSettingsFactory
{
    /// <summary>
    /// Turns the stored preferences into what the pipeline expects. The bitrate is left at the
    /// default because the bitrate ladder overrides it from the receiver count anyway — the
    /// settings screen shows the ladder read-only for the same reason.
    /// </summary>
    public static CaptureSettings From(AppSettings settings) => new()
    {
        TargetFps = settings.CaptureFps,
        TargetHeight = AppSettings.ParseResolutionHeight(settings.CaptureResolution) ?? 1080,
    };
}
