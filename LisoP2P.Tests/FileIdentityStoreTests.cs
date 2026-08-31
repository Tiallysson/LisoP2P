using LisoP2P.Core;

namespace LisoP2P.Tests;

public class FileIdentityStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LisoP2PTests_" + Guid.NewGuid());

    [Fact]
    public void FirstBoot_GeneratesNewIdentity()
    {
        var store = new FileIdentityStore(_directory);

        Assert.NotEqual(Guid.Empty, store.Id.Value);
        Assert.StartsWith("user-", store.Nickname);
    }

    [Fact]
    public void SecondBoot_LoadsSameIdentity()
    {
        var first = new FileIdentityStore(_directory);
        var second = new FileIdentityStore(_directory);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Nickname, second.Nickname);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
