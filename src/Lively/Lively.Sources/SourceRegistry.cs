namespace Lively.Sources;

/// <summary>
/// Keys for the providers that need one.
///
/// The values are not written here. They are injected at build time from
/// <c>secrets/ApiKeys.local.props</c>, which is git-ignored, and end up in the
/// generated <see cref="GeneratedApiKeys"/> class. A clean checkout builds with
/// empty keys, and the two keyless sources still work.
/// </summary>
public sealed record SourceKeys
{
    public string? Wallhaven { get; init; }
    public string? Unsplash { get; init; }
    public string? Pixabay { get; init; }

    public static SourceKeys Defaults { get; } = new()
    {
        Wallhaven = GeneratedApiKeys.Wallhaven,
        Unsplash = GeneratedApiKeys.Unsplash,
        Pixabay = GeneratedApiKeys.Pixabay,
    };
}

/// <summary>
/// The set of sources the app knows about, and which of them the person turned
/// on. Enabled sources are resolved in a stable order so the Home feed does not
/// reshuffle itself between launches.
/// </summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, IWallpaperSource> _all;
    private readonly List<string> _enabled;

    public SourceRegistry(HttpClient httpClient, SourceKeys? keys = null, IEnumerable<string>? enabledIds = null)
        : this(BuildDefaultSources(httpClient, keys), enabledIds)
    {
    }

    /// <summary>For tests and for callers that bring their own provider set.</summary>
    public SourceRegistry(IEnumerable<IWallpaperSource> sources, IEnumerable<string>? enabledIds = null)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var all = sources.ToList();
        _all = all.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        // Default: everything that works without a signup, plus anything whose
        // key is already present. A missing key must never look like an empty feed.
        var defaults = all
            .Where(s => !s.RequiresApiKey || s.IsConfigured)
            .Select(s => s.Id)
            .ToList();

        _enabled = (enabledIds ?? defaults)
            .Where(id => _all.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_enabled.Count == 0) _enabled = defaults;
    }

    private static IWallpaperSource[] BuildDefaultSources(HttpClient httpClient, SourceKeys? keys)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var k = keys ?? SourceKeys.Defaults;

        return
        [
            new Providers.WallhavenSource(httpClient, k.Wallhaven),
            new Providers.UnsplashSource(httpClient, k.Unsplash),
            new Providers.PixabaySource(httpClient, k.Pixabay),
            new Providers.BingSource(httpClient),
            new Providers.SpotlightSource(httpClient),
        ];
    }

    public IReadOnlyList<IWallpaperSource> All => _all.Values.OrderBy(s => s.DisplayName, StringComparer.Ordinal).ToList();

    public IReadOnlyList<IWallpaperSource> Enabled =>
        _enabled.Select(id => _all[id]).ToList();

    public IWallpaperSource? Get(string id) =>
        _all.TryGetValue(id, out var source) ? source : null;
}
