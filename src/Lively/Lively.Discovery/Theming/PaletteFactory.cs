using Lively.Discovery.Time;
using Lively.Discovery.Weather;

namespace Lively.Discovery.Theming;

/// <summary>
/// Every colour the app needs, in one object. Screens bind to this instead of
/// hard-coding hex values, which is what lets the whole app change with the time
/// of day and the weather.
/// </summary>
public sealed record Palette
{
    public required string Name { get; init; }
    public required uint BackgroundTop { get; init; }
    public required uint BackgroundBottom { get; init; }
    public required uint Surface { get; init; }
    public required uint Accent { get; init; }
    public required uint TextPrimary { get; init; }
    public required uint TextSecondary { get; init; }
    public required uint Divider { get; init; }
    public bool IsDark { get; init; }

    public IReadOnlyList<uint> Gradient => new[] { BackgroundTop, BackgroundBottom };
}

/// <summary>
/// Builds the palette from the clock, the sky, and optionally the wallpaper.
///
/// This is deliberately a pure function: given the same inputs it always
/// produces the same colours. That makes the look of the app testable, and it
/// means a theme bug can be reported as "at 19:40 in the rain it went muddy"
/// rather than "it looked wrong once".
/// </summary>
public static class PaletteFactory
{
    private sealed record PhaseSeed(uint Top, uint Bottom, uint Accent, bool Dark);

    private static readonly Dictionary<DayPhase, PhaseSeed> Seeds = new()
    {
        [DayPhase.Night] = new(ColorMath.Parse("0A1024"), ColorMath.Parse("141C33"), ColorMath.Parse("7FA8FF"), true),
        [DayPhase.Dawn] = new(ColorMath.Parse("241A2E"), ColorMath.Parse("3B2434"), ColorMath.Parse("FF9EC0"), true),
        [DayPhase.Morning] = new(ColorMath.Parse("DCEAF7"), ColorMath.Parse("F2F8FF"), ColorMath.Parse("2F6FA8"), false),
        [DayPhase.Afternoon] = new(ColorMath.Parse("FBEFDD"), ColorMath.Parse("FFF8EE"), ColorMath.Parse("B96A21"), false),
        [DayPhase.GoldenHour] = new(ColorMath.Parse("2A1712"), ColorMath.Parse("4A2A1C"), ColorMath.Parse("FFB55E"), true),
    };

    /// <summary>
    /// Weather tint and how strongly to apply it. Clear is intentionally absent:
    /// a clear sky should fall through to the time of day, not flatten it.
    /// </summary>
    private static readonly Dictionary<WeatherKind, (uint Tint, double Amount)> WeatherTints = new()
    {
        [WeatherKind.PartlyCloudy] = (ColorMath.Parse("BFD0DE"), 0.10),
        [WeatherKind.Overcast] = (ColorMath.Parse("8C97A3"), 0.28),
        [WeatherKind.Mist] = (ColorMath.Parse("B9C2C9"), 0.22),
        [WeatherKind.Rain] = (ColorMath.Parse("55677A"), 0.30),
        [WeatherKind.Snow] = (ColorMath.Parse("E9F2F8"), 0.32),
        [WeatherKind.Thunderstorm] = (ColorMath.Parse("2B3240"), 0.38),
    };

    public static Palette Create(
        DayPhase phase,
        WeatherKind? weather = null,
        uint? wallpaperAccent = null)
    {
        var seed = Seeds.TryGetValue(phase, out var found) ? found : Seeds[DayPhase.Morning];

        var top = seed.Top;
        var bottom = seed.Bottom;
        var accent = seed.Accent;

        if (weather is { } kind && WeatherTints.TryGetValue(kind, out var tint))
        {
            top = ColorMath.Blend(top, tint.Tint, tint.Amount);
            bottom = ColorMath.Blend(bottom, tint.Tint, tint.Amount);
            accent = ColorMath.Blend(accent, tint.Tint, tint.Amount * 0.5);
        }

        // A night sky must stay a night sky: snow at 2 a.m. must not produce a
        // white app. The same rule keeps a light theme light in a thunderstorm.
        top = ClampTone(top, seed.Dark);
        bottom = ClampTone(bottom, seed.Dark);

        if (wallpaperAccent is { } wallpaper)
        {
            // "Match the wallpaper": tint the background slightly and pull the
            // accent most of the way, so the colour is clearly the photo's.
            var backgroundPull = seed.Dark ? 0.16 : 0.10;
            var accentPull = 0.45;

            top = ClampTone(ColorMath.Blend(top, wallpaper, backgroundPull), seed.Dark);
            bottom = ClampTone(ColorMath.Blend(bottom, wallpaper, backgroundPull * 0.6), seed.Dark);
            accent = ColorMath.Blend(accent, wallpaper, accentPull);
        }

        var text = ColorMath.ReadableText(top);
        var surface = seed.Dark
            ? ColorMath.Blend(top, 0xFFFFFFFF, 0.07)
            : ColorMath.Blend(top, 0xFF000000, 0.04);

        return new Palette
        {
            Name = Describe(phase, weather, wallpaperAccent is not null),
            BackgroundTop = top,
            BackgroundBottom = bottom,
            Surface = surface,
            Accent = ColorMath.EnsureContrast(accent, top, 3.0),
            TextPrimary = text,
            TextSecondary = ColorMath.Blend(text, top, 0.35),
            Divider = ColorMath.Blend(text, top, 0.78),
            IsDark = seed.Dark,
        };
    }

    /// <summary>A plain light or dark palette, for when time-awareness is off.</summary>
    public static Palette Neutral(bool dark) =>
        Create(dark ? DayPhase.Night : DayPhase.Morning);

    /// <summary>
    /// Makes dark palettes dark and light palettes light regardless of what the
    /// weather tint did to the luminance.
    /// </summary>
    private static uint ClampTone(uint color, bool dark)
    {
        var (h, s, l) = ColorMath.ToHsl(color);
        var clamped = dark ? Math.Min(l, 0.30) : Math.Max(l, 0.55);
        return Math.Abs(clamped - l) < 1e-6 ? color : ColorMath.FromHsl(h, s, clamped);
    }

    private static string Describe(DayPhase phase, WeatherKind? weather, bool matchedWallpaper)
    {
        var name = phase switch
        {
            DayPhase.Night => "Night",
            DayPhase.Dawn => "Dawn",
            DayPhase.Morning => "Morning",
            DayPhase.Afternoon => "Afternoon",
            DayPhase.GoldenHour => "Golden hour",
            _ => "Day",
        };

        if (weather is { } kind && WeatherCode.IsMeaningful(kind))
        {
            name += kind switch
            {
                WeatherKind.PartlyCloudy => " · Some cloud",
                WeatherKind.Overcast => " · Overcast",
                WeatherKind.Mist => " · Mist",
                WeatherKind.Rain => " · Rain",
                WeatherKind.Snow => " · Snow",
                WeatherKind.Thunderstorm => " · Storm",
                _ => string.Empty,
            };
        }

        return matchedWallpaper ? name + " · Matched" : name;
    }
}
