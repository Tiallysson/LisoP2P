using System.Text.Json;

namespace LisoP2P.Core;

public sealed class FileIdentityStore : IIdentityStore
{
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LisoP2P");

    private readonly string _path;
    private readonly object _sync = new();
    private string _nickname;

    public PeerId Id { get; }
    public string Nickname => Volatile.Read(ref _nickname);

    public event Action<string>? NicknameChanged;

    public FileIdentityStore() : this(DefaultDirectory)
    {
    }

    public FileIdentityStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "identity.json");

        if (File.Exists(_path))
        {
            var data = JsonSerializer.Deserialize<IdentityData>(File.ReadAllText(_path))!;
            Id = new PeerId(data.Id);

            var stored = NicknameRules.Sanitize(data.Nickname);
            _nickname = stored.Length > 0 ? stored : NicknameRules.FallbackFor(Id);

            if (stored.Length == 0)
            {
                Persist(_nickname);
            }
        }
        else
        {
            Id = new PeerId(Guid.NewGuid());
            _nickname = NicknameRules.FallbackFor(Id);
            Persist(_nickname);
        }
    }

    public void SetNickname(string nickname)
    {
        var sanitized = NicknameRules.Sanitize(nickname);

        if (sanitized.Length == 0)
        {
            throw new ArgumentException("Nome de usuário inválido.", nameof(nickname));
        }

        lock (_sync)
        {
            if (string.Equals(sanitized, _nickname, StringComparison.Ordinal))
            {
                return;
            }

            Persist(sanitized);
            Volatile.Write(ref _nickname, sanitized);
        }

        NicknameChanged?.Invoke(sanitized);
    }

    private void Persist(string nickname)
    {
        var json = JsonSerializer.Serialize(new IdentityData(Id.Value, nickname));
        var temporary = _path + ".tmp";

        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record IdentityData(Guid Id, string Nickname);
}
