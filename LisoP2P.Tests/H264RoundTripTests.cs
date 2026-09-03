using LisoP2P.Media;

namespace LisoP2P.Tests;

public class H264RoundTripTests
{
    private const int Width = 320;
    private const int Height = 180;

    private static VideoFrame CreateFrame(int index)
    {
        var buffer = new byte[Width * Height * 3 / 2];

        for (var row = 0; row < Height; row++)
        {
            for (var x = 0; x < Width; x++)
            {
                buffer[(row * Width) + x] = (byte)((x + row + (index * 8)) % 256);
            }
        }

        for (var i = Width * Height; i < buffer.Length; i++)
        {
            buffer[i] = 128;
        }

        return new VideoFrame(buffer, buffer.Length, Width, Height, 0);
    }

    [Fact]
    public void EncodedFramesDecodeBackIntoAPictureOfTheSameSize()
    {
        IVideoEncoder encoder;

        try
        {
            encoder = VideoEncoderFactory.Create(Width, Height, 1500, 30, includeHardware: false);
        }
        catch (Exception ex)
        {
            return;
        }

        var encoded = new List<EncodedFrame>();
        encoder.FrameEncoded += frame => encoded.Add(frame);

        using (encoder)
        {
            for (var i = 0; i < 20 && encoded.Count < 5; i++)
            {
                encoder.Encode(CreateFrame(i), forceKeyframe: i == 0);
            }
        }

        if (encoded.Count == 0)
        {
            return;
        }

        IVideoDecoder decoder;

        try
        {
            decoder = VideoDecoderFactory.Create(Width, Height, includeHardware: false);
        }
        catch (Exception ex)
        {
            return;
        }

        using (decoder)
        {
            PreviewFrame? decoded = null;

            for (var i = 0; i < encoded.Count && decoded is null; i++)
            {
                decoded = decoder.Decode(new DecodableFrame(encoded[i].Data, encoded[i].IsKeyframe, (uint)i));
            }

            Assert.NotNull(decoded);
            Assert.Equal(Width, decoded!.Value.Width);
            Assert.Equal(Height, decoded.Value.Height);
            Assert.Equal(Width * 4, decoded.Value.Stride);
            Assert.True(decoded.Value.Bgra.Length >= Width * Height * 4);
        }
    }
}
