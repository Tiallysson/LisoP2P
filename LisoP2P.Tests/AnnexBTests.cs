using LisoP2P.Media;

namespace LisoP2P.Tests;

public class AnnexBTests
{
    [Fact]
    public void Wrap_PrependsStartCodeWhenMissing()
    {
        var wrapped = AnnexB.Wrap([0x65, 0x88]);

        Assert.Equal([0x00, 0x00, 0x00, 0x01, 0x65, 0x88], wrapped);
    }

    [Fact]
    public void Wrap_KeepsExistingStartCode()
    {
        var wrapped = AnnexB.Wrap([0x00, 0x00, 0x00, 0x01, 0x65]);

        Assert.Equal([0x00, 0x00, 0x00, 0x01, 0x65], wrapped);
    }

    [Fact]
    public void HasStartCode_AcceptsThreeByteVariant()
    {
        Assert.True(AnnexB.HasStartCode([0x00, 0x00, 0x01, 0x67]));
        Assert.False(AnnexB.HasStartCode([0x00, 0x01, 0x67]));
        Assert.False(AnnexB.HasStartCode([0x67]));
    }

    [Fact]
    public void Writer_SeparatesFramesWithStartCodes()
    {
        using var stream = new MemoryStream();

        using (var writer = new AnnexBFileWriter(stream, ownsStream: false))
        {
            writer.Write(new EncodedFrame([0x67, 0x42], true, 0));
            writer.Write(new EncodedFrame([0x41, 0x9A], false, 1));
        }

        Assert.Equal(
            [0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x00, 0x00, 0x01, 0x41, 0x9A],
            stream.ToArray());
    }

    [Fact]
    public void Writer_DoesNotDuplicateStartCodes()
    {
        using var stream = new MemoryStream();

        using (var writer = new AnnexBFileWriter(stream, ownsStream: false))
        {
            writer.Write(new EncodedFrame([0x00, 0x00, 0x00, 0x01, 0x67], true, 0));
        }

        Assert.Equal([0x00, 0x00, 0x00, 0x01, 0x67], stream.ToArray());
    }
}
