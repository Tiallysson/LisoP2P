namespace LisoP2P.Media;

public sealed record CaptureSettings
{
    public int TargetFps { get; init; } = 30;
    public int TargetBitrateKbps { get; init; } = 5000;
    public int TargetHeight { get; init; } = 1080;
    public bool RecordToFile { get; init; }
    public string? RecordPath { get; init; }
    public RecordFormat RecordFormat { get; init; } = RecordFormat.Mp4;
    public bool ForceSoftwareEncoder { get; init; }
    public int PreviewFps { get; init; } = 30;
    public int PreviewWidth { get; init; } = 1280;

    public static string DefaultRecordPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LisoP2P",
        "capture-test.mp4");
}
