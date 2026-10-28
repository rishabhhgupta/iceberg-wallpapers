using System.Security.Cryptography;
using System.Text;

namespace Lively.Imaging.Caching;

/// <summary>
/// On-disk cache with a hard size limit and least-recently-used eviction.
///
/// Files are named by the SHA-256 of the URL, so an API key embedded in a query
/// string never appears in a file name. Writes go to a temporary file first and
/// are then moved into place, so a crash cannot leave a half-written image that
/// later decodes to garbage.
/// </summary>
public sealed class DiskImageCache
{
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private long _totalBytes;
    private bool _scanned;

    public DiskImageCache(string directory, long maxBytes = 128L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        _directory = directory;
        _maxBytes = maxBytes;
    }

    public string Directory => _directory;
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public long MaxBytes => _maxBytes;

    public string PathFor(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return System.IO.Path.Combine(_directory, hash[..2], hash + ".img");
    }

    public bool TryGetPath(string url, out string path)
    {
        path = PathFor(url);
        return File.Exists(path);
    }

    public async Task StoreAsync(string url, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var path = PathFor(url);
        var folder = System.IO.Path.GetDirectoryName(path)!;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureScanned();
            System.IO.Directory.CreateDirectory(folder);

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            if (_sizes.TryGetValue(path, out var previous)) _totalBytes -= previous;
            _sizes[path] = bytes.Length;
            _totalBytes += bytes.Length;

            EvictUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]?> ReadAsync(string url, CancellationToken cancellationToken = default)
    {
        var path = PathFor(url);
        if (!File.Exists(path)) return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureScanned();
                if (_sizes.TryGetValue(path, out var previous)) _totalBytes -= previous;
                _sizes[path] = bytes.Length;
                _totalBytes += bytes.Length;
            }
            finally
            {
                _gate.Release();
            }

            // Touch, so LRU reflects real use rather than write time.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }

            return bytes;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (System.IO.Directory.Exists(_directory))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch (IOException) { }
                }
            }

            _sizes.Clear();
            _totalBytes = 0;
            _scanned = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureScanned()
    {
        if (_scanned) return;
        _scanned = true;
        _sizes.Clear();
        _totalBytes = 0;

        if (!System.IO.Directory.Exists(_directory)) return;

        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.img", SearchOption.AllDirectories))
        {
            try
            {
                var length = new FileInfo(file).Length;
                _sizes[file] = length;
                _totalBytes += length;
            }
            catch (IOException) { }
        }
    }

    /// <summary>Caller must hold the gate.</summary>
    private void EvictUnsafe()
    {
        if (_totalBytes <= _maxBytes) return;

        var ordered = _sizes
            .Select(kv => (Path: kv.Key, Size: kv.Value, Used: SafeLastWrite(kv.Key)))
            .OrderBy(x => x.Used)
            .ToList();

        foreach (var entry in ordered)
        {
            if (_totalBytes <= _maxBytes) break;

            try { File.Delete(entry.Path); } catch (IOException) { continue; }

            _sizes.Remove(entry.Path);
            _totalBytes -= entry.Size;
        }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch (IOException) { return DateTime.MinValue; }
    }
}
