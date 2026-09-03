namespace LisoP2P.Media;

public static class Nv12Converter
{
    private const int ParallelRowThreshold = 240;

    public static int BgraBufferSize(int width, int height) => width * height * 4;

    public static int Nv12BufferSize(int stride, int height) => (stride * height) + (stride * ((height + 1) / 2));

    public static void ToBgra(byte[] nv12, int stride, int width, int height, byte[] destination) =>
        ToBgra(nv12, stride, height, width, height, destination);

    public static void ToBgra(byte[] nv12, int stride, int codedHeight, int width, int height, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        ArgumentNullException.ThrowIfNull(destination);

        if (stride < width)
        {
            throw new ArgumentException("Stride cannot be smaller than the frame width.", nameof(stride));
        }

        if (codedHeight < height)
        {
            throw new ArgumentException("Coded height cannot be smaller than the visible height.", nameof(codedHeight));
        }

        if (nv12.Length < Nv12BufferSize(stride, codedHeight))
        {
            throw new ArgumentException($"NV12 buffer needs at least {Nv12BufferSize(stride, codedHeight)} bytes.", nameof(nv12));
        }

        if (destination.Length < BgraBufferSize(width, height))
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        if (height >= ParallelRowThreshold)
        {
            Parallel.For(0, height, row => ConvertRow(nv12, stride, codedHeight, width, destination, row));
            return;
        }

        for (var row = 0; row < height; row++)
        {
            ConvertRow(nv12, stride, codedHeight, width, destination, row);
        }
    }

    private static void ConvertRow(byte[] nv12, int stride, int codedHeight, int width, byte[] destination, int row)
    {
        var luminance = row * stride;
        var chroma = (stride * codedHeight) + ((row / 2) * stride);
        var target = row * width * 4;

        for (var x = 0; x < width; x++)
        {
            var c = nv12[luminance + x] - 16;
            var chromaIndex = chroma + ((x / 2) * 2);
            var d = nv12[chromaIndex] - 128;
            var e = nv12[chromaIndex + 1] - 128;

            var offset = target + (x * 4);
            destination[offset] = Clamp(((298 * c) + (516 * d) + 128) >> 8);
            destination[offset + 1] = Clamp(((298 * c) - (100 * d) - (208 * e) + 128) >> 8);
            destination[offset + 2] = Clamp(((298 * c) + (409 * e) + 128) >> 8);
            destination[offset + 3] = 255;
        }
    }

    private static byte Clamp(int value) => value switch
    {
        < 0 => 0,
        > 255 => 255,
        _ => (byte)value,
    };
}
