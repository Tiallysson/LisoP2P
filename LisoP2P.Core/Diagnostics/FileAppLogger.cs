using System.Globalization;
using System.Text;

namespace LisoP2P.Core.Diagnostics;

/// <summary>
/// A plain rolling text log. Deliberately not telemetry and not structured: it exists so a problem
/// reported during real use can be read back, and it must never be the reason a write fails.
/// </summary>
public sealed class FileAppLogger : IAppLogger
{
    public const long DefaultMaxFileBytes = 5 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly string _path;
    private readonly long _maxBytes;

    public string? FilePath => _path;

    public FileAppLogger(string path, long maxBytes = DefaultMaxFileBytes)
    {
        _path = path;
        _maxBytes = maxBytes;

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        RollIfTooLarge();
    }

    public void Info(string message) => Write("INFO ", message, null);

    public void Warning(string message) => Write("AVISO ", message, null);

    public void Error(string message, Exception? error = null) => Write("ERRO ", message, error);

    private void Write(string level, string message, Exception? error)
    {
        var builder = new StringBuilder();
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(level);
        builder.Append(message);

        if (error is not null)
        {
            builder.AppendLine();
            builder.Append(error);
        }

        var line = builder.ToString();

        lock (_sync)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RollIfTooLarge()
    {
        try
        {
            var info = new FileInfo(_path);

            if (!info.Exists || info.Length < _maxBytes)
            {
                return;
            }

            var previous = _path + ".1";
            File.Delete(previous);
            File.Move(_path, previous);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
