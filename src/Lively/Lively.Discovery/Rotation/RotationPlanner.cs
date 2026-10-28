namespace Lively.Discovery.Rotation;

/// <summary>One wallpaper the rotation may choose from.</summary>
public sealed record WallpaperCandidate(string Id, string SourceId);

/// <summary>
/// Picks the next wallpaper while avoiding repeats.
///
/// Rules, in order:
///   1. Never pick anything already used inside the recent window.
///   2. If every candidate is inside the window, pick the one used longest ago.
///      (Refusing to change at all would leave the same wallpaper up forever.)
///   3. An empty pool returns null. The caller decides whether to keep the
///      current wallpaper or surface an error.
///
/// Random selection is seeded, so tests are deterministic and a "surprise me"
/// sequence can be replayed exactly.
/// </summary>
public sealed class RotationPlanner
{
    private readonly int _recentWindow;
    private readonly Random _random;
    private readonly List<string> _recent = new();

    public RotationPlanner(int recentWindow = 20, int? seed = null)
    {
        if (recentWindow < 0) throw new ArgumentOutOfRangeException(nameof(recentWindow));
        _recentWindow = recentWindow;
        _random = seed is { } s ? new Random(s) : new Random();
    }

    /// <summary>Ids used most recently first.</summary>
    public IReadOnlyList<string> Recent => _recent;

    public WallpaperCandidate? Pick(IReadOnlyList<WallpaperCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return null;

        if (_recentWindow == 0)
        {
            var any = candidates[_random.Next(candidates.Count)];
            MarkUsed(any.Id);
            return any;
        }

        var recentSet = new HashSet<string>(_recent, StringComparer.Ordinal);
        var fresh = candidates.Where(c => !recentSet.Contains(c.Id)).ToList();

        if (fresh.Count > 0)
        {
            var chosen = fresh[_random.Next(fresh.Count)];
            MarkUsed(chosen.Id);
            return chosen;
        }

        // Everything is recent. Take the candidate whose last use is oldest.
        // Candidates not in the recent list at all sort first, which cannot
        // happen here, but keeps the comparison total.
        var candidateIds = new HashSet<string>(candidates.Select(c => c.Id), StringComparer.Ordinal);
        var oldestId = _recent
            .Where(candidateIds.Contains)
            .LastOrDefault();

        var fallback = candidates.FirstOrDefault(c => c.Id == oldestId) ?? candidates[0];
        MarkUsed(fallback.Id);
        return fallback;
    }

    public void MarkUsed(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        _recent.Remove(id);
        _recent.Insert(0, id);

        while (_recent.Count > _recentWindow)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }
    }

    /// <summary>Restores history after a restart, newest first.</summary>
    public void Restore(IEnumerable<string> recentIds)
    {
        ArgumentNullException.ThrowIfNull(recentIds);
        _recent.Clear();
        foreach (var id in recentIds)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (_recent.Contains(id)) continue;
            _recent.Add(id);
            if (_recent.Count >= _recentWindow) break;
        }
    }
}
