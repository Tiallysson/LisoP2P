using LisoP2P.Core.Diagnostics;

namespace LisoP2P.Media;

/// <summary>
/// media.log, kept apart from app.log: an encoder problem is read by looking at capture start,
/// adapters and MFT enumeration in one continuous file, not interleaved with session errors.
/// </summary>
public sealed class FileMediaLogger(string path) : IMediaLogger
{
    private readonly FileAppLogger _log = new(path);

    public string? FilePath => _log.FilePath;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LisoP2P",
        "logs",
        "media.log");

    public void Info(string message) => _log.Info(message);

    public void Error(string message, Exception? error = null) => _log.Error(message, error);
}
