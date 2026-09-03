using LisoP2P.Media;
using Vortice.MediaFoundation;

namespace LisoP2P.Tests;

public class VideoEncoderFactoryTests
{
    private sealed class FakeEncoder : IVideoEncoder
    {
        private readonly bool _failOnConfigure;

        public string Name { get; }
        public bool IsHardware { get; }
        public bool Disposed { get; private set; }
        public bool Configured { get; private set; }

        public event Action<EncodedFrame>? FrameEncoded;

        public FakeEncoder(string name, bool isHardware, bool failOnConfigure)
        {
            Name = name;
            IsHardware = isHardware;
            _failOnConfigure = failOnConfigure;
        }

        public void Configure(int width, int height, int targetBitrateKbps, int fps)
        {
            if (_failOnConfigure)
            {
                throw new NotSupportedException("MFT de hardware assíncrono não é suportado nesta fase.");
            }

            Configured = true;
        }

        public IMFMediaType? CreateOutputMediaType() => null;

        public EncodedFrame? Encode(VideoFrame frame, bool forceKeyframe)
        {
            var encoded = new EncodedFrame([0x00, 0x00, 0x00, 0x01], forceKeyframe, frame.TimestampTicks);
            FrameEncoded?.Invoke(encoded);
            return encoded;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Create_FallsBackToSoftwareWhenHardwareFailsToConfigure()
    {
        var hardware = new FakeEncoder("NVIDIA H.264", true, failOnConfigure: true);
        var software = new FakeEncoder("Microsoft H264 Encoder MFT", false, failOnConfigure: false);
        var logs = new List<string>();

        var candidates = new[]
        {
            new VideoEncoderCandidate(hardware.Name, true, () => hardware),
            new VideoEncoderCandidate(software.Name, false, () => software),
        };

        var encoder = VideoEncoderFactory.Create(candidates, 1920, 1080, 3000, 30, logs.Add);

        Assert.Same(software, encoder);
        Assert.True(software.Configured);
        Assert.True(hardware.Disposed);
        Assert.Contains(logs, line => line.Contains("NVIDIA H.264", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("Fallback para software", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("assíncrono", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_UsesHardwareWhenItConfigures()
    {
        var hardware = new FakeEncoder("Intel QuickSync H.264", true, failOnConfigure: false);
        var software = new FakeEncoder("Microsoft H264 Encoder MFT", false, failOnConfigure: false);

        var candidates = new[]
        {
            new VideoEncoderCandidate(hardware.Name, true, () => hardware),
            new VideoEncoderCandidate(software.Name, false, () => software),
        };

        var encoder = VideoEncoderFactory.Create(candidates, 1920, 1080, 3000, 30);

        Assert.Same(hardware, encoder);
        Assert.False(software.Configured);
    }

    [Fact]
    public void Create_ThrowsWhenEveryCandidateFails()
    {
        var candidates = new[]
        {
            new VideoEncoderCandidate("hw", true, () => new FakeEncoder("hw", true, failOnConfigure: true)),
            new VideoEncoderCandidate("sw", false, () => new FakeEncoder("sw", false, failOnConfigure: true)),
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => VideoEncoderFactory.Create(candidates, 1920, 1080, 3000, 30));

        Assert.Contains("hw", error.Message, StringComparison.Ordinal);
        Assert.Contains("sw", error.Message, StringComparison.Ordinal);
    }
}
