using System.Collections.Concurrent;
using Lively.Sources;

namespace Lively.Sources;

/// <summary>One provider that could not answer, and why.</summary>
public sealed record SourceFailure(string SourceId, string DisplayName, SourceErrorKind Kind, string Message)
{
    public bool IsRetryable => Kind is SourceErrorKind.Network or SourceErrorKind.RateLimited or SourceErrorKind.Http;
}

/// <summary>One screenful of the feed.</summary>
public sealed record FeedPage
{
    public required IReadOnlyList<WallpaperItem> Items { get; init; }
    public required int Page { get; init; }
    public required bool HasMore { get; init; }
    public required IReadOnlyList<SourceFailure> Failures { get; init; }

    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// Merges several providers into one endless feed.
///
/// The rules that matter, all learned from what goes wrong in the real world:
///   * One provider being down must not empty the feed. Its failure is reported
///     separately and the others still contribute.
///   * Duplicates across providers are removed by their global key.
///   * Items are interleaved rather than concatenated, so the first screen is not
///     twelve photos from the same site.
///   * When every provider fails the page is empty but does not throw, so the UI
///     can show "no connection, retry" instead of a spinner that never stops.
/// </summary>
public sealed class FeedService
{
    private readonly SourceRegistry _registry;
    private readonly int _perSourcePageSize;
    private readonly TimeSpan _sourceTimeout;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);

    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(3);

    public FeedService(SourceRegistry registry, int perSourcePageSize = 12, TimeSpan? sourceTimeout = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _perSourcePageSize = Math.Clamp(perSourcePageSize, 1, 50);

        // One slow site must not hold the whole first screen hostage. With this
        // cap the feed appears in a couple of seconds even if a provider is
        // having a bad day, and the slow one is simply reported as unavailable.
        _sourceTimeout = sourceTimeout ?? TimeSpan.FromSeconds(12);
    }

    /// <summary>
    /// Forgets every "this source is having a bad day" marker. Called when the
    /// network comes back, so a provider is tried immediately instead of waiting
    /// out the three-minute cooldown it earned while the cable was unplugged.
    /// </summary>
    public void ClearCooldowns() => _cooldown.Clear();

    public async Task<FeedPage> GetPageAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sources = _registry.Enabled;
        if (sources.Count == 0)
        {
            return new FeedPage
            {
                Items = Array.Empty<WallpaperItem>(),
                Page = query.Page,
                HasMore = false,
                Failures = Array.Empty<SourceFailure>(),
            };
        }

        var perSource = query with { PerPage = _perSourcePageSize };
        var results = await Task.WhenAll(sources.Select(source => FetchGuardedAsync(source, perSource, cancellationToken)))
            .ConfigureAwait(false);

        var failures = results.Where(r => r.Failure is not null).Select(r => r.Failure!).ToList();

        // A site that just failed is left alone for a few minutes. Otherwise every
        // scroll would wait the full timeout again for the same broken provider.
        var until = DateTimeOffset.UtcNow + FailureCooldown;
        foreach (var failure in failures)
        {
            _cooldown[failure.SourceId] = until;
        }

        // Interleave: take the first item from every source, then the second, and
        // so on. Preserves each source's own ordering while mixing the sites.
        var merged = new List<WallpaperItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var longest = results.Max(r => r.Items.Count);

        for (var index = 0; index < longest; index++)
        {
            foreach (var result in results)
            {
                if (index >= result.Items.Count) continue;

                var item = result.Items[index];
                if (seen.Add(item.Key)) merged.Add(item);
            }
        }

        return new FeedPage
        {
            Items = merged,
            Page = query.Page,
            // "More" is true if any provider still has pages. A single exhausted
            // provider must not stop the feed.
            HasMore = results.Any(r => r.HasMore),
            Failures = failures,
        };
    }

    private Task<SourceResult> FetchGuardedAsync(
        IWallpaperSource source,
        WallpaperQuery query,
        CancellationToken cancellationToken)
    {
        if (_cooldown.TryGetValue(source.Id, out var until) && until > DateTimeOffset.UtcNow)
        {
            return Task.FromResult(new SourceResult(
                source.Id,
                Array.Empty<WallpaperItem>(),
                false,
                new SourceFailure(
                    source.Id,
                    source.DisplayName,
                    SourceErrorKind.Network,
                    "Unavailable right now. Trying again shortly.")));
        }

        return FetchAsync(source, query, cancellationToken);
    }

    private async Task<SourceResult> FetchAsync(
        IWallpaperSource source,
        WallpaperQuery query,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_sourceTimeout);

        try
        {
            var page = await source.SearchAsync(query, timeout.Token).ConfigureAwait(false);

            return new SourceResult(source.Id, page.Items, page.HasMore, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SourceException ex)
        {
            return new SourceResult(source.Id, Array.Empty<WallpaperItem>(), false,
                new SourceFailure(source.Id, source.DisplayName, ex.Kind, ex.Message));
        }
        catch (OperationCanceledException)
        {
            // The per-source deadline, not the person scrolling away.
            return new SourceResult(source.Id, Array.Empty<WallpaperItem>(), false,
                new SourceFailure(source.Id, source.DisplayName, SourceErrorKind.Network, "The source took too long."));
        }
        catch (Exception ex)
        {
            return new SourceResult(source.Id, Array.Empty<WallpaperItem>(), false,
                new SourceFailure(source.Id, source.DisplayName, SourceErrorKind.Http, ex.Message));
        }
    }

    private sealed record SourceResult(
        string SourceId,
        IReadOnlyList<WallpaperItem> Items,
        bool HasMore,
        SourceFailure? Failure);
}
