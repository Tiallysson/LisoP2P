namespace LisoP2P.Core.Diagnostics;

public interface IAppLogger
{
    string? FilePath { get; }

    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? error = null);
}
