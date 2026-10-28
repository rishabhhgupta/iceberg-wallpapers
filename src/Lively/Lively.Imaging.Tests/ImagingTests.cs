using System.Net;
using Lively.Imaging.Caching;
using Lively.Imaging.Loading;
using Lively.Imaging.Processing;
using SkiaSharp;
using Xunit;

namespace Lively.Imaging.Tests;

internal sealed class CountingHandler : HttpMessageHandler
{
    private readonly byte[] _payload;
    private readonly HttpStatusCode _status;

    public CountingHandler(byte[] payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        _payload = payload;
        _status = status;
    }

    public int Calls;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        // Yield so concurrent callers genuinely overlap.
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);

        return new HttpResponseMessage(_status)
        {
            Content = new ByteArrayContent(_payload),
        };
    }
}

internal static class TestImage
{
    public static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(10, 40, 90));
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

public class MemoryCacheTests
{
    [Fact]
    public void Oldest_entry_is_evicted_when_over_the_cap()
    {
        var cache = new MemoryImageCache(maxBytes: 100);
        cache.Set("a", new byte[40]);
        cache.Set("b", new byte[40]);
        cache.Set("c", new byte[40]);

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TotalBytes <= 100);
    }

    [Fact]
    public void Reading_an_entry_makes_it_recent()
    {
        var cache = new MemoryImageCache(maxBytes: 100);
        cache.Set("a", new byte[40]);
        cache.Set("b", new byte[40]);

        Assert.True(cache.TryGet("a", out _)); // a becomes newest
        cache.Set("c", new byte[40]);

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void An_item_larger_than_the_whole_cache_is_not_stored()
    {
        var cache = new MemoryImageCache(maxBytes: 50);
        cache.Set("big", new byte[51]);

        Assert.False(cache.TryGet("big", out _));
        Assert.Equal(0, cache.TotalBytes);
    }

    [Fact]
    public void Setting_the_same_key_twice_does_not_double_count()
    {
        var cache = new MemoryImageCache(maxBytes: 1000);
        cache.Set("a", new byte[100]);
        cache.Set("a", new byte[100]);

        Assert.Equal(100, cache.TotalBytes);
        Assert.Equal(1, cache.Count);
    }
}

public class DiskCacheTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "lively-img-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Store_then_read_round_trips()
    {
        var dir = TempDir();
        try
        {
            var cache = new DiskImageCache(dir, maxBytes: 10_000);
            await cache.StoreAsync("https://example.invalid/a.jpg", new byte[] { 1, 2, 3 });

            var bytes = await cache.ReadAsync("https://example.invalid/a.jpg");
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void File_names_do_not_leak_api_keys_from_urls()
    {
        var dir = TempDir();
        try
        {
            var cache = new DiskImageCache(dir);
            var path = cache.PathFor("https://wallhaven.cc/api/v1/search?apikey=SUPERSECRET");

            Assert.DoesNotContain("SUPERSECRET", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wallhaven", path, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Exceeding_the_cap_evicts_until_it_fits()
    {
        var dir = TempDir();
        try
        {
            var cache = new DiskImageCache(dir, maxBytes: 1000);
            await cache.StoreAsync("https://example.invalid/a.jpg", new byte[600]);
            await cache.StoreAsync("https://example.invalid/b.jpg", new byte[600]);

            Assert.True(cache.TotalBytes <= 1000);

            var files = Directory.GetFiles(dir, "*.img", SearchOption.AllDirectories);
            Assert.Single(files);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Clearing_removes_everything()
    {
        var dir = TempDir();
        try
        {
            var cache = new DiskImageCache(dir);
            await cache.StoreAsync("https://example.invalid/a.jpg", new byte[10]);
            await cache.ClearAsync();

            Assert.Equal(0, cache.TotalBytes);
            Assert.Empty(Directory.GetFiles(dir, "*.img", SearchOption.AllDirectories));
        }
        finally { Cleanup(dir); }
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}

public class ImageLoaderTests
{
    [Fact]
    public async Task Concurrent_requests_for_one_url_download_once()
    {
        var handler = new CountingHandler(new byte[] { 9, 9, 9 });
        var loader = new ImageLoader(new HttpClient(handler));

        var tasks = Enumerable.Range(0, 6).Select(_ => loader.GetAsync("https://example.invalid/x.jpg")).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, handler.Calls);
        Assert.All(results, r => Assert.Equal(new byte[] { 9, 9, 9 }, r));
    }

    [Fact]
    public async Task A_second_request_is_served_from_memory()
    {
        var handler = new CountingHandler(new byte[] { 1 });
        var loader = new ImageLoader(new HttpClient(handler));

        await loader.GetAsync("https://example.invalid/x.jpg");
        await loader.GetAsync("https://example.invalid/x.jpg");

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_failed_download_returns_null_instead_of_throwing()
    {
        var handler = new CountingHandler(Array.Empty<byte>(), HttpStatusCode.NotFound);
        var loader = new ImageLoader(new HttpClient(handler));

        var result = await loader.GetAsync("https://example.invalid/missing.jpg");

        Assert.Null(result);
    }

    [Fact]
    public async Task Cancelling_a_caller_does_not_poison_the_cache()
    {
        var handler = new CountingHandler(new byte[] { 5, 5 });
        var loader = new ImageLoader(new HttpClient(handler));

        using var cts = new CancellationTokenSource();
        var cancelled = loader.GetAsync("https://example.invalid/x.jpg", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // The download continued in the background, so this is now a hit.
        var again = await loader.GetAsync("https://example.invalid/x.jpg");
        Assert.Equal(new byte[] { 5, 5 }, again);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task In_flight_count_returns_to_zero()
    {
        var handler = new CountingHandler(new byte[] { 1 });
        var loader = new ImageLoader(new HttpClient(handler));

        await loader.GetAsync("https://example.invalid/x.jpg");
        await Task.Delay(20);

        Assert.Equal(0, loader.InFlightCount);
    }
}

public class ThumbnailerTests
{
    [Fact]
    public void Large_images_are_downscaled_to_the_target_width()
    {
        var png = TestImage.Png(400, 200);

        var resized = Thumbnailer.Resize(png, 100);
        Assert.NotNull(resized);

        var dimensions = Thumbnailer.Dimensions(resized!);
        Assert.NotNull(dimensions);
        Assert.Equal(100, dimensions!.Value.Width);
        Assert.Equal(50, dimensions.Value.Height);
    }

    [Fact]
    public void Small_images_are_returned_untouched()
    {
        var png = TestImage.Png(50, 50);
        var resized = Thumbnailer.Resize(png, 200);

        Assert.Same(png, resized);
    }

    [Fact]
    public void Garbage_returns_null_rather_than_throwing()
    {
        Assert.Null(Thumbnailer.Resize(new byte[] { 0, 1, 2, 3 }, 100));
        Assert.Null(Thumbnailer.Dimensions(new byte[] { 0, 1, 2, 3 }));
    }
}
