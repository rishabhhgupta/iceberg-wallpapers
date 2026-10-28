namespace Lively.Discovery.Rotation;

/// <summary>
/// When the next wallpaper change is due.
///
/// The interesting case is sleep. A laptop closed for ten hours with a one-hour
/// interval must change once when it wakes, not ten times in a row. Everything
/// here is computed from a stored "last change" instant rather than from a
/// running timer, so a missed window simple means one change is due.
/// </summary>
public static class RotationClock
{
    public static bool IsDue(DateTimeOffset lastChange, DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) return false;
        return now - lastChange >= interval;
    }

    /// <summary>
    /// The next instant after <paramref name="now"/> that lands on the interval
    /// grid anchored at <paramref name="lastChange"/>. Skips every missed slot
    /// instead of returning a timestamp in the past.
    /// </summary>
    public static DateTimeOffset NextChangeAfter(DateTimeOffset lastChange, DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");

        if (now <= lastChange) return lastChange + interval;

        var elapsedTicks = (now - lastChange).Ticks;
        var steps = elapsedTicks / interval.Ticks + 1;

        // Guard against absurd step counts if the clock is set far forward.
        if (steps > 1_000_000) return now + interval;

        return lastChange + TimeSpan.FromTicks(interval.Ticks * steps);
    }

    /// <summary>
    /// How many changes were missed while asleep. Reported for diagnostics only;
    /// the app still only performs one change on wake.
    /// </summary>
    public static int MissedChanges(DateTimeOffset lastChange, DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || now <= lastChange) return 0;
        var elapsed = (now - lastChange).Ticks;
        var whole = elapsed / interval.Ticks;
        return whole > int.MaxValue ? int.MaxValue : (int)whole;
    }
}
