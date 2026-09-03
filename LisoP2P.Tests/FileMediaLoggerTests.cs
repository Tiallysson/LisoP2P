using LisoP2P.Media;

namespace LisoP2P.Tests;

public class FileMediaLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lisop2p-log-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_directory, "logs", "media.log");

    [Fact]
    public void Info_CreatesFileAndAppendsLines()
    {
        var logger = new FileMediaLogger(LogPath);

        logger.Info("primeira");
        logger.Info("segunda");

        var lines = File.ReadAllLines(LogPath);

        Assert.Equal(2, lines.Length);
        Assert.Contains("INFO primeira", lines[0], StringComparison.Ordinal);
        Assert.Contains("INFO segunda", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Error_WritesExceptionDetails()
    {
        var logger = new FileMediaLogger(LogPath);

        logger.Error("encoder falhou", new InvalidOperationException("MF_E_TRANSFORM_ASYNC_LOCKED"));

        var content = File.ReadAllText(LogPath);

        Assert.Contains("ERRO encoder falhou", content, StringComparison.Ordinal);
        Assert.Contains("MF_E_TRANSFORM_ASYNC_LOCKED", content, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), content, StringComparison.Ordinal);
    }

    [Fact]
    public void FilePath_ExposesTargetFile()
    {
        var logger = new FileMediaLogger(LogPath);

        Assert.Equal(LogPath, logger.FilePath);
    }

    [Fact]
    public void ConcurrentWrites_DoNotLoseLines()
    {
        var logger = new FileMediaLogger(LogPath);

        Parallel.For(0, 200, i => logger.Info($"linha {i}"));

        Assert.Equal(200, File.ReadAllLines(LogPath).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
