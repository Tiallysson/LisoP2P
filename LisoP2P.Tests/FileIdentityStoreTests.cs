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

    [Fact]
    public void SetNickname_PersistsAndKeepsTheSameId()
    {
        var store = new FileIdentityStore(_directory);
        var id = store.Id;

        store.SetNickname("  Nome   Escolhido ");

        Assert.Equal("Nome Escolhido", store.Nickname);

        var reloaded = new FileIdentityStore(_directory);

        Assert.Equal(id, reloaded.Id);
        Assert.Equal("Nome Escolhido", reloaded.Nickname);
    }

    [Fact]
    public void SetNickname_RaisesChangedOnlyWhenTheValueChanges()
    {
        var store = new FileIdentityStore(_directory);
        var changes = new List<string>();
        store.NicknameChanged += changes.Add;

        store.SetNickname("alfa");
        store.SetNickname("alfa");
        store.SetNickname("beta");

        Assert.Equal(["alfa", "beta"], changes);
    }

    [Fact]
    public void SetNickname_RejectsEmptyValue()
    {
        var store = new FileIdentityStore(_directory);
        var original = store.Nickname;

        Assert.Throws<ArgumentException>(() => store.SetNickname("   "));
        Assert.Equal(original, store.Nickname);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
