using System.Text.Json;
using Lively.Sources.Http;

namespace Lively.Sources.Providers;

/// <summary>
/// Windows Spotlight. Microsoft publishes no API for this; the app asks the same
/// address Windows itself asks. It can change without warning, so a failure here
/// is quiet: the other sources still fill the feed.
/// </summary>
public sealed class SpotlightSource : IWallpaperSource
{
    private const string Endpoint = "https://arc.msn.com/v3/Delivery/Placement";

    // Each locale returns a few pictures. Asking several gives a feed worth
    // scrolling instead of the same three images every day.
    private static readonly (string Country, string Locale)[] Markets =
    [
        ("US", "en-US"),
        ("GB", "en-GB"),
        ("IN", "en-IN"),
        ("AU", "en-AU"),
        ("CA", "en-CA"),
        ("DE", "de-DE"),
        ("FR", "fr-FR"),
        ("JP", "ja-JP"),
    ];

    private readonly HttpClient _http;

    public SpotlightSource(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string Id => "spotlight";
    public string DisplayName => "Spotlight";
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;

    public IReadOnlyList<WallpaperMediaType> MediaTypes => [WallpaperMediaType.Photo];

    public IReadOnlyList<SourceFilterGroup> Filters => [];

    public async Task<WallpaperPage> SearchAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // There is no paging; one request per market is all Microsoft offers.
        if (query.Page > 1)
        {
            return WallpaperPage.Empty(query.Page);
        }

        var items = new List<WallpaperItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var answered = 0;

        foreach (var (country, locale) in Markets)
        {
            try
            {
                var url = HttpJson.WithQuery(
                    Endpoint,
                    ("pid", "209567"),
                    ("fmt", "json"),
                    ("rafb", "0"),
                    ("ctry", country),
                    ("lc", locale),
                    ("cdm", "1"),
                    ("tp", "1"));

                using var doc = await HttpJson.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
                answered++;
                ReadBatch(doc.RootElement, seen, items);
            }
            catch (SourceException) when (!cancellationToken.IsCancellationRequested)
            {
                // One market refusing is normal. The rest still answer.
            }
        }

        if (answered == 0 && !cancellationToken.IsCancellationRequested)
        {
            throw new SourceException(SourceErrorKind.Http, "Spotlight is not answering right now.");
        }

        return new WallpaperPage
        {
            Items = items,
            Page = 1,
            HasMore = false,
        };
    }

    private void ReadBatch(JsonElement root, HashSet<string> seen, List<WallpaperItem> items)
    {
        if (!root.TryGetProperty("batchrsp", out var batch)) return;
        if (!batch.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array) return;

        foreach (var wrapper in list.EnumerateArray())
        {
            // Each entry is a JSON document encoded as a string.
            var raw = HttpJson.String(wrapper, "item");
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JsonDocument inner;
            try
            {
                inner = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                continue;
            }

            using (inner)
            {
                if (!inner.RootElement.TryGetProperty("ad", out var ad)) continue;

                var image = HttpJson.Object(ad, "image_fullscreen_001_landscape");
                var full = image is { } img ? HttpJson.String(img, "u") : null;
                if (string.IsNullOrWhiteSpace(full)) continue;

                // Microsoft pads unused slots with a 736-byte "empty.jpg".
                if (full.EndsWith("empty.jpg", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(full!)) continue;

                var title = Text(ad, "title_text");
                var credit = Text(ad, "copyright_text");
                var page = Link(ad, "title_destination_url");

                items.Add(new WallpaperItem
                {
                    Id = StableId(full!),
                    SourceId = Id,
                    FullUrl = full!,
                    ThumbnailUrl = full,
                    Title = title,
                    Author = credit,
                    PageUrl = page,
                    Width = 1920,
                    Height = 1080,
                    MediaType = WallpaperMediaType.Photo,
                });
            }
        }
    }

    private static string? Text(JsonElement parent, string name)
    {
        var value = HttpJson.Object(parent, name);
        return value is { } v ? HttpJson.String(v, "tx") : null;
    }

    private static string? Link(JsonElement parent, string name)
    {
        var value = HttpJson.Object(parent, name);
        var url = value is { } v ? HttpJson.String(v, "u") : null;
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Microsoft wraps these in a protocol handler; keep the plain address.
        const string prefix = "microsoft-edge:";
        return url!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? url[prefix.Length..] : url;
    }

    /// <summary>A short, stable key derived from the picture address.</summary>
    private static string StableId(string url)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
