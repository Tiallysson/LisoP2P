using System.Text.Json;
using LisoP2P.Core;

namespace LisoP2P.Tests;

public class PeerIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LisoP2PIdentity_" + Guid.NewGuid());

    [Fact]
    public void PeerId_EqualityAndHashCode_FollowTheKeyBytes()
    {
        var first = TestIds.From(0x42);
        var second = TestIds.From(0x42);
        var other = TestIds.From(0x43);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void PeerId_WorksAsADictionaryKey()
    {
        // Every layer since fase 1 looks peers up by id; reference equality here would fail
        // silently instead of throwing.
        var map = new Dictionary<PeerId, string> { [TestIds.From(7)] = "sete" };

        Assert.True(map.ContainsKey(TestIds.From(7)));
        Assert.Equal("sete", map[TestIds.From(7)]);
        Assert.False(map.ContainsKey(TestIds.From(8)));
    }

    [Fact]
    public void PeerId_MutatingTheSourceArray_DoesNotChangeTheId()
    {
        var bytes = new byte[PeerId.PublicKeySize];
        Array.Fill(bytes, (byte)9);

        var id = new PeerId(bytes);
        bytes[0] = 1;

        Assert.Equal(TestIds.From(9), id);
    }

    [Fact]
    public void PeerId_RejectsAKeyOfTheWrongSize()
    {
        Assert.Throws<ArgumentException>(() => new PeerId([1, 2, 3]));
        Assert.False(PeerId.IsValidKey([1, 2, 3]));
        Assert.False(PeerId.IsValidKey(null));
        Assert.True(PeerId.IsValidKey(new byte[PeerId.PublicKeySize]));
    }

    [Fact]
    public void PeerId_RoundTripsThroughHex()
    {
        var id = TestIds.From(0xC7);

        Assert.True(PeerId.TryParseHex(id.ToHex(), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(PeerId.TryParseHex("abc", out _));
        Assert.False(PeerId.TryParseHex(null, out _));
    }

    [Fact]
    public void Fingerprint_IsStableForTheSameKeyAndGroupedByFour()
    {
        var id = TestIds.From(0x5A);

        var fingerprint = PeerFingerprint.For(id);

        Assert.Equal(fingerprint, PeerFingerprint.For(TestIds.From(0x5A)));
        Assert.NotEqual(fingerprint, PeerFingerprint.For(TestIds.From(0x5B)));

        var groups = fingerprint.Split(' ');

        Assert.Equal(PeerFingerprint.GroupCount, groups.Length);
        Assert.All(groups, group => Assert.Equal(PeerFingerprint.GroupSize, group.Length));
        Assert.All(groups, group => Assert.All(group, c => Assert.Contains(c, "0123456789ABCDEF")));
    }

    [Fact]
    public void FirstBoot_GeneratesAKeyPairAndPersistsThePrivateKeyProtected()
    {
        var store = new FileIdentityStore(_directory);

        Assert.True(store.IsFirstRun);
        Assert.False(store.MigratedFromLegacyIdentity);
        Assert.Equal(PeerId.PublicKeySize, store.Id.PublicKeyBytes.Length);
        Assert.Equal(PeerFingerprint.For(store.Id), store.Fingerprint);

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "identity.json")));
        var root = document.RootElement;
        var storedKey = Convert.FromBase64String(root.GetProperty("PrivateKey").GetString()!);

        Assert.Equal(OperatingSystem.IsWindows() ? "dpapi" : "none", root.GetProperty("Protection").GetString());

        if (OperatingSystem.IsWindows())
        {
            // A DPAPI blob carries its own header, so it is never the bare 32-byte seed.
            Assert.True(storedKey.Length > 32);
        }
    }

    [Fact]
    public void SecondBoot_KeepsTheSameKeyPair()
    {
        var first = new FileIdentityStore(_directory);
        var second = new FileIdentityStore(_directory);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.False(second.IsFirstRun);
    }

    [Fact]
    public void LegacyIdentity_IsReplacedByANewKeyPairAndKeepsTheNickname()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "identity.json"),
            JsonSerializer.Serialize(new { Id = Guid.NewGuid(), Nickname = "antigo" }));

        var store = new FileIdentityStore(_directory);

        Assert.True(store.MigratedFromLegacyIdentity);
        Assert.Equal("antigo", store.Nickname);
        Assert.Equal(PeerId.PublicKeySize, store.Id.PublicKeyBytes.Length);

        // The replacement is itself persisted, so the migration happens exactly once.
        var reloaded = new FileIdentityStore(_directory);

        Assert.False(reloaded.MigratedFromLegacyIdentity);
        Assert.Equal(store.Id, reloaded.Id);
    }

    [Fact]
    public void Current_ExposesTheKeyFingerprintAndNickname()
    {
        var store = new FileIdentityStore(_directory);
        store.SetNickname("ana");

        var identity = store.Current;

        Assert.Equal(store.Id, identity.Id);
        Assert.Equal(store.Fingerprint, identity.Fingerprint);
        Assert.Equal("ana", identity.Nickname);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
