using System.Security.Cryptography;
using System.Text;

namespace LisoP2P.Core;

/// <summary>
/// SHA-256 of the public key, shown as 16 groups of 4 hex characters. The grouping exists so two
/// people can read it out loud to each other and notice a mismatch — it is verification by
/// transparency, never a gate that blocks a connection.
/// </summary>
public static class PeerFingerprint
{
    public const int GroupSize = 4;
    public const int GroupCount = 16;

    public static string For(PeerId id) => For(id.PublicKeyBytes);

    public static string For(ReadOnlySpan<byte> publicKey)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(publicKey, hash);

        var hex = Convert.ToHexString(hash);
        var builder = new StringBuilder(hex.Length + GroupCount - 1);

        for (var offset = 0; offset < hex.Length; offset += GroupSize)
        {
            if (offset > 0)
            {
                builder.Append(' ');
            }

            builder.Append(hex.AsSpan(offset, GroupSize));
        }

        return builder.ToString();
    }

    /// <summary>The first groups, for a toast or a header that has no room for all 16.</summary>
    public static string Short(PeerId id, int groups = 4)
    {
        var full = For(id);
        var take = Math.Clamp(groups, 1, GroupCount);
        var length = (take * (GroupSize + 1)) - 1;

        return take == GroupCount ? full : full[..length] + "…";
    }
}
