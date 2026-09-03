namespace LisoP2P.Media;

public sealed class NullMediaLogger : IMediaLogger
{
    public static readonly NullMediaLogger Instance = new();

    public string? FilePath => null;

    public void Info(string message)
    {
    }

    public void Error(string message, Exception? error = null)
    {
    }
}
