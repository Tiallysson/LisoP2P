using LisoP2P.Media;

namespace LisoP2P.Net;

public sealed class JitterBuffer : IJitterBuffer
{
    public const int DefaultTargetFrames = 3;
    public const int DefaultMaxFrames = 7;
    public const int DefaultSilenceFrames = 25;

    private readonly IAudioDecoder _decoder;
    private readonly SortedDictionary<uint, byte[]> _frames = [];
    private readonly object _sync = new();
    private readonly int _targetFrames;
    private readonly int _maxFrames;
    private readonly int _silenceFrames;

    private uint _next;
    private bool _playing;
    private int _consecutiveMisses;

    public int Depth
    {
        get
        {
            lock (_sync)
            {
                return _frames.Count;
            }
        }
    }

    public int ConcealedFrames { get; private set; }
    public int LateDiscards { get; private set; }
    public int OverflowDiscards { get; private set; }

    public JitterBuffer(
        IAudioDecoder decoder,
        int targetFrames = DefaultTargetFrames,
        int maxFrames = DefaultMaxFrames,
        int silenceFrames = DefaultSilenceFrames)
    {
        _decoder = decoder;
        _targetFrames = Math.Max(1, targetFrames);
        _maxFrames = Math.Max(_targetFrames, maxFrames);
        _silenceFrames = Math.Max(1, silenceFrames);
    }

    public void Push(uint sequenceNumber, byte[] opusData, long arrivalTicks)
    {
        lock (_sync)
        {
            if (_playing && sequenceNumber < _next)
            {
                LateDiscards++;
                return;
            }

            _frames[sequenceNumber] = opusData;

            TrimOverflow();
        }
    }

    public float[]? Pull()
    {
        byte[]? packet;

        lock (_sync)
        {
            if (!_playing)
            {
                if (_frames.Count < _targetFrames)
                {
                    return null;
                }

                _playing = true;
                _next = _frames.Keys.First();
                _consecutiveMisses = 0;
            }

            if (_frames.Remove(_next, out packet))
            {
                _next++;
                _consecutiveMisses = 0;
            }
            else
            {
                if (_frames.Count == 0 && _consecutiveMisses++ >= _silenceFrames)
                {
                    _playing = false;
                    _consecutiveMisses = 0;
                    return null;
                }

                _next++;
                ConcealedFrames++;
            }
        }

        return packet is null ? _decoder.DecodePacketLoss() : _decoder.Decode(packet);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _frames.Clear();
            _playing = false;
            _next = 0;
            _consecutiveMisses = 0;
        }
    }

    private void TrimOverflow()
    {
        while (_frames.Count > _maxFrames)
        {
            var oldest = _frames.Keys.First();
            _frames.Remove(oldest);
            OverflowDiscards++;

            if (_playing && oldest >= _next)
            {
                _next = oldest + 1;
            }
        }
    }
}
