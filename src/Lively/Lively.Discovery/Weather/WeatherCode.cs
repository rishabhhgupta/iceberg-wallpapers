namespace Lively.Discovery.Weather;

/// <summary>What the sky is doing, in the few categories a wallpaper cares about.</summary>
public enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Overcast,
    Mist,
    Rain,
    Snow,
    Thunderstorm,
}

/// <summary>
/// Maps WMO weather codes (what Open-Meteo returns) onto <see cref="WeatherKind"/>.
///
/// Kept as a pure function with a full code table so it can be tested against
/// every value 0-99, including the gaps that must be treated as unknown.
/// </summary>
public static class WeatherCode
{
    /// <summary>
    /// Returns the kind, or <c>null</c> when the code is not a real WMO code.
    /// Unknown is not Clear: guessing "sunny" from a broken response would
    /// override the time of day with false information.
    /// </summary>
    public static WeatherKind? ToKind(int? code)
    {
        if (code is not { } c) return null;
        if (c < 0 || c > 99) return null;

        return c switch
        {
            0 => WeatherKind.Clear,
            1 => WeatherKind.Clear,
            2 => WeatherKind.PartlyCloudy,
            3 => WeatherKind.Overcast,
            45 or 48 => WeatherKind.Mist,
            (>= 51 and <= 57) => WeatherKind.Rain,       // drizzle
            (>= 61 and <= 67) => WeatherKind.Rain,       // rain
            (>= 71 and <= 77) => WeatherKind.Snow,       // snow fall
            (>= 80 and <= 82) => WeatherKind.Rain,       // rain showers
            85 or 86 => WeatherKind.Snow,                // snow showers
            (>= 95 and <= 99) => WeatherKind.Thunderstorm,
            _ => null,
        };
    }

    /// <summary>
    /// True when the sky is actually worth acting on. A clear sky should fall
    /// back to time of day rather than force a "clear" preset, otherwise a sunny
    /// morning and a sunny midnight would look identical.
    /// </summary>
    public static bool IsMeaningful(WeatherKind? kind) =>
        kind is not null and not WeatherKind.Clear;
}
