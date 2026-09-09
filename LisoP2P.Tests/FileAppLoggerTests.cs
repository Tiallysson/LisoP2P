using LisoP2P.Core.Diagnostics;

namespace LisoP2P.Tests;

public class FileAppLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lisop2p-applog-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_directory, "app.log");

    [Fact]
    public void WritesOneLinePerLevel()
    {
        var logger = new FileAppLogger(LogPath);

        logger.Info("iniciou");
        logger.Warning("porta ocupada");
        logger.Error("falhou", new InvalidOperationException("detalhe"));

        var content = File.ReadAllText(LogPath);

        Assert.Contains("INFO iniciou", content, StringComparison.Ordinal);
        Assert.Contains("AVISO porta ocupada", content, StringComparison.Ordinal);
        Assert.Contains("ERRO falhou", content, StringComparison.Ordinal);
        Assert.Contains("detalhe", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RollsTheFileOnceItPassesTheSizeLimit()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(LogPath, new string('x', 2048));

        var logger = new FileAppLogger(LogPath, maxBytes: 1024);
        logger.Info("depois do roll");

        Assert.True(File.Exists(LogPath + ".1"));
        Assert.DoesNotContain("xxxx", File.ReadAllText(LogPath), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
