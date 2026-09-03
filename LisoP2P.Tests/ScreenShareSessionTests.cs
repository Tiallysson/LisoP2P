using System.Drawing;
using System.Net;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class ScreenShareSessionTests
{
    private static readonly PeerId LocalId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly PeerId RemoteId = new(new Guid("22222222-2222-2222-2222-222222222222"));

    private sealed class FakeCapturePipeline : ICapturePipeline
    {
        public IReadOnlyList<CaptureAdapterInfo> AvailableMonitors { get; } =
            [new CaptureAdapterInfo(0, "monitor", new Size(1920, 1080))];

        public bool IsRunning { get; private set; }
        public string? LogFilePath => null;
        public string? LastRecordingPath => null;
        public int KeyframeRequests { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public event Action<EncodedFrame>? FrameReady;
#pragma warning disable CS0067
        public event Action<CaptureStats>? StatsUpdated;
        public event Action<PreviewFrame>? PreviewReady;
        public event Action<string>? Log;
#pragma warning restore CS0067

        public Task StartAsync(int monitorIndex, CaptureSettings settings, CancellationToken ct)
        {
            IsRunning = true;
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            StopCount++;
            return Task.CompletedTask;
        }

        public void RequestKeyframe() => KeyframeRequests++;

        public void Emit(EncodedFrame frame) => FrameReady?.Invoke(frame);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDecoder : IVideoDecoder
    {
        public string Name => "fake";
        public bool IsHardware => false;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public List<DecodableFrame> Decoded { get; } = [];
        public bool Disposed { get; private set; }

        public void Configure(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public PreviewFrame? Decode(DecodableFrame frame)
        {
            Decoded.Add(frame);
            return new PreviewFrame(new byte[16], 2, 2, 8);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeSessionManager Sessions { get; } = new();
        public FakeDiscoveryWithPeers Discovery { get; } = new();
        public FakeCapturePipeline Pipeline { get; } = new();
        public FakeMediaSender Sender { get; } = new();
        public FakeMediaReceiver Receiver { get; } = new();
        public FakeDecoder Decoder { get; } = new();
        public ScreenShareSession Share { get; }

        public Harness()
        {
            Share = new ScreenShareSession(
                new NetworkOptions { MediaPort = 51102 },
                new StubIdentityStore(LocalId.Value, "local"),
                Discovery,
                Sessions,
                Pipeline,
                Sender,
                Receiver,
                NullMediaLogger.Instance,
                (width, height) =>
                {
                    Decoder.Configure(width, height);
                    return Decoder;
                });
        }

        public ValueTask DisposeAsync() => Share.DisposeAsync();
    }

    private static byte[] ShareStart(int width, int height, int fps) =>
        ScreenSharePayloadCodec.Encode(new ScreenSharePayload { Width = width, Height = height, Fps = fps });

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task StartSharing_SendsScreenShareStartAndStreamsFramesToThePeerMediaPort()
    {
        await using var harness = new Harness();
        await harness.Share.StartAsync(CancellationToken.None);

        var session = new FakePeerSession { RemoteId = RemoteId };
        harness.Sessions.Open(session);
        harness.Discovery.Known.Add(new DiscoveredPeer(
            RemoteId, "peer", IPAddress.Parse("192.168.0.42"), 47101, 47999, DateTimeOffset.UtcNow));

        await harness.Share.StartSharingAsync(RemoteId, 0, new CaptureSettings { TargetHeight = 720, TargetFps = 30 }, CancellationToken.None);

        var start = Assert.Single(session.Sent, envelope => envelope.Type == MessageType.ScreenShareStart);
        Assert.True(ScreenSharePayloadCodec.TryDecode(start.Payload, out var payload));
        Assert.Equal(1280, payload!.Width);
        Assert.Equal(720, payload.Height);
        Assert.True(harness.Share.IsSharing);

        harness.Pipeline.Emit(new EncodedFrame([1, 2, 3], true, 0));

        var sent = Assert.Single(harness.Sender.Sent);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.0.42"), 47999), sent.Destination);
    }

    [Fact]
    public async Task StopSharing_StopsTheCaptureAndTellsThePeer()
    {
        await using var harness = new Harness();
        await harness.Share.StartAsync(CancellationToken.None);

        var session = new FakePeerSession { RemoteId = RemoteId };
        harness.Sessions.Open(session);
        harness.Discovery.Known.Add(new DiscoveredPeer(
            RemoteId, "peer", IPAddress.Loopback, 47101, 47102, DateTimeOffset.UtcNow));

        await harness.Share.StartSharingAsync(RemoteId, 0, new CaptureSettings(), CancellationToken.None);
        await harness.Share.StopSharingAsync();

        Assert.False(harness.Share.IsSharing);
        Assert.Equal(1, harness.Pipeline.StopCount);
        Assert.Contains(session.Sent, envelope => envelope.Type == MessageType.ScreenShareStop);

        harness.Pipeline.Emit(new EncodedFrame([1], true, 0));
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task KeyframeRequest_FromTheViewer_ForcesAKeyframeOnTheSender()
    {
        await using var harness = new Harness();
        await harness.Share.StartAsync(CancellationToken.None);

        var session = new FakePeerSession { RemoteId = RemoteId };
        harness.Sessions.Open(session);
        harness.Discovery.Known.Add(new DiscoveredPeer(
            RemoteId, "peer", IPAddress.Loopback, 47101, 47102, DateTimeOffset.UtcNow));

        await harness.Share.StartSharingAsync(RemoteId, 0, new CaptureSettings(), CancellationToken.None);

        session.Receive(MessageType.KeyframeRequest, []);

        Assert.Equal(1, harness.Pipeline.KeyframeRequests);
    }

    [Fact]
    public async Task ScreenShareStart_ConfiguresTheDecoderAndAsksForAKeyframe()
    {
        await using var harness = new Harness();
        await harness.Share.StartAsync(CancellationToken.None);

        var session = new FakePeerSession { RemoteId = RemoteId };
        harness.Sessions.Open(session);
        harness.Discovery.Known.Add(new DiscoveredPeer(
            RemoteId, "peer", IPAddress.Parse("10.0.0.7"), 47101, 47102, DateTimeOffset.UtcNow));

        session.Receive(MessageType.ScreenShareStart, ShareStart(1280, 720, 30));

        Assert.True(harness.Share.IsWatching);
        Assert.Equal(1280, harness.Decoder.Width);
        Assert.Equal(720, harness.Decoder.Height);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), harness.Receiver.ExpectedSource);
        Assert.Contains(session.Sent, envelope => envelope.Type == MessageType.KeyframeRequest);
    }

    [Fact]
    public async Task ReceivedFrames_AreOnlyDecodedOnceAKeyframeHasArrived()
    {
        await using var harness = new Harness();
        await harness.Share.StartAsync(CancellationToken.None);

        var session = new FakePeerSession { RemoteId = RemoteId };
        harness.Sessions.Open(session);
        harness.Discovery.Known.Add(new DiscoveredPeer(
            RemoteId, "peer", IPAddress.Loopback, 47101, 47102, DateTimeOffset.UtcNow));

        session.Receive(MessageType.ScreenShareStart, ShareStart(640, 360, 30));

        harness.Receiver.Emit(new DecodableFrame([1, 2, 3], false, 1));
        await Task.Delay(50);
        Assert.Empty(harness.Decoder.Decoded);

        PreviewFrame? shown = null;
        harness.Share.RemoteFrameReady += frame => shown = frame;

        harness.Receiver.Emit(new DecodableFrame([4, 5, 6], true, 2));

        Assert.True(await WaitForAsync(() => harness.Decoder.Decoded.Count == 1));
        Assert.True(await WaitForAsync(() => shown is not null));

        harness.Receiver.Emit(new DecodableFrame([7], false, 3));
        Assert.True(await WaitForAsync(() => harness.Decoder.Decoded.Count == 2));
    }

    [Fact]
    public async Task ScreenShareStop_ClearsTheViewerSide()
    {
        await using var harness = new Harness();
        await harness.Share.StartAsync(CancellationToken.None);

        var session = new FakePeerSession { RemoteId = RemoteId };
        harness.Sessions.Open(session);

        session.Receive(MessageType.ScreenShareStart, ShareStart(640, 360, 30));
        session.Receive(MessageType.ScreenShareStop, []);

        Assert.False(harness.Share.IsWatching);
        Assert.True(harness.Decoder.Disposed);
        Assert.Null(harness.Receiver.ExpectedSource);
    }

    [Fact]
    public async Task SessionClose_StopsBothSidesOfTheShare()
    {
        await using var harness = new Harness();
        await harness.Share.StartAsync(CancellationToken.None);

        var session = new FakePeerSession { RemoteId = RemoteId };
        harness.Sessions.Open(session);
        harness.Discovery.Known.Add(new DiscoveredPeer(
            RemoteId, "peer", IPAddress.Loopback, 47101, 47102, DateTimeOffset.UtcNow));

        await harness.Share.StartSharingAsync(RemoteId, 0, new CaptureSettings(), CancellationToken.None);
        session.Receive(MessageType.ScreenShareStart, ShareStart(640, 360, 30));

        harness.Sessions.Close(RemoteId);

        Assert.True(await WaitForAsync(() => !harness.Share.IsSharing));
        Assert.False(harness.Share.IsWatching);
    }
}
