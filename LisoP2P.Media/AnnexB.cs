namespace LisoP2P.Media;

public static class AnnexB
{
    public static ReadOnlySpan<byte> StartCode => [0x00, 0x00, 0x00, 0x01];

    public static bool HasStartCode(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
        {
            return true;
        }

        return data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1;
    }

    public static byte[] Wrap(ReadOnlySpan<byte> data)
    {
        if (HasStartCode(data))
        {
            return data.ToArray();
        }

        var result = new byte[data.Length + 4];
        StartCode.CopyTo(result);
        data.CopyTo(result.AsSpan(4));
        return result;
    }
}
