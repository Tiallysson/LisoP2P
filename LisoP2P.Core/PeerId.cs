namespace LisoP2P.Core;

/// <summary>
/// The peer's Ed25519 public key. Carrying the whole key rather than a hash of it is what lets any
/// peer compute the fingerprint of anyone it hears about without a second round trip — and it keeps
/// the door open for signing envelopes in a later phase.
/// </summary>
public sealed record PeerId : IComparable<PeerId>
{
    public const int PublicKeySize = 32;

    public byte[] PublicKeyBytes { get; }

    public PeerId(byte[] publicKeyBytes)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBytes);

        if (publicKeyBytes.Length != PublicKeySize)
        {
            throw new ArgumentException(
                $"Uma chave pública Ed25519 tem {PublicKeySize} bytes.", nameof(publicKeyBytes));
        }

        // Copied so a buffer decoded off the wire can never mutate an id already used as a
        // dictionary key.
        PublicKeyBytes = [.. publicKeyBytes];
    }

    public static bool IsValidKey(byte[]? bytes) => bytes is { Length: PublicKeySize };

    public bool Equals(PeerId? other) =>
        other is not null && PublicKeyBytes.AsSpan().SequenceEqual(other.PublicKeyBytes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(PublicKeyBytes);
        return hash.ToHashCode();
    }

    public int CompareTo(PeerId? other) =>
        other is null ? 1 : PublicKeyBytes.AsSpan().SequenceCompareTo(other.PublicKeyBytes);

    public string ToHex() => Convert.ToHexString(PublicKeyBytes);

    public override string ToString() => ToHex()[..16];

    public static bool TryParseHex(string? value, out PeerId? id)
    {
        id = null;

        if (value is null || value.Length != PublicKeySize * 2)
        {
            return false;
        }

        try
        {
            id = new PeerId(Convert.FromHexString(value));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
