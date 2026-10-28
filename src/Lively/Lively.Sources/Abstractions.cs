namespace Lively.Sources;

/// <summary>
/// One wallpaper offered by a source. Every provider is normalised into this
/// shape so the UI never has to know which API it came from.
/// </summary>
public sealed record WallpaperItem
{
    /// <summary>Stable id, unique within the source. Used for favourites and de-duplication.</summary>
    public required string Id { get; init; }

    /// <summary>Which provider produced it.</summary>
    public required string SourceId { get; init; }

    /// <summary>The image to download and set as wallpaper.</summary>
    public required string FullUrl { get; init; }

    /// <summary>Smaller image for the grid. Falls back to <see cref="FullUrl"/>.</summary>
    public string? ThumbnailUrl { get; init; }

    public string? Title { get; init; }
    public string? Author { get; init; }

    /// <summary>The public web page for this wallpaper, opened from the preview.</summary>
    public string? PageUrl { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? FileSizeBytes { get; init; }

    /// <summary>What kind of file <see cref="FullUrl"/> points at.</summary>
    public WallpaperMediaType MediaType { get; init; } = WallpaperMediaType.Photo;

    /// <summary>The source's own category label, e.g. "Nature". Shown in the preview.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// A small, silent video used to preview a video wallpaper under the mouse.
    /// Only set when <see cref="MediaType"/> is video.
    /// </summary>
    public string? PreviewVideoUrl { get; init; }

    public string Thumb => string.IsNullOrWhiteSpace(ThumbnailUrl) ? FullUrl : ThumbnailUrl!;

    /// <summary>Globally unique key: source plus id. Safe as a dictionary key.</summary>
    public string Key => $"{SourceId}:{Id}";
}

/// <summary>
/// How the feed should be ordered. Every provider translates this into its own
/// idea of "top", "new" and "random"; none of them expose exactly the same
/// options, so the mapping lives inside each provider.
/// </summary>
public enum WallpaperSort
{
    Featured = 0,
    Top = 1,
    Newest = 2,
    Random = 3,
}

/// <summary>Which shape of picture to ask for. Desktop wallpapers default to landscape.</summary>
public enum WallpaperOrientation
{
    Landscape = 0,
    Portrait = 1,
    Any = 2,
}

/// <summary>The kinds of wallpaper a source can return.</summary>
public enum WallpaperMediaType
{
    Photo = 0,
    Illustration = 1,
    Video = 2,
    Gif = 3,
}

/// <summary>One choice inside a source's own filter, e.g. "Anime".</summary>
public sealed record SourceOption(string Id, string Label);

/// <summary>
/// A filter that belongs to one source and means nothing to the others.
/// The Filters dialog draws one block per source from these.
/// </summary>
public sealed record SourceFilterGroup(
    string Id,
    string Label,
    IReadOnlyList<SourceOption> Options,
    string DefaultId)
{
    public static SourceFilterGroup Of(string id, string label, string defaultId, params SourceOption[] options) =>
        new(id, label, options, defaultId);
}

/// <summary>What to ask a source for.</summary>
public sealed record WallpaperQuery
{
    public string? Text { get; init; }

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Requested page size. Providers clamp this to their own maximum.</summary>
    public int PerPage { get; init; } = 24;

    /// <summary>When false, sources that support it include adult content. Defaults to safe.</summary>
    public bool SafeSearch { get; init; } = true;

    /// <summary>
    /// Free-form hint from the rotation engine, e.g. "sunset" or "rain".
    /// Sources that support search terms use it; the rest ignore it.
    /// </summary>
    public string? Hint { get; init; }

    /// <summary>Ordering chosen on the Home page. Defaults to each source's featured list.</summary>
    public WallpaperSort Sort { get; init; } = WallpaperSort.Featured;

    /// <summary>Shape filter chosen on the Home page. Defaults to landscape for a desktop.</summary>
    public WallpaperOrientation Orientation { get; init; } = WallpaperOrientation.Landscape;

    /// <summary>
    /// Null asks the source for its default kind. Set when the person picks
    /// Photos, Illustrations or Videos in the Filters dialog.
    /// </summary>
    public WallpaperMediaType? MediaType { get; init; }

    /// <summary>
    /// Choices for each source's own filters, keyed by source id and then by
    /// <see cref="SourceFilterGroup.Id"/>. Missing means "use the default".
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SourceFilters { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads one of a source's filter choices, with a fallback.</summary>
    public string Filter(string sourceId, string filterId, string fallback) =>
        SourceFilters.TryGetValue(sourceId, out var filters)
        && filters.TryGetValue(filterId, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    public WallpaperQuery NextPage() => this with { Page = Page + 1 };
}

/// <summary>One page of results.</summary>
public sealed record WallpaperPage
{
    public required IReadOnlyList<WallpaperItem> Items { get; init; }
    public int Page { get; init; }
    public bool HasMore { get; init; }

    public static WallpaperPage Empty(int page) => new() { Items = Array.Empty<WallpaperItem>(), Page = page, HasMore = false };
}

public enum SourceErrorKind
{
    NotConfigured,
    Network,
    Http,
    RateLimited,
    Parse,
    Cancelled,
}

/// <summary>
/// A failure that is safe to show to a person. Never contains a URL with an API
/// key in it, because these strings end up in logs and on screen.
/// </summary>
public sealed class SourceException : Exception
{
    public SourceException(SourceErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public SourceErrorKind Kind { get; }

    public bool IsRetryable => Kind is SourceErrorKind.Network or SourceErrorKind.RateLimited or SourceErrorKind.Http;
}

/// <summary>A wallpaper provider.</summary>
public interface IWallpaperSource
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>True when the source cannot work without an API key.</summary>
    bool RequiresApiKey { get; }

    /// <summary>False when the key is missing or still a placeholder.</summary>
    bool IsConfigured { get; }

    /// <summary>The kinds of wallpaper this source can return, in display order.</summary>
    IReadOnlyList<WallpaperMediaType> MediaTypes { get; }

    /// <summary>This source's own filters, drawn under its name in the Filters dialog.</summary>
    IReadOnlyList<SourceFilterGroup> Filters { get; }

    Task<WallpaperPage> SearchAsync(WallpaperQuery query, CancellationToken cancellationToken = default);
}
