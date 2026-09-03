namespace LisoP2P.Media;

public interface IMediaLogger
{
    string? FilePath { get; }

    void Info(string message);
    void Error(string message, Exception? error = null);
}
