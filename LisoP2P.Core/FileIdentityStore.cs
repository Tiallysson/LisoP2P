using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;

namespace LisoP2P.Core;

/// <summary>
/// Holds the Ed25519 key pair that is this peer's identity. The private key never leaves this
/// class: nothing in the app signs anything yet, and the pair exists so the id is derived and
/// verifiable rather than a random Guid.
/// </summary>
public sealed class FileIdentityStore : IIdentityStore
{
    private const int CurrentVersion = 2;
    private const string DpapiProtection = "dpapi";
    private const string NoProtection = "none";

    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LisoP2P");

    private readonly string _path;
    private readonly byte[] _privateKey;
    private readonly object _sync = new();
    private string _nickname;

    public PeerId Id { get; }
    public string Fingerprint { get; }
    public string Nickname => Volatile.Read(ref _nickname);

    public PeerIdentity Current => new()
    {
        PublicKey = Id.PublicKeyBytes,
        Fingerprint = Fingerprint,
        Nickname = Nickname,
    };

    /// <summary>
    /// True when this boot replaced a pre-fase-6 identity.json (Guid identity) with a key pair.
    /// The old id is gone for good, so the UI says so instead of letting the peer silently look
    /// like a stranger to everyone who already knew it.
    /// </summary>
    public bool MigratedFromLegacyIdentity { get; }

    /// <summary>True when this boot generated the key pair, i.e. there was nothing to load.</summary>
    public bool IsFirstRun { get; }

    public event Action<string>? NicknameChanged;

    public FileIdentityStore() : this(DefaultDirectory)
    {
    }

    public FileIdentityStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "identity.json");

        var loaded = TryLoad(out var storedNickname, out var wasLegacy);

        MigratedFromLegacyIdentity = wasLegacy;
        IsFirstRun = loaded is null && !wasLegacy;

        _privateKey = loaded ?? RandomNumberGenerator.GetBytes(Ed25519PrivateKeyParameters.KeySize);
        Id = new PeerId(DerivePublicKey(_privateKey));
        Fingerprint = PeerFingerprint.For(Id);

        var sanitized = NicknameRules.Sanitize(storedNickname);
        _nickname = sanitized.Length > 0 ? sanitized : NicknameRules.FallbackFor(Id);

        if (loaded is null || sanitized.Length == 0)
        {
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

    private byte[]? TryLoad(out string? nickname, out bool wasLegacy)
    {
        nickname = null;
        wasLegacy = false;

        if (!File.Exists(_path))
        {
            return null;
        }

        IdentityData? data;

        try
        {
            data = JsonSerializer.Deserialize<IdentityData>(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (data is null)
        {
            return null;
        }

        nickname = data.Nickname;

        if (data.Version != CurrentVersion || data.PrivateKey is null)
        {
            // Fase 0-5 stored a random Guid. There is no way to turn one into a key pair, so the
            // identity is replaced; the nickname survives because it was never the identity.
            wasLegacy = data.Id is not null;
            return null;
        }

        try
        {
            var raw = Convert.FromBase64String(data.PrivateKey);
            var unprotected = data.Protection == DpapiProtection ? Unprotect(raw) : raw;

            return unprotected.Length == Ed25519PrivateKeyParameters.KeySize ? unprotected : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // A key that cannot be read is a key that is gone: a fresh pair beats refusing to boot.
            return null;
        }
    }

    private void Persist(string nickname)
    {
        var protection = OperatingSystem.IsWindows() ? DpapiProtection : NoProtection;
        var stored = protection == DpapiProtection ? Protect(_privateKey) : _privateKey;

        var json = JsonSerializer.Serialize(new IdentityData(
            CurrentVersion,
            nickname,
            Convert.ToBase64String(Id.PublicKeyBytes),
            Convert.ToBase64String(stored),
            protection,
            null));

        var temporary = _path + ".tmp";

        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    private static byte[] DerivePublicKey(byte[] privateKey) =>
        new Ed25519PrivateKeyParameters(privateKey).GeneratePublicKey().GetEncoded();

    private static byte[] Protect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
        : data;

    private static byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser)
        : data;

    private sealed record IdentityData(
        int Version,
        string? Nickname,
        string? PublicKey,
        string? PrivateKey,
        string? Protection,
        string? Id);
}
