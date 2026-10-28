using System.Collections.Concurrent;
using Lively.Imaging.Caching;

namespace Lively.Imaging.Loading;

/// <summary>
/// Loads image bytes with a memory cache, an optional disk cache, and request
/// de-duplication.
///
/// The de-duplication matters more than it sounds: fast scrolling re-requests
/// the same thumbnail several times as tiles are recycled. Without it, one photo
/// becomes five downloads. Here, concurrent callers for the same URL share a
/// single request.
///
/// Cancellation only detaches the caller. The download keeps going, so scrolling
/// back to the same tile is instant instead of starting over.
/// </summary>
public sealed class ImageLoader
{
    private readonly HttpClient _http;
    private readonly MemoryImageCache _memory;
    private readonly DiskImageCache? _disk;
    private readonly long _maxDownloadBytes;

    // Lazy guarantees that a losing racer never actually starts a download:
    // only the instance stored in the dictionary is ever realised.
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _inFlight = new(StringComparer.Ordinal);

    // Picture sites rate-limit bursty clients. Six at a time keeps a scrolling
    // grid responsive without looking like an attack.
    private readonly SemaphoreSlim _gate = new(6, 6);

    public ImageLoader(
        HttpClient httpClient,
        MemoryImageCache? memory = null,
        DiskImageCache? disk = null,
        long maxDownloadBytes = 32L * 1024 * 1024)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _memory = memory ?? new MemoryImageCache();
        _disk = disk;
        _maxDownloadBytes = maxDownloadBytes;
    }

    public MemoryImageCache Memory => _memory;
    public DiskImageCache? Disk => _disk;

    /// <summary>Number of URLs currently downloading. Exposed for tests and diagnostics.</summary>
    public int InFlightCount => _inFlight.Count;

    public async Task<byte[]?> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (_memory.TryGet(url, out var cached)) return cached;

        var entry = _inFlight.GetOrAdd(url, key => new Lazy<Task<byte[]?>>(
            () => RunAsync(key),
            LazyThreadSafetyMode.ExecutionAndPublication));

        // WaitAsync only cancels this caller. The shared download is unaffected,
        // which is why it is started with CancellationToken.None.
        return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> RunAsync(string url)
    {
        try
        {
            return await LoadCoreAsync(url, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // The result is already in the memory cache by now, so a caller that
            // arrives a moment later still gets a hit rather than a new download.
            _inFlight.TryRemove(url, out _);
        }
    }

    private async Task<byte[]?> LoadCoreAsync(string url, CancellationToken cancellationToken)
    {
        if (_disk is not null)
        {
            var fromDisk = await _disk.ReadAsync(url, cancellationToken).ConfigureAwait(false);
            if (fromDisk is { Length: > 0 })
            {
                _memory.Set(url, fromDisk);
                return fromDisk;
            }
        }

        byte[]? bytes;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bytes = await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (HttpRequestException)
        {
            // A failed image must not poison the grid: the tile shows its
            // placeholder and the next scroll can retry.
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }

        if (bytes is null or { Length: 0 }) return null;

        _memory.Set(url, bytes);
        if (_disk is not null)
        {
            try { await _disk.StoreAsync(url, bytes, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { }
        }

        return bytes;
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        var declared = response.Content.Headers.ContentLength;
        if (declared > _maxDownloadBytes) return null;

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream(declared is > 0 ? (int)declared.Value : 64 * 1024);
        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _maxDownloadBytes) return null; // refuse absurd files
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
