using LisoP2P.Media;

namespace LisoP2P.Tests;

public class VideoDecoderFactoryTests
{
    private sealed class FakeDecoder : IVideoDecoder
    {
        private readonly bool _failOnConfigure;

        public string Name { get; }
        public bool IsHardware { get; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool Configured { get; private set; }
        public bool Disposed { get; private set; }

        public FakeDecoder(string name, bool isHardware, bool failOnConfigure)
        {
            Name = name;
            IsHardware = isHardware;
            _failOnConfigure = failOnConfigure;
        }

        public void Configure(int width, int height)
        {
            if (_failOnConfigure)
            {
                throw new NotSupportedException("MFT de decode assíncrono não é suportado nesta fase.");
            }

            Width = width;
            Height = height;
            Configured = true;
        }

        public PreviewFrame? Decode(DecodableFrame frame) => null;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Create_PrefersTheFirstCandidateThatConfigures()
    {
        var software = new FakeDecoder("Microsoft H264 Video Decoder MFT", false, failOnConfigure: false);
        var hardware = new FakeDecoder("NVIDIA H.264 decoder", true, failOnConfigure: false);

        var candidates = new[]
        {
            new VideoDecoderCandidate(software.Name, false, () => software),
            new VideoDecoderCandidate(hardware.Name, true, () => hardware),
        };

        var decoder = VideoDecoderFactory.Create(candidates, 1280, 720);

        Assert.Same(software, decoder);
        Assert.Equal(1280, software.Width);
        Assert.Equal(720, software.Height);
        Assert.False(hardware.Configured);
    }

    [Fact]
    public void Create_FallsBackWhenTheFirstCandidateFails()
    {
        var software = new FakeDecoder("sw", false, failOnConfigure: true);
        var hardware = new FakeDecoder("hw", true, failOnConfigure: false);
        var logs = new List<string>();

        var candidates = new[]
        {
            new VideoDecoderCandidate(software.Name, false, () => software),
            new VideoDecoderCandidate(hardware.Name, true, () => hardware),
        };

        var decoder = VideoDecoderFactory.Create(candidates, 640, 360, logs.Add);

        Assert.Same(hardware, decoder);
        Assert.True(software.Disposed);
        Assert.Contains(logs, line => line.Contains("sw", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_ThrowsWhenEveryCandidateFails()
    {
        var candidates = new[]
        {
            new VideoDecoderCandidate("sw", false, () => new FakeDecoder("sw", false, failOnConfigure: true)),
            new VideoDecoderCandidate("hw", true, () => new FakeDecoder("hw", true, failOnConfigure: true)),
        };

        var error = Assert.Throws<InvalidOperationException>(() => VideoDecoderFactory.Create(candidates, 640, 360));

        Assert.Contains("sw", error.Message, StringComparison.Ordinal);
        Assert.Contains("hw", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumerateSystemDecoders_ListsSoftwareBeforeHardware()
    {
        var candidates = VideoDecoderFactory.EnumerateSystemDecoders().ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var firstHardware = candidates.FindIndex(candidate => candidate.IsHardware);
        var lastSoftware = candidates.FindLastIndex(candidate => !candidate.IsHardware);

        if (firstHardware >= 0 && lastSoftware >= 0)
        {
            Assert.True(lastSoftware < firstHardware);
        }
    }
}
