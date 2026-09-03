using LisoP2P.Core.Protocol;
using LisoP2P.Media;

namespace LisoP2P.Net;

public sealed class FrameReassembler
{
    public const int DefaultWindowSize = 8;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(200);

    private sealed class Pending
    {
        public required byte[]?[] Fragments { get; init; }
        public required DateTimeOffset FirstSeenAt { get; init; }
        public required bool IsKeyframe { get; init; }
        public int ReceivedCount { get; set; }
        public int TotalBytes { get; set; }
    }

    private readonly Dictionary<uint, Pending> _pending = [];
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _windowSize;
    private readonly TimeSpan _timeout;
    private readonly object _sync = new();

    private uint _lastDecidedFrameId;
    private bool _hasDecided;

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    private int _droppedFrames;

    public int DroppedFrames => Volatile.Read(ref _droppedFrames);

    public event Action<DecodableFrame>? FrameReassembled;
    public event Action? FrameDropped;

    public FrameReassembler(
        Func<DateTimeOffset>? clock = null,
        int windowSize = DefaultWindowSize,
        TimeSpan? timeout = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _windowSize = Math.Max(1, windowSize);
        _timeout = timeout ?? DefaultTimeout;
    }

    public void Add(in MediaPacketHeader header, ReadOnlySpan<byte> payload)
    {
        DecodableFrame? completed = null;
        var dropped = 0;

        lock (_sync)
        {
            if (_hasDecided && header.FrameId <= _lastDecidedFrameId)
            {
                return;
            }

            if (!_pending.TryGetValue(header.FrameId, out var pending))
            {
                dropped += EvictOldestIfFull();

                pending = new Pending
                {
                    Fragments = new byte[]?[header.FragmentCount],
                    FirstSeenAt = _clock(),
                    IsKeyframe = header.IsKeyframe,
                };
                _pending[header.FrameId] = pending;
            }

            if (pending.Fragments.Length != header.FragmentCount || pending.Fragments[header.FragmentIndex] is not null)
            {
                RaiseDropped(dropped);
                return;
            }

            pending.Fragments[header.FragmentIndex] = payload.ToArray();
            pending.ReceivedCount++;
            pending.TotalBytes += payload.Length;

            if (pending.ReceivedCount == pending.Fragments.Length)
            {
                completed = Assemble(header.FrameId, pending);
                _pending.Remove(header.FrameId);
                dropped += MarkDecided(header.FrameId);
            }
        }

        RaiseDropped(dropped);

        if (completed is { } frame)
        {
            FrameReassembled?.Invoke(frame);
        }
    }

    public void CollectExpired()
    {
        var dropped = 0;

        lock (_sync)
        {
            var now = _clock();

            foreach (var (frameId, pending) in _pending.ToArray())
            {
                if (now - pending.FirstSeenAt < _timeout)
                {
                    continue;
                }

                _pending.Remove(frameId);
                dropped++;
                dropped += MarkDecided(frameId);
            }
        }

        RaiseDropped(dropped);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _pending.Clear();
            _hasDecided = false;
            _lastDecidedFrameId = 0;
        }
    }

    private int EvictOldestIfFull()
    {
        if (_pending.Count < _windowSize)
        {
            return 0;
        }

        var oldest = uint.MaxValue;

        foreach (var frameId in _pending.Keys)
        {
            if (frameId < oldest)
            {
                oldest = frameId;
            }
        }

        _pending.Remove(oldest);
        return 1 + MarkDecided(oldest);
    }

    private int MarkDecided(uint frameId)
    {
        if (_hasDecided && frameId <= _lastDecidedFrameId)
        {
            return 0;
        }

        _hasDecided = true;
        _lastDecidedFrameId = frameId;

        var stale = _pending.Keys.Where(id => id < frameId).ToArray();

        foreach (var id in stale)
        {
            _pending.Remove(id);
        }

        return stale.Length;
    }

    private static DecodableFrame Assemble(uint frameId, Pending pending)
    {
        var data = new byte[pending.TotalBytes];
        var offset = 0;

        foreach (var fragment in pending.Fragments)
        {
            fragment!.CopyTo(data, offset);
            offset += fragment.Length;
        }

        return new DecodableFrame(data, pending.IsKeyframe, frameId);
    }

    private void RaiseDropped(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _droppedFrames, count);

        for (var i = 0; i < count; i++)
        {
            FrameDropped?.Invoke();
        }
    }
}
