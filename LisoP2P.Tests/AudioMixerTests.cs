using LisoP2P.Core;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class AudioMixerTests
{
    private static readonly PeerId PeerA = TestIds.From(0xA1);
    private static readonly PeerId PeerB = TestIds.From(0xB2);

    private sealed class FakeJitterBuffer : IJitterBuffer
    {
        private readonly Queue<float[]?> _frames = new();

        public int Depth => _frames.Count;
        public int ConcealedFrames => 0;
        public int LateDiscards => 0;
        public int OverflowDiscards => 0;
        public int Pulls { get; private set; }

        public void Enqueue(float[]? frame) => _frames.Enqueue(frame);

        public void Push(uint sequenceNumber, byte[] opusData, long arrivalTicks)
        {
        }

        public float[]? Pull()
        {
            Pulls++;
            return _frames.Count > 0 ? _frames.Dequeue() : null;
        }

        public void Reset() => _frames.Clear();
    }

    private static AudioMixer Build(params (PeerId Peer, FakeJitterBuffer Buffer)[] sources)
    {
        var map = sources.ToDictionary(entry => entry.Peer, entry => entry.Buffer);
        var mixer = new AudioMixer(peer => map.TryGetValue(peer, out var buffer) ? buffer : null);
        mixer.SetActiveSources([.. map.Keys]);
        return mixer;
    }

    [Fact]
    public void MixNextFrame_WithTwoSources_StaysInsideFullScale()
    {
        var a = new FakeJitterBuffer();
        var b = new FakeJitterBuffer();

        a.Enqueue([1f, 1f, -1f]);
        b.Enqueue([1f, 1f, -1f]);

        var mixed = Build((PeerA, a), (PeerB, b)).MixNextFrame();

        Assert.NotNull(mixed);
        Assert.Equal(3, mixed!.Length);
        Assert.All(mixed, sample => Assert.InRange(sample, -1f, 1f));

        // Two identical sources must still be audible, not cancelled or halved into nothing.
        Assert.True(mixed[0] > 0.5f);
        Assert.True(mixed[2] < -0.5f);
    }

    [Fact]
    public void MixNextFrame_SumsBothSources()
    {
        var a = new FakeJitterBuffer();
        var b = new FakeJitterBuffer();

        a.Enqueue([0.4f, 0f]);
        b.Enqueue([0f, 0.4f]);

        var mixed = Build((PeerA, a), (PeerB, b)).MixNextFrame();

        Assert.NotNull(mixed);
        Assert.True(mixed![0] > 0.2f);
        Assert.True(mixed[1] > 0.2f);
    }

    [Fact]
    public void MixNextFrame_WithNoActiveSources_ReturnsSilenceWithoutThrowing()
    {
        var mixer = new AudioMixer(_ => null);
        mixer.SetActiveSources([]);

        Assert.Null(mixer.MixNextFrame());
    }

    [Fact]
    public void MixNextFrame_WithActiveSourcesThatHaveNothing_ReturnsSilence()
    {
        var a = new FakeJitterBuffer();

        Assert.Null(Build((PeerA, a)).MixNextFrame());
    }

    [Fact]
    public void MixNextFrame_PullsEveryActiveSourceEvenWhenOneIsEmpty()
    {
        var a = new FakeJitterBuffer();
        var b = new FakeJitterBuffer();

        a.Enqueue([0.5f]);

        Build((PeerA, a), (PeerB, b)).MixNextFrame();

        // A buffer that is not drained on schedule drifts, so silence must not skip the pull.
        Assert.Equal(1, a.Pulls);
        Assert.Equal(1, b.Pulls);
    }

    [Fact]
    public void MixNextFrame_WithOneSource_HandsTheFrameThroughUnchanged()
    {
        var a = new FakeJitterBuffer();
        var frame = new[] { 0.25f, -0.25f };
        a.Enqueue(frame);

        var mixed = Build((PeerA, a)).MixNextFrame();

        Assert.Same(frame, mixed);
        Assert.Equal([0.25f, -0.25f], mixed);
    }
}
