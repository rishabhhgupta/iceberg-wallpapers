using Lively.Discovery.Favourites;
using Lively.Discovery.Rotation;
using Xunit;

namespace Lively.Discovery.Tests;

public class RotationClockTests
{
    private static readonly DateTimeOffset Last = new(2026, 6, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Not_due_before_the_interval_has_passed()
    {
        Assert.False(RotationClock.IsDue(Last, Last.AddMinutes(59), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Due_once_the_interval_has_passed()
    {
        Assert.True(RotationClock.IsDue(Last, Last.AddMinutes(60), TimeSpan.FromHours(1)));
        Assert.True(RotationClock.IsDue(Last, Last.AddHours(9), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Ten_hours_asleep_means_one_change_not_ten()
    {
        var woke = Last.AddHours(10);

        Assert.True(RotationClock.IsDue(Last, woke, TimeSpan.FromHours(1)));
        Assert.Equal(10, RotationClock.MissedChanges(Last, woke, TimeSpan.FromHours(1)));

        // Next change is one interval after waking, never in the past.
        var next = RotationClock.NextChangeAfter(Last, woke, TimeSpan.FromHours(1));
        Assert.Equal(Last.AddHours(11), next);
        Assert.True(next > woke);
    }

    [Fact]
    public void Clock_moved_backwards_does_not_produce_a_past_change()
    {
        var now = Last.AddHours(-5);
        var next = RotationClock.NextChangeAfter(Last, now, TimeSpan.FromHours(1));
        Assert.Equal(Last.AddHours(1), next);
        Assert.False(RotationClock.IsDue(Last, now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Zero_interval_is_never_due_and_throws_when_scheduled()
    {
        Assert.False(RotationClock.IsDue(Last, Last.AddDays(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RotationClock.NextChangeAfter(Last, Last, TimeSpan.Zero));
    }
}

public class RotationPlannerTests
{
    private static List<WallpaperCandidate> Pool(int count) =>
        Enumerable.Range(1, count).Select(i => new WallpaperCandidate($"w{i}", "test")).ToList();

    [Fact]
    public void Empty_pool_returns_null_rather_than_throwing()
    {
        var planner = new RotationPlanner(seed: 1);
        Assert.Null(planner.Pick(Array.Empty<WallpaperCandidate>()));
    }

    [Fact]
    public void Never_repeats_until_the_pool_is_exhausted()
    {
        var planner = new RotationPlanner(recentWindow: 5, seed: 7);
        var pool = Pool(5);

        var picked = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            picked.Add(planner.Pick(pool)!.Id);
        }

        Assert.Equal(5, picked.Distinct().Count());
    }

    [Fact]
    public void When_everything_is_recent_it_still_changes()
    {
        var planner = new RotationPlanner(recentWindow: 3, seed: 3);
        var pool = Pool(2);

        // Two items, window of two: the third pick must reuse the oldest.
        var a = planner.Pick(pool)!.Id;
        var b = planner.Pick(pool)!.Id;
        var c = planner.Pick(pool)!.Id;

        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Seeded_planners_replay_the_same_sequence()
    {
        var first = new RotationPlanner(recentWindow: 50, seed: 42);
        var second = new RotationPlanner(recentWindow: 50, seed: 42);
        var pool = Pool(20);

        var a = Enumerable.Range(0, 15).Select(_ => first.Pick(pool)!.Id).ToList();
        var b = Enumerable.Range(0, 15).Select(_ => second.Pick(pool)!.Id).ToList();

        Assert.Equal(a, b);
    }

    [Fact]
    public void Restore_keeps_the_newest_and_ignores_duplicates()
    {
        var planner = new RotationPlanner(recentWindow: 3, seed: 1);
        planner.Restore(new[] { "w1", "w2", "w1", "w3", "w4" });

        Assert.Equal(3, planner.Recent.Count);
        Assert.Equal("w1", planner.Recent[0]);
        Assert.Equal("w2", planner.Recent[1]);
        Assert.Equal("w3", planner.Recent[2]);
    }
}

public class FavouriteStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "lively-tests-" + Guid.NewGuid().ToString("N"), "favourites.json");

    private static FavouriteItem Item(string id) => new()
    {
        Id = id,
        SourceId = "wallhaven",
        Title = "Test " + id,
        ThumbnailUrl = "https://example.invalid/" + id + ".jpg",
    };

    [Fact]
    public async Task Adding_then_reloading_keeps_the_item()
    {
        var path = TempFile();
        try
        {
            var store = new FavouriteStore(path);
            await store.LoadAsync();
            Assert.True(await store.AddAsync(Item("a")));

            var reopened = new FavouriteStore(path);
            await reopened.LoadAsync();

            Assert.Single(reopened.Items);
            Assert.Equal("a", reopened.Items[0].Id);
            Assert.True(reopened.Contains("a"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Adding_the_same_id_twice_does_not_duplicate()
    {
        var path = TempFile();
        try
        {
            var store = new FavouriteStore(path);
            await store.LoadAsync();

            Assert.True(await store.AddAsync(Item("a")));
            Assert.False(await store.AddAsync(Item("a")));
            Assert.Single(store.Items);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Removing_an_item_persists()
    {
        var path = TempFile();
        try
        {
            var store = new FavouriteStore(path);
            await store.LoadAsync();
            await store.AddAsync(Item("a"));
            await store.AddAsync(Item("b"));

            Assert.True(await store.RemoveAsync("a"));
            Assert.False(await store.RemoveAsync("a"));

            var reopened = new FavouriteStore(path);
            await reopened.LoadAsync();
            Assert.Single(reopened.Items);
            Assert.Equal("b", reopened.Items[0].Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Newest_favourite_comes_first()
    {
        var path = TempFile();
        try
        {
            var store = new FavouriteStore(path);
            await store.LoadAsync();
            await store.AddAsync(Item("a"));
            await store.AddAsync(Item("b"));

            Assert.Equal("b", store.Items[0].Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task A_corrupt_file_is_quarantined_and_the_app_still_starts()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{ this is not json");

            var store = new FavouriteStore(path);
            await store.LoadAsync();

            Assert.Empty(store.Items);
            Assert.True(await store.AddAsync(Item("a")));

            var quarantined = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*");
            Assert.Single(quarantined);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task A_missing_file_is_an_empty_list_not_an_error()
    {
        var path = TempFile();
        var store = new FavouriteStore(path);
        await store.LoadAsync();
        Assert.Empty(store.Items);
    }

    private static void Cleanup(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
