using MessagePack;

namespace LisoP2P.Core.Settings;

/// <summary>
/// Everything the user can change. Kept apart from identity.json on purpose: the key pair is the
/// identity and never changes, while these change whenever the user wants them to.
/// </summary>
[MessagePackObject]
public sealed class AppSettings
{
    public const string NativeResolution = "Native";

    [Key(0)] public int DiscoveryPort { get; set; } = NetworkPorts.DefaultDiscoveryPort;
    [Key(1)] public int SessionPort { get; set; } = NetworkPorts.DefaultSessionPort;
    [Key(2)] public int MediaPort { get; set; } = NetworkPorts.DefaultMediaPort;
    [Key(3)] public int PreferredMonitorIndex { get; set; }
    [Key(4)] public string CaptureResolution { get; set; } = NativeResolution;
    [Key(5)] public int CaptureFps { get; set; } = 30;

    /// <summary>Null means "whatever Windows calls the default device".</summary>
    [Key(6)] public string? AudioInputDeviceId { get; set; }

    [Key(7)] public string? AudioOutputDeviceId { get; set; }
    [Key(8)] public AudioCaptureMode AudioMode { get; set; } = AudioCaptureMode.Microphone;
    [Key(9)] public bool PushToTalkEnabled { get; set; } = true;
    [Key(10)] public string PushToTalkKey { get; set; } = "LeftCtrl";

    /// <summary>
    /// A mirror of the nickname, so settings.json describes the whole configuration on its own.
    /// identity.json stays authoritative — it is what raises NicknameChanged and what the wire
    /// carries — and this field is written from it, never read back as the source of truth.
    /// </summary>
    [Key(11)] public string Nickname { get; set; } = "";

    public AppSettings Clone() => new()
    {
        DiscoveryPort = DiscoveryPort,
        SessionPort = SessionPort,
        MediaPort = MediaPort,
        PreferredMonitorIndex = PreferredMonitorIndex,
        CaptureResolution = CaptureResolution,
        CaptureFps = CaptureFps,
        AudioInputDeviceId = AudioInputDeviceId,
        AudioOutputDeviceId = AudioOutputDeviceId,
        AudioMode = AudioMode,
        PushToTalkEnabled = PushToTalkEnabled,
        PushToTalkKey = PushToTalkKey,
        Nickname = Nickname,
    };

    /// <summary>
    /// A copy with every out-of-range or malformed value replaced by its default. settings.json is
    /// hand-editable, so a bad value has to mean "use the default", never "refuse to start".
    /// </summary>
    public AppSettings Normalized()
    {
        var defaults = new AppSettings();
        var copy = Clone();

        copy.DiscoveryPort = NetworkPorts.IsValid(DiscoveryPort) ? DiscoveryPort : defaults.DiscoveryPort;
        copy.SessionPort = NetworkPorts.IsValid(SessionPort) ? SessionPort : defaults.SessionPort;
        copy.MediaPort = NetworkPorts.IsValid(MediaPort) ? MediaPort : defaults.MediaPort;
        copy.PreferredMonitorIndex = PreferredMonitorIndex >= 0 ? PreferredMonitorIndex : 0;
        copy.CaptureResolution = ParseResolutionHeight(CaptureResolution) is null
            ? defaults.CaptureResolution
            : CaptureResolution;
        copy.CaptureFps = CaptureFps is >= 1 and <= 240 ? CaptureFps : defaults.CaptureFps;
        copy.AudioMode = Enum.IsDefined(AudioMode) ? AudioMode : defaults.AudioMode;
        copy.PushToTalkKey = string.IsNullOrWhiteSpace(PushToTalkKey) ? defaults.PushToTalkKey : PushToTalkKey.Trim();
        copy.Nickname = NicknameRules.Sanitize(Nickname);
        copy.AudioInputDeviceId = string.IsNullOrWhiteSpace(AudioInputDeviceId) ? null : AudioInputDeviceId;
        copy.AudioOutputDeviceId = string.IsNullOrWhiteSpace(AudioOutputDeviceId) ? null : AudioOutputDeviceId;

        return copy;
    }

    /// <summary>
    /// The capture height the pipeline should target: 0 for native, as CaptureSettings spells it.
    /// Null when the string is not a resolution at all.
    /// </summary>
    public static int? ParseResolutionHeight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (string.Equals(text, NativeResolution, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var separator = text.IndexOf('x', StringComparison.OrdinalIgnoreCase);

        if (separator < 0)
        {
            return int.TryParse(text, out var height) && height is > 0 and <= 8192 ? height : null;
        }

        return int.TryParse(text.AsSpan(separator + 1), out var parsed) && parsed is > 0 and <= 8192
            ? parsed
            : null;
    }
}
