using LisoP2P.Core;
using LisoP2P.Core.Settings;

namespace LisoP2P.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LisoP2PSettings_" + Guid.NewGuid());

    private static AppSettings Populated() => new()
    {
        DiscoveryPort = 48100,
        SessionPort = 48101,
        MediaPort = 48102,
        PreferredMonitorIndex = 2,
        CaptureResolution = "1920x1080",
        CaptureFps = 45,
        AudioInputDeviceId = "{entrada}",
        AudioOutputDeviceId = "{saida}",
        AudioMode = AudioCaptureMode.Both,
        PushToTalkEnabled = false,
        PushToTalkKey = "Space",
        Nickname = "ana",
    };

    private static void AssertSame(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.DiscoveryPort, actual.DiscoveryPort);
        Assert.Equal(expected.SessionPort, actual.SessionPort);
        Assert.Equal(expected.MediaPort, actual.MediaPort);
        Assert.Equal(expected.PreferredMonitorIndex, actual.PreferredMonitorIndex);
        Assert.Equal(expected.CaptureResolution, actual.CaptureResolution);
        Assert.Equal(expected.CaptureFps, actual.CaptureFps);
        Assert.Equal(expected.AudioInputDeviceId, actual.AudioInputDeviceId);
        Assert.Equal(expected.AudioOutputDeviceId, actual.AudioOutputDeviceId);
        Assert.Equal(expected.AudioMode, actual.AudioMode);
        Assert.Equal(expected.PushToTalkEnabled, actual.PushToTalkEnabled);
        Assert.Equal(expected.PushToTalkKey, actual.PushToTalkKey);
        Assert.Equal(expected.Nickname, actual.Nickname);
    }

    [Fact]
    public void Codec_RoundTripsEveryField()
    {
        var settings = Populated();

        Assert.True(AppSettingsCodec.TryDecode(AppSettingsCodec.Encode(settings), out var decoded));
        AssertSame(settings, decoded!);
    }

    [Fact]
    public void Codec_NeverThrowsOnRandomBytes()
    {
        var random = new Random(4711);
        var buffer = new byte[64];

        for (var i = 0; i < 2000; i++)
        {
            random.NextBytes(buffer);
            AppSettingsCodec.TryDecode(buffer.AsSpan(0, random.Next(buffer.Length + 1)), out _);
        }
    }

    [Fact]
    public void Normalized_ReplacesOutOfRangeValuesWithDefaults()
    {
        var defaults = new AppSettings();

        var normalized = new AppSettings
        {
            DiscoveryPort = 80,
            SessionPort = 70000,
            MediaPort = -1,
            PreferredMonitorIndex = -3,
            CaptureResolution = "não é resolução",
            CaptureFps = 0,
            AudioMode = (AudioCaptureMode)99,
            PushToTalkKey = "   ",
            AudioInputDeviceId = "  ",
            Nickname = "  ana   maria  ",
        }.Normalized();

        Assert.Equal(defaults.DiscoveryPort, normalized.DiscoveryPort);
        Assert.Equal(defaults.SessionPort, normalized.SessionPort);
        Assert.Equal(defaults.MediaPort, normalized.MediaPort);
        Assert.Equal(0, normalized.PreferredMonitorIndex);
        Assert.Equal(defaults.CaptureResolution, normalized.CaptureResolution);
        Assert.Equal(defaults.CaptureFps, normalized.CaptureFps);
        Assert.Equal(AudioCaptureMode.Microphone, normalized.AudioMode);
        Assert.Equal(defaults.PushToTalkKey, normalized.PushToTalkKey);
        Assert.Null(normalized.AudioInputDeviceId);
        Assert.Equal("ana maria", normalized.Nickname);
    }

    [Theory]
    [InlineData("Native", 0)]
    [InlineData("native", 0)]
    [InlineData("1920x1080", 1080)]
    [InlineData("1280X720", 720)]
    [InlineData("720", 720)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("1920x", null)]
    public void ParseResolutionHeight_ReadsTheHeightOrNothing(string value, int? expected) =>
        Assert.Equal(expected, AppSettings.ParseResolutionHeight(value));

    [Fact]
    public void FileStore_RoundTripsThroughDiskAndRaisesChanged()
    {
        var store = new FileAppSettingsStore(_directory);
        var settings = Populated();
        AppSettings? observed = null;
        store.Changed += value => observed = value;

        store.Save(settings);

        AssertSame(settings, observed!);
        AssertSame(settings, new FileAppSettingsStore(_directory).Current);
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void FileStore_FallsBackToDefaultsOnACorruptFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "isto não é json {{{");

        AssertSame(new AppSettings(), new FileAppSettingsStore(_directory).Current);
    }

    [Fact]
    public void FileStore_CurrentIsACopy()
    {
        var store = new FileAppSettingsStore(_directory);

        store.Current.SessionPort = 50000;

        Assert.Equal(NetworkPorts.DefaultSessionPort, store.Current.SessionPort);
    }

    [Fact]
    public void Ports_DefaultWhenThereIsNeitherAFileNorAFlag()
    {
        var ports = PortResolver.Resolve(null, []);

        Assert.Equal(NetworkPorts.DefaultDiscoveryPort, ports.DiscoveryPort);
        Assert.Equal(NetworkPorts.DefaultSessionPort, ports.SessionPort);
        Assert.Equal(NetworkPorts.DefaultMediaPort, ports.MediaPort);
    }

    [Fact]
    public void Ports_ComeFromTheFileWhenThereIsNoFlag()
    {
        var ports = PortResolver.Resolve(
            new AppSettings { DiscoveryPort = 40100, SessionPort = 40101, MediaPort = 40102 },
            []);

        Assert.Equal(new ResolvedPorts(40100, 40101, 40102), ports);
    }

    [Fact]
    public void Ports_FlagsBeatTheFile()
    {
        var stored = new AppSettings { DiscoveryPort = 40100, SessionPort = 40101, MediaPort = 40102 };

        var ports = PortResolver.Resolve(
            stored,
            ["--discovery-port", "50100", "--session-port", "50101", "--media-port", "50102"]);

        Assert.Equal(new ResolvedPorts(50100, 50101, 50102), ports);
    }

    [Fact]
    public void Ports_APartialFlagSetLeavesTheRestToTheFile()
    {
        var stored = new AppSettings { DiscoveryPort = 40100, SessionPort = 40101, MediaPort = 40102 };

        var ports = PortResolver.Resolve(stored, ["--discovery-port", "50100"]);

        Assert.Equal(new ResolvedPorts(50100, 40101, 40102), ports);
    }

    [Fact]
    public void Ports_SessionPortFlagWithoutAMediaPortFlagDerivesTheMediaPort()
    {
        // The fase 0/1 two-instance test passes only --session-port; both instances would
        // otherwise bind the same media socket.
        var ports = PortResolver.Resolve(null, ["--session-port", "47201"]);

        Assert.Equal(47201, ports.SessionPort);
        Assert.Equal(47202, ports.MediaPort);
    }

    [Fact]
    public void Ports_SessionPortFlagMatchingTheFileKeepsTheStoredMediaPort()
    {
        var stored = new AppSettings { SessionPort = 40101, MediaPort = 45000 };

        var ports = PortResolver.Resolve(stored, ["--session-port", "40101"]);

        Assert.Equal(45000, ports.MediaPort);
    }

    [Fact]
    public void Ports_IgnoreAFlagThatIsNotAValidPort()
    {
        var stored = new AppSettings { SessionPort = 40101, MediaPort = 40102 };

        var ports = PortResolver.Resolve(stored, ["--session-port", "banana", "--media-port", "80"]);

        Assert.Equal(40101, ports.SessionPort);
        Assert.Equal(40102, ports.MediaPort);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
