using System.Text.Json;
using Lively.Sources.Http;

namespace Lively.Sources.Providers;

/// <summary>
/// Unsplash. Needs an access key. Uses the search endpoint when there is a
/// search term, a topic's feed when a topic is chosen, and the editorial feed
/// otherwise.
/// </summary>
public sealed class UnsplashSource : IWallpaperSource
{
    private const string FeedEndpoint = "https://api.unsplash.com/photos";
    private const string SearchEndpoint = "https://api.unsplash.com/search/photos";
    private const string TopicEndpoint = "https://api.unsplash.com/topics";

    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public UnsplashSource(HttpClient http, string? apiKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    public string Id => "unsplash";
    public string DisplayName => "Unsplash";
    public bool RequiresApiKey => true;
    public bool IsConfigured => _apiKey is not null;

    public IReadOnlyList<WallpaperMediaType> MediaTypes => [WallpaperMediaType.Photo];

    public IReadOnlyList<SourceFilterGroup> Filters { get; } =
    [
        SourceFilterGroup.Of("topic", "Topic",
            "featured",
            new SourceOption("featured", "Featured"),
            new SourceOption("wallpapers", "Wallpapers"),
            new SourceOption("nature", "Nature"),
            new SourceOption("animals", "Animals"),
            new SourceOption("architecture", "Architecture"),
            new SourceOption("travel", "Travel"),
            new SourceOption("food", "Food"),
            new SourceOption("fashion", "Fashion"),
            new SourceOption("people", "People"),
            new SourceOption("technology", "Technology"),
            new SourceOption("interiors", "Interiors"),
            new SourceOption("street", "Street")),
    ];

    public async Task<WallpaperPage> SearchAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_apiKey is null)
        {
            throw new SourceException(SourceErrorKind.NotConfigured, "Unsplash needs an access key.");
        }

        var page = Math.Max(1, query.Page);
        var perPage = Math.Clamp(query.PerPage, 1, 30);
        var searching = !string.IsNullOrWhiteSpace(query.Text);
        var topic = query.Filter(Id, "topic", "featured");
        var topicLabel = topic == "featured"
            ? null
            : char.ToUpperInvariant(topic[0]) + topic[1..];

        // The live feed has no random switch. Starting on a random page gives a
        // fresh mix each time the person taps Random; later pages continue from
        // there so scrolling still works normally.
        if (query.Sort == WallpaperSort.Random && !searching && query.Page == 1)
        {
            page = Random.Shared.Next(1, 20);
        }

        var orderBy = searching
            ? (query.Sort == WallpaperSort.Newest ? "latest" : "relevant")
            : (query.Sort == WallpaperSort.Newest ? "latest" : "popular");

        var orientation = query.Orientation switch
        {
            WallpaperOrientation.Landscape => "landscape",
            WallpaperOrientation.Portrait => "portrait",
            _ => null,
        };

        var endpoint = searching
            ? SearchEndpoint
            : topic == "featured" ? FeedEndpoint : $"{TopicEndpoint}/{Uri.EscapeDataString(topic)}/photos";

        var url = HttpJson.WithQuery(
            endpoint,
            ("query", searching ? query.Text : null),
            ("page", page.ToString()),
            ("per_page", perPage.ToString()),
            ("order_by", orderBy),
            ("orientation", orientation),
            ("client_id", _apiKey));

        using var doc = await HttpJson.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        // Search nests its results; feeds are a bare array.
        var results = searching && root.TryGetProperty("results", out var nested) ? nested : root;
        var totalPages = searching ? HttpJson.IntOrDefault(root, "total_pages", page) : page + 1;

        var items = new List<WallpaperItem>();
        if (results.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in results.EnumerateArray())
            {
                var id = HttpJson.String(entry, "id");
                var urls = HttpJson.Object(entry, "urls");
                if (urls is not { } u) continue;

                var full = HttpJson.String(u, "full") ?? HttpJson.String(u, "raw");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(full)) continue;

                var links = HttpJson.Object(entry, "links");
                var user = HttpJson.Object(entry, "user");

                items.Add(new WallpaperItem
                {
                    Id = id!,
                    SourceId = Id,
                    FullUrl = full!,
                    ThumbnailUrl = HttpJson.String(u, "small") ?? HttpJson.String(u, "thumb"),
                    // Unsplash often has no description; an empty title is
                    // honest, a made-up "Unsplash photo" is not.
                    Title = HttpJson.String(entry, "alt_description")
                            ?? HttpJson.String(entry, "description"),
                    Author = user is { } usr ? HttpJson.String(usr, "name") : null,
                    PageUrl = links is { } l ? HttpJson.String(l, "html") : null,
                    Width = HttpJson.Int(entry, "width"),
                    Height = HttpJson.Int(entry, "height"),
                    MediaType = WallpaperMediaType.Photo,
                    Category = topicLabel,
                });
            }
        }

        return new WallpaperPage
        {
            Items = items,
            Page = page,
            HasMore = items.Count > 0 && (searching ? page < totalPages : true),
        };
    }
}
