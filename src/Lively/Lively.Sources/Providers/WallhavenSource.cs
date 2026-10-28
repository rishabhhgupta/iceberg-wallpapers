using System.Text.Json;
using Lively.Sources.Http;

namespace Lively.Sources.Providers;

/// <summary>
/// Wallhaven. Works without a key for safe content; a key raises the rate limit
/// but is never required. Explicit results are never requested, because this app
/// ships through the Microsoft Store.
/// </summary>
public sealed class WallhavenSource : IWallpaperSource
{
    private const string Endpoint = "https://wallhaven.cc/api/v1/search";
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public WallhavenSource(HttpClient http, string? apiKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    public string Id => "wallhaven";
    public string DisplayName => "Wallhaven";
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;

    public IReadOnlyList<WallpaperMediaType> MediaTypes => [WallpaperMediaType.Photo];

    public IReadOnlyList<SourceFilterGroup> Filters { get; } =
    [
        SourceFilterGroup.Of("purity", "Content",
            "safe",
            new SourceOption("safe", "Safe"),
            new SourceOption("sketchy", "Safe + Sketchy")),
        SourceFilterGroup.Of("category", "Category",
            "all",
            new SourceOption("all", "All"),
            new SourceOption("general", "General"),
            new SourceOption("anime", "Anime"),
            new SourceOption("people", "People")),
    ];

    public async Task<WallpaperPage> SearchAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var perPage = Math.Clamp(query.PerPage, 1, 24);

        var sorting = query.Sort switch
        {
            WallpaperSort.Newest => "date_added",
            WallpaperSort.Random => "random",
            // Wallhaven ignores the search term when sorting by toplist, so a
            // search must stay on relevance for Top to mean anything.
            WallpaperSort.Top => string.IsNullOrWhiteSpace(query.Text) ? "toplist" : "relevance",
            _ => string.IsNullOrWhiteSpace(query.Text) ? "toplist" : "relevance",
        };

        var ratios = query.Orientation switch
        {
            WallpaperOrientation.Landscape => "landscape",
            WallpaperOrientation.Portrait => "portrait",
            _ => null,
        };

        // topRange is only valid together with sorting=toplist. A short window
        // for "featured" keeps that list fresh; "top" asks for a whole year.
        var topRange = sorting == "toplist"
            ? (query.Sort == WallpaperSort.Top ? "1y" : "1M")
            : null;

        var purity = query.Filter(Id, "purity", "safe") == "sketchy" ? "110" : "100";
        var category = query.Filter(Id, "category", "all") switch
        {
            "general" => "100",
            "anime" => "010",
            "people" => "001",
            _ => "111",
        };
        var categoryLabel = query.Filter(Id, "category", "all") switch
        {
            "general" => "General",
            "anime" => "Anime",
            "people" => "People",
            _ => null,
        };

        var url = HttpJson.WithQuery(
            Endpoint,
            ("q", query.Text),
            ("page", page.ToString()),
            ("categories", category),
            ("purity", purity),
            ("sorting", sorting),
            ("topRange", topRange),
            ("ratios", ratios),
            ("apikey", _apiKey));

        using var doc = await HttpJson.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var items = new List<WallpaperItem>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                var id = HttpJson.String(entry, "id");
                var full = HttpJson.String(entry, "path");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(full)) continue;

                var thumbs = HttpJson.Object(entry, "thumbs");
                items.Add(new WallpaperItem
                {
                    Id = id!,
                    SourceId = Id,
                    FullUrl = full!,
                    ThumbnailUrl = thumbs is { } t ? HttpJson.String(t, "small") ?? HttpJson.String(t, "large") : null,
                    // Wallhaven has no titles, only ids. Showing "Wallhaven
                    // 6lw16w" on a card looks like a bug, so the title stays empty
                    // and the preview shows the source line instead.
                    PageUrl = HttpJson.String(entry, "url"),
                    Width = HttpJson.Int(entry, "dimension_x"),
                    Height = HttpJson.Int(entry, "dimension_y"),
                    MediaType = WallpaperMediaType.Photo,
                    Category = categoryLabel,
                });
            }
        }

        var currentPage = page;
        var lastPage = page;
        if (root.TryGetProperty("meta", out var meta))
        {
            currentPage = HttpJson.IntOrDefault(meta, "current_page", page);
            lastPage = HttpJson.IntOrDefault(meta, "last_page", page);
        }

        return new WallpaperPage
        {
            Items = items,
            Page = currentPage,
            HasMore = items.Count > 0 && currentPage < lastPage,
        };
    }
}
