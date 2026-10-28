using System.Text.Json;
using Lively.Sources.Http;

namespace Lively.Sources.Providers;

/// <summary>
/// Pixabay. Needs a key. Serves photos, illustrations and videos; the endpoint
/// and the shape of the answer differ, so the mapping is kept in one place.
/// </summary>
public sealed class PixabaySource : IWallpaperSource
{
    private const string ImageEndpoint = "https://pixabay.com/api/";
    private const string VideoEndpoint = "https://pixabay.com/api/videos/";
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public PixabaySource(HttpClient http, string? apiKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    public string Id => "pixabay";
    public string DisplayName => "Pixabay";
    public bool RequiresApiKey => true;
    public bool IsConfigured => _apiKey is not null;

    public IReadOnlyList<WallpaperMediaType> MediaTypes { get; } =
        [WallpaperMediaType.Photo, WallpaperMediaType.Illustration, WallpaperMediaType.Video];

    public IReadOnlyList<SourceFilterGroup> Filters { get; } =
    [
        SourceFilterGroup.Of("category", "Category",
            "all",
            new SourceOption("all", "All"),
            new SourceOption("backgrounds", "Backgrounds"),
            new SourceOption("nature", "Nature"),
            new SourceOption("travel", "Travel"),
            new SourceOption("animals", "Animals"),
            new SourceOption("buildings", "Buildings"),
            new SourceOption("computer", "Computer"),
            new SourceOption("people", "People"),
            new SourceOption("food", "Food"),
            new SourceOption("sports", "Sports")),
    ];

    public async Task<WallpaperPage> SearchAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_apiKey is null)
        {
            throw new SourceException(SourceErrorKind.NotConfigured, "Pixabay needs an API key.");
        }

        var video = query.MediaType == WallpaperMediaType.Video;
        var page = Math.Max(1, query.Page);
        var perPage = Math.Clamp(query.PerPage, 3, video ? 50 : 100);

        // Pixabay has no random order. A random first page is the next best
        // thing and keeps the "Random" button from always showing the same row.
        if (query.Sort == WallpaperSort.Random
            && string.IsNullOrWhiteSpace(query.Text)
            && query.Page == 1)
        {
            page = Random.Shared.Next(1, 11);
        }

        var order = query.Sort == WallpaperSort.Newest ? "latest" : "popular";
        var orientation = query.Orientation switch
        {
            WallpaperOrientation.Landscape => "horizontal",
            WallpaperOrientation.Portrait => "vertical",
            _ => "all",
        };

        var category = query.Filter(Id, "category", "all");
        var categoryLabel = category == "all" ? null : char.ToUpperInvariant(category[0]) + category[1..];

        var url = video
            ? HttpJson.WithQuery(
                VideoEndpoint,
                ("key", _apiKey),
                ("q", query.Text),
                ("page", page.ToString()),
                ("per_page", perPage.ToString()),
                ("video_type", "film"),
                ("category", category == "all" ? null : category),
                ("orientation", orientation),
                ("order", order),
                ("safesearch", query.SafeSearch ? "true" : "false"))
            : HttpJson.WithQuery(
                ImageEndpoint,
                ("key", _apiKey),
                ("q", query.Text),
                ("page", page.ToString()),
                ("per_page", perPage.ToString()),
                ("image_type", query.MediaType == WallpaperMediaType.Illustration ? "illustration" : "photo"),
                ("category", category == "all" ? null : category),
                ("orientation", orientation),
                ("order", order),
                ("safesearch", query.SafeSearch ? "true" : "false"));

        using var doc = await HttpJson.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var items = new List<WallpaperItem>();
        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in hits.EnumerateArray())
            {
                if (video) AddVideo(entry, categoryLabel, items);
                else AddImage(entry, query.MediaType, categoryLabel, items);
            }
        }

        var totalHits = HttpJson.IntOrDefault(root, "totalHits", 0);
        return new WallpaperPage
        {
            Items = items,
            Page = page,
            HasMore = items.Count > 0 && page * perPage < totalHits,
        };
    }

    private void AddImage(JsonElement entry, WallpaperMediaType? mediaType, string? category, List<WallpaperItem> items)
    {
        var id = HttpJson.String(entry, "id");
        var full = HttpJson.String(entry, "largeImageURL")
                   ?? HttpJson.String(entry, "webformatURL");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(full)) return;

        items.Add(new WallpaperItem
        {
            Id = id!,
            SourceId = Id,
            FullUrl = full!,
            ThumbnailUrl = HttpJson.String(entry, "webformatURL"),
            Title = CleanTags(HttpJson.String(entry, "tags")),
            Author = HttpJson.String(entry, "user"),
            PageUrl = HttpJson.String(entry, "pageURL"),
            Width = HttpJson.Int(entry, "imageWidth"),
            Height = HttpJson.Int(entry, "imageHeight"),
            FileSizeBytes = HttpJson.Long(entry, "imageSize"),
            MediaType = mediaType == WallpaperMediaType.Illustration
                ? WallpaperMediaType.Illustration
                : WallpaperMediaType.Photo,
            Category = category,
        });
    }

    private void AddVideo(JsonElement entry, string? category, List<WallpaperItem> items)
    {
        var id = HttpJson.String(entry, "id");
        var videos = HttpJson.Object(entry, "videos");
        if (string.IsNullOrWhiteSpace(id) || videos is not { } v) return;

        // "large" is 1920x1080 when Pixabay has it; medium is the honest fallback.
        var large = HttpJson.Object(v, "large");
        var medium = HttpJson.Object(v, "medium");
        var small = HttpJson.Object(v, "small");
        var tiny = HttpJson.Object(v, "tiny");

        var full = large is { } l ? HttpJson.String(l, "url") : null;
        full ??= medium is { } m ? HttpJson.String(m, "url") : null;
        full ??= small is { } s ? HttpJson.String(s, "url") : null;
        if (string.IsNullOrWhiteSpace(full)) return;

        // The tiny file is what plays under the mouse. It is the same quality as
        // a thumbnail, so hovering costs a few hundred kilobytes, not tens of megabytes.
        var preview = tiny is { } t ? HttpJson.String(t, "url") : null;
        preview ??= small is { } s2 ? HttpJson.String(s2, "url") : null;

        var thumbnail = tiny is { } t2 ? HttpJson.String(t2, "thumbnail") : null;
        thumbnail ??= small is { } s3 ? HttpJson.String(s3, "thumbnail") : null;
        thumbnail ??= preview;

        var width = large is { } lw ? HttpJson.Int(lw, "width") : null;
        var height = large is { } lh ? HttpJson.Int(lh, "height") : null;
        var size = large is { } ls ? HttpJson.Long(ls, "size") : null;

        items.Add(new WallpaperItem
        {
            Id = id!,
            SourceId = Id,
            FullUrl = full!,
            ThumbnailUrl = thumbnail,
            PreviewVideoUrl = preview,
            Title = CleanTags(HttpJson.String(entry, "tags")),
            Author = HttpJson.String(entry, "user"),
            PageUrl = HttpJson.String(entry, "pageURL"),
            Width = width,
            Height = height,
            FileSizeBytes = size,
            MediaType = WallpaperMediaType.Video,
            Category = category,
        });
    }

    /// <summary>Pixabay tags arrive comma-separated and lower-case; make them readable.</summary>
    private static string? CleanTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return null;

        var first = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3)
            .ToList();

        return first.Count == 0 ? null : string.Join(", ", first);
    }
}
