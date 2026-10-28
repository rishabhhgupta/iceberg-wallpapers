using Lively.Discovery.Solar;

namespace Lively.Discovery.Time;

/// <summary>
/// The parts of the day the wallpaper rotation cares about. Kept deliberately
/// small: every extra phase is another rule that can disagree with itself.
/// </summary>
public enum DayPhase
{
    Night,
    Dawn,
    Morning,
    Afternoon,
    GoldenHour,
}

/// <summary>
/// Decides which <see cref="DayPhase"/> an instant falls in.
///
/// Two paths, in priority order:
///   1. Real solar events, when a location is known (Nagomi's rules).
///   2. Fixed wall-clock hours, when it is not.
///
/// The fallback is not a bug: geolocation is often blocked inside a wallpaper
/// host, and a pond that freezes at dawn is worse than one that is an hour off.
/// </summary>
public static class DayPhaseCalculator
{
    /// <summary>Minutes before sunrise that already count as night.</summary>
    public const int PreDawnMinutes = 30;

    /// <summary>Minutes before sunset that the golden hour starts.</summary>
    public const int GoldenHourBeforeMinutes = 45;

    /// <summary>Minutes after sunset that the golden hour ends.</summary>
    public const int GoldenHourAfterMinutes = 35;

    // Wall-clock fallback boundaries, in minutes from local midnight.
    private const int FallbackDawnStart = 5 * 60;        // 05:00
    private const int FallbackMorningStart = 7 * 60;     // 07:00
    private const int FallbackAfternoonStart = 12 * 60;  // 12:00
    private const int FallbackGoldenStart = 17 * 60;     // 17:00
    private const int FallbackNightStart = 19 * 60 + 30; // 19:30

    public static DayPhase Determine(DateTimeOffset instant, SunTimes? sun)
    {
        if (sun is { } s && s.Sunset > s.Sunrise)
        {
            var t = instant.UtcTicks;
            var sunrise = s.Sunrise.UtcTicks;
            var sunset = s.Sunset.UtcTicks;

            var goldenStart = sunset - TimeSpan.FromMinutes(GoldenHourBeforeMinutes).Ticks;
            var goldenEnd = sunset + TimeSpan.FromMinutes(GoldenHourAfterMinutes).Ticks;

            if (t >= goldenStart && t <= goldenEnd) return DayPhase.GoldenHour;
            if (t < sunrise - TimeSpan.FromMinutes(PreDawnMinutes).Ticks) return DayPhase.Night;
            if (t > goldenEnd) return DayPhase.Night;
            if (t < sunrise) return DayPhase.Dawn;

            var solarNoon = sunrise + (sunset - sunrise) / 2;
            return t < solarNoon ? DayPhase.Morning : DayPhase.Afternoon;
        }

        return FromWallClock(instant);
    }

    public static DayPhase FromWallClock(DateTimeOffset instant)
    {
        var minutes = instant.Hour * 60 + instant.Minute;

        if (minutes < FallbackDawnStart || minutes >= FallbackNightStart) return DayPhase.Night;
        if (minutes < FallbackMorningStart) return DayPhase.Dawn;
        if (minutes < FallbackAfternoonStart) return DayPhase.Morning;
        if (minutes < FallbackGoldenStart) return DayPhase.Afternoon;
        return DayPhase.GoldenHour;
    }

    /// <summary>
    /// Next instant at which the phase will change, or null when it cannot be
    /// known (polar latitudes without a location). Used to show "next change"
    /// and to schedule work precisely instead of polling every minute.
    /// </summary>
    public static DateTimeOffset? NextChangeAfter(DateTimeOffset instant, SunTimes? sun)
    {
        if (sun is not { } s || s.Sunset <= s.Sunrise) return null;

        var current = Determine(instant, sun);

        var boundaries = new[]
        {
            s.Sunrise - TimeSpan.FromMinutes(PreDawnMinutes),
            s.Sunrise,
            s.Sunset - TimeSpan.FromMinutes(GoldenHourBeforeMinutes),
            s.Sunset + TimeSpan.FromMinutes(GoldenHourAfterMinutes),
        };

        foreach (var boundary in boundaries.OrderBy(b => b))
        {
            // The first boundary strictly in the future AND genuinely different
            // from the current phase is the real change instant.
            if (boundary > instant && Determine(boundary, sun) != current) return boundary;
        }

        // Every boundary for today is behind us. Tomorrow needs tomorrow's
        // declination, so report unknown rather than guess.
        return null;
    }
}
