using LisoP2P.Media;

namespace LisoP2P.Tests;

public class Nv12ConverterTests
{
    private static byte[] CreateFrame(int stride, int width, int height, byte luminance, byte u, byte v)
    {
        var buffer = new byte[Nv12Converter.Nv12BufferSize(stride, height)];

        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                buffer[(row * stride) + x] = luminance;
            }
        }

        var chroma = stride * height;

        for (var row = 0; row < height / 2; row++)
        {
            for (var x = 0; x < width; x += 2)
            {
                buffer[chroma + (row * stride) + x] = u;
                buffer[chroma + (row * stride) + x + 1] = v;
            }
        }

        return buffer;
    }

    [Fact]
    public void ToBgra_ConvertsNeutralGrayToGray()
    {
        var nv12 = CreateFrame(8, 8, 4, 126, 128, 128);
        var bgra = new byte[Nv12Converter.BgraBufferSize(8, 4)];

        Nv12Converter.ToBgra(nv12, 8, 8, 4, bgra);

        for (var i = 0; i < bgra.Length; i += 4)
        {
            Assert.InRange(bgra[i], 126, 130);
            Assert.Equal(bgra[i], bgra[i + 1]);
            Assert.Equal(bgra[i], bgra[i + 2]);
            Assert.Equal(255, bgra[i + 3]);
        }
    }

    [Fact]
    public void ToBgra_ClampsBlackAndWhite()
    {
        var black = new byte[Nv12Converter.BgraBufferSize(4, 2)];
        var white = new byte[Nv12Converter.BgraBufferSize(4, 2)];

        Nv12Converter.ToBgra(CreateFrame(4, 4, 2, 0, 128, 128), 4, 4, 2, black);
        Nv12Converter.ToBgra(CreateFrame(4, 4, 2, 255, 128, 128), 4, 4, 2, white);

        Assert.Equal(0, black[0]);
        Assert.Equal(0, black[1]);
        Assert.Equal(0, black[2]);
        Assert.Equal(255, white[0]);
        Assert.Equal(255, white[1]);
        Assert.Equal(255, white[2]);
    }

    [Fact]
    public void ToBgra_HonoursStridePaddingLargerThanTheWidth()
    {
        var nv12 = CreateFrame(16, 4, 2, 200, 128, 128);
        var bgra = new byte[Nv12Converter.BgraBufferSize(4, 2)];

        Nv12Converter.ToBgra(nv12, 16, 4, 2, bgra);

        Assert.All(Enumerable.Range(0, 4 * 2), pixel => Assert.InRange(bgra[pixel * 4], 210, 220));
    }

    [Fact]
    public void ToBgra_CropsThePaddingRowsAddedByMacroblockAlignment()
    {
        var nv12 = CreateFrame(320, 320, 192, 128, 128, 128);
        var chroma = 320 * 192;

        for (var row = 0; row < 96; row++)
        {
            for (var x = 0; x < 320; x += 2)
            {
                nv12[chroma + (row * 320) + x] = 90;
                nv12[chroma + (row * 320) + x + 1] = 240;
            }
        }

        var bgra = new byte[Nv12Converter.BgraBufferSize(320, 180)];

        Nv12Converter.ToBgra(nv12, 320, 192, 320, 180, bgra);

        Assert.True(bgra[2] > bgra[0]);
    }

    [Fact]
    public void ToBgra_RejectsBuffersThatAreTooSmall()
    {
        Assert.Throws<ArgumentException>(
            () => Nv12Converter.ToBgra(new byte[10], 4, 4, 2, new byte[Nv12Converter.BgraBufferSize(4, 2)]));

        Assert.Throws<ArgumentException>(
            () => Nv12Converter.ToBgra(CreateFrame(4, 4, 2, 0, 128, 128), 4, 4, 2, new byte[4]));
    }

    [Fact]
    public void ToBgra_RejectsStrideSmallerThanWidth()
    {
        Assert.Throws<ArgumentException>(
            () => Nv12Converter.ToBgra(new byte[1024], 2, 4, 2, new byte[Nv12Converter.BgraBufferSize(4, 2)]));
    }
}
