using System.Net;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class VoiceSessionTests
{
    private static readonly PeerId LocalId = new(new Guid("33333333-3333-3333-3333-333333333333"));
    private static readonly PeerId RemoteId = new(new Guid("44444444-4444-4444-4444-444444444444"));

    private sealed class FakeAudioCapture : IAudioCapture
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public AudioSettings? LastSettings { get; private set; }

        public event Action<AudioFrame>? FrameCaptured;
        public event Action<Exception>? Failed;

        public void Start(AudioSettings settings)
        {
            IsRunning = true;
            StartCount++;
            LastSettings = settings;
        }

        public void Stop()
        {
            IsRunning = false;
            StopCount++;
        }

        public void Emit(float[] samples) => FrameCaptured?.Invoke(new AudioFrame(samples, 0));

        public void Fail(Exception error) => Failed?.Invoke(error);

        public void Dispose() => Stop();
    }

    private sealed class FakeAudioPlayback : IAudioPlayback
    {
        public bool IsRunning { get; private set; }
        public float Volume { get; set; } = 1f;
        public Func<float[]?>? Source { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public void Start(AudioSettings settings)
        {
            IsRunning = true;
            StartCount++;
        }

        public void Stop()
        {
            IsRunning = false;
            StopCount++;
        }

        public void SetSource(Func<float[]?> pullCallback) => Source = pullCallback;

        public void Dispose() => Stop();
    }

    private sealed class FakeAudioEncoder : IAudioEncoder
    {
        public List<float[]> Encoded { get; } = [];
        public bool Disposed { get; private set; }

        public byte[] Encode(AudioFrame frame)
        {
            Encoded.Add(frame.Samples);
            return [(byte)frame.Samples.Length];
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeAudioDecoder : IAudioDecoder
    {
        public bool Disposed { get; private set; }

        public float[] Decode(byte[] opusData) => [opusData[0]];

        public float[] DecodePacketLoss() => [-1f];

        public void Dispose() => Disposed = true;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeSessionManager Sessions { get; } = new();
        public FakeDiscoveryWithPeers Discovery { get; } = new();
        public FakeMediaSender Sender { get; } = new();
        public FakeMediaReceiver Receiver { get; } = new();
        public FakeAudioCapture Capture { get; } = new();
        public FakeAudioPlayback Playback { get; } = new();
        public FakeAudioEncoder Encoder { get; } = new();
        public FakeAudioDecoder Decoder { get; } = new();
        public VoiceSession Voice { get; }

        public Harness()
        {
            Voice = new VoiceSession(
                new NetworkOptions { MediaPort = 51102 },
                new StubIdentityStore(LocalId.Value, "local"),
                Discovery,
                Sessions,
                Sender,
                Receiver,
                Capture,
                Playback,
                NullMediaLogger.Instance,
                _ => Encoder,
                _ => Decoder);
        }

        public FakePeerSession OpenSession(int mediaPort = 47102)
        {
            var session = new FakePeerSession { RemoteId = RemoteId };
            Sessions.Open(session);
            Discovery.Known.Add(new DiscoveredPeer(
                RemoteId, "peer", IPAddress.Parse("192.168.0.9"), 47101, mediaPort, DateTimeOffset.UtcNow));

            return session;
        }

        public ValueTask DisposeAsync() => Voice.DisposeAsync();
    }

    private static byte[] VoiceStart(int sampleRate = 48000, int frameSamples = 960) =>
        VoicePayloadCodec.Encode(new VoicePayload
        {
            SampleRate = sampleRate,
            Channels = 1,
            FrameSamples = frameSamples,
        });

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task StartVoice_AnnouncesTheFormatAndStartsPlaybackWithoutCapturing()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);

        var session = harness.OpenSession();

        await harness.Voice.StartVoiceAsync([RemoteId], new AudioSettings(), CancellationToken.None);

        var start = Assert.Single(session.Sent, envelope => envelope.Type == MessageType.VoiceStart);
        Assert.True(VoicePayloadCodec.TryDecode(start.Payload, out var payload));
        Assert.Equal(48000, payload!.SampleRate);
        Assert.Equal(960, payload.FrameSamples);

        Assert.True(harness.Voice.IsActive);
        Assert.True(harness.Playback.IsRunning);
        Assert.False(harness.Capture.IsRunning);
        Assert.False(harness.Voice.IsTransmitting);
    }

    [Fact]
    public async Task PushToTalk_StartsCaptureAndSendsEncodedAudioToThePeerMediaPort()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);
        harness.OpenSession(mediaPort: 47999);

        await harness.Voice.StartVoiceAsync([RemoteId], new AudioSettings(), CancellationToken.None);

        harness.Voice.SetTransmitting(true);
        Assert.True(harness.Capture.IsRunning);

        harness.Capture.Emit(new float[960]);

        var sent = Assert.Single(harness.Sender.AudioSent);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.0.9"), 47999), sent.Destination);
        Assert.Single(harness.Encoder.Encoded);
    }

    [Fact]
    public async Task ReleasingPushToTalk_StopsCapturingAndEncoding()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);
        harness.OpenSession();

        await harness.Voice.StartVoiceAsync([RemoteId], new AudioSettings(), CancellationToken.None);

        harness.Voice.SetTransmitting(true);
        harness.Capture.Emit(new float[960]);
        harness.Voice.SetTransmitting(false);

        Assert.False(harness.Capture.IsRunning);
        Assert.Equal(1, harness.Capture.StopCount);

        harness.Capture.Emit(new float[960]);

        Assert.Single(harness.Sender.AudioSent);
        Assert.Single(harness.Encoder.Encoded);
    }

    [Fact]
    public async Task SetTransmitting_IsIgnoredWhenNoCallIsActive()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);

        harness.Voice.SetTransmitting(true);

        Assert.False(harness.Voice.IsTransmitting);
        Assert.False(harness.Capture.IsRunning);
    }

    [Fact]
    public async Task VoiceStart_FromThePeer_StartsPlaybackEvenWithoutALocalCall()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);

        var session = harness.OpenSession();
        session.Receive(MessageType.VoiceStart, VoiceStart());

        Assert.True(harness.Voice.IsReceiving);
        Assert.True(harness.Playback.IsRunning);
        Assert.False(harness.Voice.IsActive);
    }

    [Fact]
    public async Task ReceivedAudio_ReachesPlaybackThroughTheJitterBuffer()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);

        var session = harness.OpenSession();
        session.Receive(MessageType.VoiceStart, VoiceStart());

        Assert.Null(harness.Playback.Source!());

        harness.Receiver.EmitAudio(RemoteId, 1, [11]);
        harness.Receiver.EmitAudio(RemoteId, 2, [12]);
        harness.Receiver.EmitAudio(RemoteId, 3, [13]);

        Assert.Equal([11f], harness.Playback.Source!());
        Assert.Equal([12f], harness.Playback.Source!());
        Assert.True(harness.Voice.IsPeerSpeaking);
    }

    [Fact]
    public async Task VoiceStop_FromThePeer_StopsReceivingAndReleasesTheDecoder()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);

        var session = harness.OpenSession();
        session.Receive(MessageType.VoiceStart, VoiceStart());
        session.Receive(MessageType.VoiceStop, []);

        Assert.False(harness.Voice.IsReceiving);
        Assert.True(harness.Decoder.Disposed);
        Assert.False(harness.Playback.IsRunning);
    }

    [Fact]
    public async Task StopVoice_TellsThePeerAndStopsEverythingLocally()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);

        var session = harness.OpenSession();

        await harness.Voice.StartVoiceAsync([RemoteId], new AudioSettings(), CancellationToken.None);
        harness.Voice.SetTransmitting(true);
        await harness.Voice.StopVoiceAsync();

        Assert.False(harness.Voice.IsActive);
        Assert.False(harness.Voice.IsTransmitting);
        Assert.False(harness.Capture.IsRunning);
        Assert.False(harness.Playback.IsRunning);
        Assert.True(harness.Encoder.Disposed);
        Assert.Contains(session.Sent, envelope => envelope.Type == MessageType.VoiceStop);
    }

    [Fact]
    public async Task SessionClose_EndsTheCall()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);

        var session = harness.OpenSession();

        await harness.Voice.StartVoiceAsync([RemoteId], new AudioSettings(), CancellationToken.None);
        session.Receive(MessageType.VoiceStart, VoiceStart());

        harness.Sessions.Close(RemoteId);

        Assert.True(await WaitForAsync(() => !harness.Voice.IsActive));
        Assert.False(harness.Voice.IsReceiving);
    }

    [Fact]
    public async Task CaptureFailure_StopsTransmittingInsteadOfKillingTheCall()
    {
        await using var harness = new Harness();
        await harness.Voice.StartAsync(CancellationToken.None);
        harness.OpenSession();

        await harness.Voice.StartVoiceAsync([RemoteId], new AudioSettings(), CancellationToken.None);
        harness.Voice.SetTransmitting(true);

        harness.Capture.Fail(new InvalidOperationException("device gone"));

        Assert.False(harness.Voice.IsTransmitting);
        Assert.True(harness.Voice.IsActive);
    }
}
