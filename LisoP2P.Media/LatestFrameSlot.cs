namespace LisoP2P.Media;

public sealed class LatestFrameSlot<T>
{
    private readonly object _sync = new();
    private readonly Action<T>? _onDropped;
    private T? _pending;
    private bool _hasPending;
    private bool _closed;

    public int DroppedCount { get; private set; }

    public LatestFrameSlot(Action<T>? onDropped = null)
    {
        _onDropped = onDropped;
    }

    public void Publish(T item)
    {
        T? dropped = default;
        var hasDropped = false;

        lock (_sync)
        {
            if (_closed)
            {
                _onDropped?.Invoke(item);
                return;
            }

            if (_hasPending)
            {
                dropped = _pending;
                hasDropped = true;
                DroppedCount++;
            }

            _pending = item;
            _hasPending = true;
            Monitor.Pulse(_sync);
        }

        if (hasDropped && dropped is not null)
        {
            _onDropped?.Invoke(dropped);
        }
    }

    public bool TryTake(int timeoutMilliseconds, out T item)
    {
        lock (_sync)
        {
            if (!_hasPending && !_closed)
            {
                Monitor.Wait(_sync, timeoutMilliseconds);
            }

            if (!_hasPending)
            {
                item = default!;
                return false;
            }

            item = _pending!;
            _pending = default;
            _hasPending = false;
            return true;
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            _closed = true;

            if (_hasPending && _pending is not null)
            {
                _onDropped?.Invoke(_pending);
            }

            _pending = default;
            _hasPending = false;
            Monitor.PulseAll(_sync);
        }
    }
}
