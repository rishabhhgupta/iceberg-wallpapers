using System.Net;
using System.Text;
using Lively.Sources;
using Lively.Sources.Providers;
using Xunit;

namespace Lively.Sources.Tests;

/// <summary>
/// Serves canned JSON so the tests never touch the internet. Deterministic, free,
/// and works on a plane.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body = body;
        _status = status;
    }

    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}

internal static class Http
{
    public static (HttpClient Client, StubHandler Handler) For(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(body, status);
        return (new HttpClient(handler), handler);
    }

    public static HttpClient Failing(HttpStatusCode status) => For("{}", status).Client;
}

public class WallhavenTests
{
    private const string Body = """
    {
      "data": [
        {
          "id": "abc123",
          "url": "https://wallhaven.cc/w/abc123",
          "path": "https://w.wallhaven.cc/full/ab/wallhaven-abc123.jpg",
          "dimension_x": 3840,
          "dimension_y": 2160,
          "thumbs": { "large": "https://th.wallhaven.cc/lg/ab/abc123.jpg", "small": "https://th.wallhaven.cc/small/ab/abc123.jpg" }
        }
      ],
      "meta": { "current_page": 1, "last_page": 5 }
    }
    """;

    [Fact]
    public async Task Parses_item_and_reports_more_pages()
    {
        var (client, _) = Http.For(Body);
        var source = new WallhavenSource(client, apiKey: null);

        var page = await source.SearchAsync(new WallpaperQuery { Page = 1 });

        var item = Assert.Single(page.Items);
        Assert.Equal("abc123", item.Id);
        Assert.Equal("wallhaven", item.SourceId);
        Assert.Equal(3840, item.Width);
        Assert.Equal("https://th.wallhaven.cc/small/ab/abc123.jpg", item.ThumbnailUrl);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task Never_requests_adult_content()
    {
        var (client, handler) = Http.For(Body);
        var source = new WallhavenSource(client, apiKey: null);

        await source.SearchAsync(new WallpaperQuery());

        Assert.Contains("purity=100", handler.LastRequestUri!.Query);
    }

    [Fact]
    public async Task Last_page_reports_no_more()
    {
        var body = Body.Replace("\"last_page\": 5", "\"last_page\": 1");
        var (client, _) = Http.For(body);
        var source = new WallhavenSource(client, null);

        var page = await source.SearchAsync(new WallpaperQuery());

        Assert.False(page.HasMore);
    }
}

public class UnsplashTests
{
    private const string SearchBody = """
    {
      "total": 100,
      "total_pages": 4,
      "results": [
        {
          "id": "u1",
          "description": "misty mountains",
          "width": 4000,
          "height": 3000,
          "urls": { "raw": "https://images.unsplash.com/raw", "full": "https://images.unsplash.com/full", "small": "https://images.unsplash.com/small" },
          "links": { "html": "https://unsplash.com/photos/u1" },
          "user": { "name": "Ada" }
        }
      ]
    }
    """;

    [Fact]
    public async Task Search_parses_nested_results()
    {
        var (client, _) = Http.For(SearchBody);
        var source = new UnsplashSource(client, "key");

        var page = await source.SearchAsync(new WallpaperQuery { Text = "mountains", Page = 1 });

        var item = Assert.Single(page.Items);
        Assert.Equal("u1", item.Id);
        Assert.Equal("misty mountains", item.Title);
        Assert.Equal("Ada", item.Author);
        Assert.Equal("https://unsplash.com/photos/u1", item.PageUrl);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task Missing_key_is_reported_as_not_configured()
    {
        var (client, _) = Http.For(SearchBody);
        var source = new UnsplashSource(client, null);

        var ex = await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new WallpaperQuery()));
        Assert.Equal(SourceErrorKind.NotConfigured, ex.Kind);
        Assert.False(source.IsConfigured);
    }
}

public class PixabayTests
{
    private const string Body = """
    {
      "total": 500,
      "totalHits": 500,
      "hits": [
        {
          "id": 12345,
          "pageURL": "https://pixabay.com/photos/x-12345/",
          "tags": "sea, calm, blue",
          "previewURL": "https://cdn.pixabay.com/preview.jpg",
          "webformatURL": "https://cdn.pixabay.com/webformat.jpg",
          "largeImageURL": "https://cdn.pixabay.com/large.jpg",
          "imageWidth": 6000,
          "imageHeight": 4000,
          "imageSize": 2500000,
          "user": "photographer"
        }
      ]
    }
    """;

    [Fact]
    public async Task Parses_hits_and_prefers_the_large_image()
    {
        var (client, _) = Http.For(Body);
        var source = new PixabaySource(client, "key");

        var page = await source.SearchAsync(new WallpaperQuery { PerPage = 24 });

        var item = Assert.Single(page.Items);
        Assert.Equal("12345", item.Id);
        Assert.Equal("https://cdn.pixabay.com/large.jpg", item.FullUrl);
        Assert.Equal(2500000, item.FileSizeBytes);
        Assert.True(page.HasMore);
    }
}

public class BingTests
{
    private const string Body = """
    {
      "images": [
        {
          "startdate": "20260102",
          "urlbase": "/th?id=OHR.KuneticeCastle_EN-US9492527404",
          "copyright": "Kunetice Castle, Bohemia (© Marek Saroch/Alamy)",
          "copyrightlink": "https://www.bing.com/search?q=Kunetice+Castle",
          "title": "Where history stands watch",
          "hsh": "91f1c7013a17a3fe22dcc845d15fbee4"
        }
      ]
    }
    """;

