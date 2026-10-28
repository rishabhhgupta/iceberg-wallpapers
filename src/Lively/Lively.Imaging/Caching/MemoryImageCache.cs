namespace Lively.Imaging.Caching;

/// <summary>
/// In-memory LRU cache of decoded image bytes, capped by total size rather than
/// entry count. A byte cap is the only way to survive someone scrolling a feed
/// of 4K photos: ten entries of 8 MB is very different from ten thumbnails.
/// </summary>
public sealed class MemoryImageCache
{
    private sealed record Entry(string Key, byte[] Bytes);

    private readonly long _maxBytes;
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private readonly object _sync = new();
    private long _totalBytes;

    public MemoryImageCache(long maxBytes = 96L * 1024 * 1024)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    public long TotalBytes
    {
        get { lock (_sync) return _totalBytes; }
    }

    public int Count
    {
        get { lock (_sync) return _map.Count; }
    }

    public bool TryGet(string key, out byte[] bytes)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    public void Set(string key, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(bytes);

        // A single item larger than the whole cache would evict everything and
        // then still not fit, so it is simply not cached.
        if (bytes.Length > _maxBytes) return;

        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _totalBytes -= existing.Value.Bytes.Length;
                _lru.Remove(existing);
                _map.Remove(key);
            }

            var node = _lru.AddFirst(new Entry(key, bytes));
            _map[key] = node;
            _totalBytes += bytes.Length;

            while (_totalBytes > _maxBytes && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _totalBytes -= last.Value.Bytes.Length;
            }
        }
    }

    public void Remove(string key)
    {
        lock (_sync)
        {
            if (!_map.TryGetValue(key, out var node)) return;
            _lru.Remove(node);
            _map.Remove(key);
            _totalBytes -= node.Value.Bytes.Length;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _lru.Clear();
            _totalBytes = 0;
        }
    }
}
