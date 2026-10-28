using Lively.Sources;
using Xunit;

namespace Lively.Sources.Tests;

internal sealed class FakeSource : IWallpaperSource
{
    private readonly Func<WallpaperQuery, CancellationToken, Task<WallpaperPage>> _handler;

    public FakeSource(
        string id,
        Func<WallpaperQuery, CancellationToken, Task<WallpaperPage>> handler,
        bool configured = true)
    {
        Id = id;
        _handler = handler;
        IsConfigured = configured;
    }

    public string Id { get; }
    public string DisplayName => Id;
    public bool RequiresApiKey => !IsConfigured;
    public bool IsConfigured { get; }
    public IReadOnlyList<WallpaperMediaType> MediaTypes => [WallpaperMediaType.Photo];
    public IReadOnlyList<SourceFilterGroup> Filters => [];
    public int Calls { get; private set; }

    public Task<WallpaperPage> SearchAsync(WallpaperQuery query, CancellationToken cancellationToken = default)
    {
        Calls++;
        return _handler(query, cancellationToken);
    }

    public static WallpaperItem Item(string id) => new()
    {
        Id = id,
        SourceId = "fake",
        FullUrl = "https://example.invalid/" + id + ".jpg",
    };
}

public class FeedServiceTests
{
    private static FakeSource Pages(string id, int perPage, string prefix, bool hasMore = true) =>
        new(id, (query, _) => Task.FromResult(new WallpaperPage
        {
            Items = Enumerable.Range(1, perPage)
                .Select(i => new WallpaperItem
                {
                    Id = $"{prefix}{i}",
                    SourceId = id,
                    FullUrl = $"https://example.invalid/{prefix}{i}.jpg",
                })
                .ToList(),
            Page = query.Page,
            HasMore = hasMore,
        }));

    [Fact]
    public async Task Items_are_interleaved_between_providers()
    {
        var registry = new SourceRegistry(new IWallpaperSource[]
        {
            Pages("a", 3, "a"),
            Pages("b", 3, "b"),
        });

        var feed = await new FeedService(registry, perSourcePageSize: 3).GetPageAsync(new WallpaperQuery());

        Assert.Equal(6, feed.Items.Count);
        Assert.Equal(new[] { "a1", "b1", "a2", "b2", "a3", "b3" }, feed.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task One_broken_provider_does_not_empty_the_feed()
    {
        var broken = new FakeSource("broken", (_, _) =>
            throw new SourceException(SourceErrorKind.Network, "no connection"));

        var registry = new SourceRegistry(new IWallpaperSource[] { Pages("good", 2, "g"), broken });

        var feed = await new FeedService(registry).GetPageAsync(new WallpaperQuery());

        Assert.Equal(2, feed.Items.Count);
        var failure = Assert.Single(feed.Failures);
        Assert.Equal("broken", failure.SourceId);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public async Task Every_provider_failing_returns_an_empty_page_without_throwing()
    {
        var broken = new FakeSource("broken", (_, _) =>
            throw new SourceException(SourceErrorKind.Http, "server error"));
        var registry = new SourceRegistry(new IWallpaperSource[] { broken });

        var feed = await new FeedService(registry).GetPageAsync(new WallpaperQuery());

        Assert.True(feed.IsEmpty);
        Assert.False(feed.HasMore);
        Assert.Single(feed.Failures);
    }

    [Fact]
    public async Task The_feed_continues_while_any_provider_has_more_pages()
    {
        var exhausted = Pages("done", 2, "d", hasMore: false);
        var more = Pages("more", 2, "m", hasMore: true);
        var registry = new SourceRegistry(new IWallpaperSource[] { exhausted, more });

        var feed = await new FeedService(registry).GetPageAsync(new WallpaperQuery());

        Assert.True(feed.HasMore);
    }

    [Fact]
    public async Task An_exhausted_feed_reports_no_more()
    {
        var registry = new SourceRegistry(new IWallpaperSource[] { Pages("only", 2, "o", hasMore: false) });

        var feed = await new FeedService(registry).GetPageAsync(new WallpaperQuery());

        Assert.False(feed.HasMore);
    }

    [Fact]
    public async Task Duplicates_within_a_page_are_removed()
    {
        var duplicate = new FakeSource("dup", (query, _) => Task.FromResult(new WallpaperPage
        {
            Items = new[] { FakeSource.Item("same"), FakeSource.Item("same"), FakeSource.Item("other") },
            Page = query.Page,
            HasMore = false,
        }));
        var registry = new SourceRegistry(new IWallpaperSource[] { duplicate });

        var feed = await new FeedService(registry).GetPageAsync(new WallpaperQuery());

        Assert.Equal(2, feed.Items.Count);
    }

    [Fact]
    public async Task The_page_number_is_passed_through()
    {
        WallpaperQuery? seen = null;
        var source = new FakeSource("s", (query, _) =>
        {
            seen = query;
            return Task.FromResult(WallpaperPage.Empty(query.Page));
        });
        var registry = new SourceRegistry(new IWallpaperSource[] { source });

        var feed = await new FeedService(registry).GetPageAsync(new WallpaperQuery { Page = 7 });

        Assert.Equal(7, seen!.Page);
        Assert.Equal(7, feed.Page);
    }

    [Fact]
    public async Task Cancelling_the_scroll_stops_the_work()
    {
        var slow = new FakeSource("slow", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return WallpaperPage.Empty(1);
        });
        var registry = new SourceRegistry(new IWallpaperSource[] { slow });

        using var cts = new CancellationTokenSource();
        var task = new FeedService(registry).GetPageAsync(new WallpaperQuery(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task A_provider_with_a_missing_key_is_not_called_at_all()
    {
        var unconfigured = new FakeSource("nokey", (_, _) =>
            throw new InvalidOperationException("should not be called"), configured: false);
        var good = Pages("good", 1, "g");

        var registry = new SourceRegistry(new IWallpaperSource[] { unconfigured, good });
        var feed = await new FeedService(registry).GetPageAsync(new WallpaperQuery());

        Assert.Single(feed.Items);
        Assert.Equal(0, unconfigured.Calls);
    }

    [Fact]
    public async Task One_slow_provider_does_not_hold_up_the_rest()
    {
        var slow = new FakeSource("slow", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return WallpaperPage.Empty(1);
        });
        var fast = Pages("fast", 2, "f");

        var registry = new SourceRegistry(new IWallpaperSource[] { slow, fast });
        var feed = new FeedService(registry, sourceTimeout: TimeSpan.FromMilliseconds(200));

        var started = DateTimeOffset.UtcNow;
        var page = await feed.GetPageAsync(new WallpaperQuery());
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Failures, f => f.SourceId == "slow");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), "the first page took " + elapsed);
    }

    [Fact]
    public async Task A_broken_provider_is_not_asked_again_straight_away()
    {
        var broken = new FakeSource("broken", (_, _) =>
            throw new SourceException(SourceErrorKind.Http, "server error"));
        var good = Pages("good", 1, "g");

        var registry = new SourceRegistry(new IWallpaperSource[] { broken, good });
        var feed = new FeedService(registry);

        await feed.GetPageAsync(new WallpaperQuery { Page = 1 });
        Assert.Equal(1, broken.Calls);

        var second = await feed.GetPageAsync(new WallpaperQuery { Page = 2 });

        Assert.Equal(1, broken.Calls);
        Assert.Single(second.Items);
    }
}