    [Fact]
    public async Task Parses_the_daily_picture_and_its_credit()
    {
        var (client, _) = Http.For(Body);
        var source = new BingSource(client);

        var page = await source.SearchAsync(new WallpaperQuery());

        var item = Assert.Single(page.Items);
        Assert.Equal("https://www.bing.com/th?id=OHR.KuneticeCastle_EN-US9492527404_1920x1080.jpg", item.FullUrl);
        Assert.Equal("Where history stands watch", item.Title);
        Assert.Equal("© Marek Saroch/Alamy", item.Author);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Page_two_is_empty_rather_than_repeating()
    {
        var (client, _) = Http.For(Body);
        var source = new BingSource(client);

        var page = await source.SearchAsync(new WallpaperQuery { Page = 2 });

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }
}

public class SpotlightTests
{
    private const string Body = """
    {
      "batchrsp": {
        "items": [
          {
            "item": "{\"ad\":{\"image_fullscreen_001_landscape\":{\"u\":\"https://example.org/spot.jpg\"},\"title_text\":{\"tx\":\"Lake Havasu\"},\"copyright_text\":{\"tx\":\"© Someone / Getty\"},\"title_destination_url\":{\"u\":\"microsoft-edge:https://www.bing.com/spotlight\"}}}"
          },
          {
            "item": "{\"ad\":{\"image_fullscreen_001_landscape\":{\"u\":\"https://example.org/empty.jpg\"}}}"
          }
        ]
      }
    }
    """;

    [Fact]
    public async Task Parses_the_nested_answer_and_skips_empty_slots()
    {
        var (client, _) = Http.For(Body);
        var source = new SpotlightSource(client);

        var page = await source.SearchAsync(new WallpaperQuery());

        var item = Assert.Single(page.Items);
        Assert.Equal("https://example.org/spot.jpg", item.FullUrl);
        Assert.Equal("Lake Havasu", item.Title);
        Assert.Equal("https://www.bing.com/spotlight", item.PageUrl);
    }
}

public class PixabayVideoTests
{
    private const string Body = """
    {
      "totalHits": 100,
      "hits": [
        {
          "id": 9,
          "pageURL": "https://pixabay.com/videos/ocean-9/",
          "tags": "ocean, waves",
          "user": "someone",
          "videos": {
            "large": { "url": "https://cdn.example/large.mp4", "width": 1920, "height": 1080, "size": 20000000, "thumbnail": "https://cdn.example/large.jpg" },
            "tiny": { "url": "https://cdn.example/tiny.mp4", "thumbnail": "https://cdn.example/tiny.jpg" }
          }
        }
      ]
    }
    """;

    [Fact]
    public async Task Video_results_carry_a_small_preview_and_the_full_file_separately()
    {
        var (client, _) = Http.For(Body);
        var source = new PixabaySource(client, "key");

        var page = await source.SearchAsync(new WallpaperQuery { MediaType = WallpaperMediaType.Video });

        var item = Assert.Single(page.Items);
        Assert.Equal(WallpaperMediaType.Video, item.MediaType);
        Assert.Equal("https://cdn.example/large.mp4", item.FullUrl);
        Assert.Equal("https://cdn.example/tiny.mp4", item.PreviewVideoUrl);
        Assert.Equal("https://cdn.example/tiny.jpg", item.ThumbnailUrl);
        Assert.Equal(20000000, item.FileSizeBytes);
    }
}

public class ErrorHandlingTests
{
    [Fact]
    public async Task Rate_limiting_is_named_as_such()
    {
        var source = new BingSource(Http.Failing(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new WallpaperQuery()));

        Assert.Equal(SourceErrorKind.RateLimited, ex.Kind);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task Server_errors_are_retryable_but_do_not_leak_the_url()
    {
        var source = new PixabaySource(Http.Failing(HttpStatusCode.InternalServerError), "secret-key-value");

        var ex = await Assert.ThrowsAsync<SourceException>(() => source.SearchAsync(new WallpaperQuery()));

        Assert.Equal(SourceErrorKind.Http, ex.Kind);
        Assert.DoesNotContain("secret-key-value", ex.Message);
    }

    [Fact]
    public async Task Cancellation_is_reported_as_cancelled()
    {
        var (client, _) = Http.For("{}");
        var source = new BingSource(client);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<SourceException>(
            () => source.SearchAsync(new WallpaperQuery(), cts.Token));

        Assert.Equal(SourceErrorKind.Cancelled, ex.Kind);
        Assert.False(ex.IsRetryable);
    }
}

public class RegistryTests
{
    [Fact]
    public void Unconfigured_key_sources_are_off_by_default()
    {
        var registry = new SourceRegistry(new HttpClient(), new SourceKeys());

        var enabled = registry.Enabled.Select(s => s.Id).ToList();

        Assert.Contains("wallhaven", enabled);
        Assert.Contains("bing", enabled);
        Assert.Contains("spotlight", enabled);
        Assert.DoesNotContain("unsplash", enabled);
        Assert.DoesNotContain("pixabay", enabled);
    }

    [Fact]
    public void A_configured_key_turns_its_source_on()
    {
        var registry = new SourceRegistry(new HttpClient(), new SourceKeys { Unsplash = "abc" });

        Assert.Contains("unsplash", registry.Enabled.Select(s => s.Id));
    }

    [Fact]
    public void Unknown_ids_are_ignored_rather_than_crashing()
    {
        var registry = new SourceRegistry(new HttpClient(), SourceKeys.Defaults, new[] { "bing", "does-not-exist" });

        Assert.Single(registry.Enabled);
        Assert.Equal("bing", registry.Enabled[0].Id);
    }
}
