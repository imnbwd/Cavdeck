namespace Cavdeck.App.Features.Explorer;

/// <summary>
/// A small, byte-budgeted LRU. Access is synchronized because thumbnail decoding
/// may finish on different worker threads. Entries are not pinned by the cache.
/// </summary>
public sealed class ThumbnailLruCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(TKey Key, TValue Value, long Bytes);

    private readonly object _gate = new();
    private readonly long _budget;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _items = new();
    private readonly LinkedList<Entry> _recent = new();
    private long _bytes;

    public ThumbnailLruCache(long byteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        _budget = byteBudget;
    }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var node))
            {
                value = default;
                return false;
            }

            _recent.Remove(node);
            _recent.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Put(TKey key, TValue value, long estimatedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(estimatedBytes);
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var existing))
                Remove(existing);
            if (estimatedBytes > _budget)
                return;

            var node = _recent.AddFirst(new Entry(key, value, estimatedBytes));
            _items.Add(key, node);
            _bytes += estimatedBytes;

            while (_bytes > _budget && _recent.Last is { } oldest)
                Remove(oldest);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _recent.Clear();
            _items.Clear();
            _bytes = 0;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _recent.Remove(node);
        _items.Remove(node.Value.Key);
        _bytes -= node.Value.Bytes;
    }
}
