using System.Text.Json;
using Lively.Sources.Http;

namespace Lively.Sources.Providers;

/// <summary>
/// Bing's daily picture. No key, no signup: the same public feed the Bing home
/// page uses. Eight days of history, each with the photographer's credit link.
/// </summary>
public sealed class BingSource : IWallpaperSource
{
    private const string Endpoint = "https://www.bing.com/HPImageArchive.aspx";
    private const string ImageBase = "https://www.bing.com";

    private readonly HttpClient _http;

    public BingSource(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string Id => "bing";
    public string DisplayName => "Bing";
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;

    public IReadOnlyList<WallpaperMediaType> MediaTypes => [WallpaperMediaType.Photo];

    public IReadOnlyList<SourceFilterGroup> Filters => [];

    public async Task<WallpaperPage> SearchAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // There are only eight days of pictures, so a second page would be empty.
        if (query.Page > 1)
        {
            return WallpaperPage.Empty(query.Page);
        }

        var url = HttpJson.WithQuery(
            Endpoint,
            ("format", "js"),
            ("n", "8"),
            ("mkt", "en-US"));

        using var doc = await HttpJson.GetAsync(_http, url, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var items = new List<WallpaperItem>();
        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in images.EnumerateArray())
            {
                var basePath = HttpJson.String(entry, "urlbase");
                if (string.IsNullOrWhiteSpace(basePath)) continue;

                var id = HttpJson.String(entry, "hsh")
                         ?? HttpJson.String(entry, "startdate")
                         ?? basePath!;

                items.Add(new WallpaperItem
                {
                    Id = id!,
                    SourceId = Id,
                    FullUrl = $"{ImageBase}{basePath}_1920x1080.jpg",
                    ThumbnailUrl = $"{ImageBase}{basePath}_400x240.jpg",
                    Title = HttpJson.String(entry, "title"),
                    Author = CleanCopyright(HttpJson.String(entry, "copyright")),
                    PageUrl = HttpJson.String(entry, "copyrightlink"),
                    Width = 1920,
                    Height = 1080,
                    MediaType = WallpaperMediaType.Photo,
                });
            }
        }

        return new WallpaperPage
        {
            Items = items,
            Page = 1,
            HasMore = false,
        };
    }

    /// <summary>Bing sends "(© Name/Alamy)" glued to the description; keep just the credit.</summary>
    private static string? CleanCopyright(string? copyright)
    {
        if (string.IsNullOrWhiteSpace(copyright)) return null;

        var open = copyright.LastIndexOf('(');
        if (open <= 0) return null;

        var credit = copyright[open..].Trim('(', ')', ' ');
        return string.IsNullOrWhiteSpace(credit) ? null : credit;
    }
}
