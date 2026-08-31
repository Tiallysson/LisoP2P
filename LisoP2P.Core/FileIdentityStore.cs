using System.Text.Json;

namespace LisoP2P.Core;

public sealed class FileIdentityStore : IIdentityStore
{
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LisoP2P");

    public PeerId Id { get; }
    public string Nickname { get; }

    public FileIdentityStore() : this(DefaultDirectory)
    {
    }

    public FileIdentityStore(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "identity.json");

        if (File.Exists(path))
        {
            var data = JsonSerializer.Deserialize<IdentityData>(File.ReadAllText(path))!;
            Id = new PeerId(data.Id);
            Nickname = data.Nickname;
        }
        else
        {
            var id = Guid.NewGuid();
            var nickname = "user-" + id.ToString("N")[..4];
            File.WriteAllText(path, JsonSerializer.Serialize(new IdentityData(id, nickname)));
            Id = new PeerId(id);
            Nickname = nickname;
        }
    }

    private sealed record IdentityData(Guid Id, string Nickname);
}
