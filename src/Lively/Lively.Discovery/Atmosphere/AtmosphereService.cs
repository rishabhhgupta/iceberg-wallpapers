using Lively.Discovery.Solar;
using Lively.Discovery.Theming;
using Lively.Discovery.Time;
using Lively.Discovery.Weather;

namespace Lively.Discovery;

/// <summary>
/// Everything the app needs to know about "right now": the phase of the day, the
/// sky, the colours that follow from them, and when any of that will next change.
/// </summary>
public sealed record AtmosphereSnapshot
{
    public required DayPhase Phase { get; init; }
    public WeatherKind? Weather { get; init; }
    public required Palette Palette { get; init; }
    public SunTimes? Sun { get; init; }
    public WeatherSnapshot? Reading { get; init; }
    public LocationInfo? Location { get; init; }
    public required DateTimeOffset At { get; init; }

    /// <summary>True when sunrise and sunset were used rather than fixed hours.</summary>
    public bool UsedSolar { get; init; }

    public string? City => Reading?.City ?? Location?.City;
    public double? TemperatureC => Reading?.TemperatureC;
    public bool WeatherIsStale => Reading?.IsStale ?? false;
}

/// <summary>
/// Turns a weather reading into the palette and phase the UI and the rotation
/// engine both use.
///
/// One deliberate rule: this never throws because the weather failed. A wallpaper
/// app that shows an error screen at sunrise because an IP lookup timed out is
/// worse than one that quietly uses the clock.
/// </summary>
public sealed class AtmosphereService
{
    private readonly IWeatherProvider _weather;
    private readonly TimeProvider _clock;
    private AtmosphereSnapshot? _current;

    public AtmosphereService(IWeatherProvider? weather, TimeProvider? clock = null)
    {
        // Iceberg does not use weather: the phase of the day comes from the
        // clock alone. A null provider must therefore be a supported case, not
        // a crash at startup.
        _weather = weather ?? new NoWeatherProvider();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The "no weather" provider: always empty, never touches the network.</summary>
    private sealed class NoWeatherProvider : IWeatherProvider
    {
        public TimeSpan MaxAge => TimeSpan.FromMinutes(15);

        public Task<WeatherSnapshot?> CurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<WeatherSnapshot?>(null);

        public void Forget()
        {
        }
    }

    public AtmosphereSnapshot? Current => _current;

    public async Task<AtmosphereSnapshot> RefreshAsync(
        uint? wallpaperAccent = null,
        CancellationToken cancellationToken = default)
    {
        WeatherSnapshot? reading;
        try
        {
            reading = await _weather.CurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            reading = null;
        }

        var snapshot = Compute(reading, wallpaperAccent);
        _current = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Builds a snapshot without touching the network. Exposed so a theme change
    /// (for example, the person picked a wallpaper with a strong colour) can be
    /// applied instantly while the weather is unchanged.
    /// </summary>
    public AtmosphereSnapshot Compute(WeatherSnapshot? reading, uint? wallpaperAccent = null)
    {
        var now = _clock.GetUtcNow();
        var location = reading?.Location;

        var localNow = location is null
            ? _clock.GetLocalNow()
            : TimeZoneInfo.ConvertTime(now, location.TimeZone);

        SunTimes? sun = location is null
            ? null
            : SolarCalculator.Get(localNow, location.Latitude, location.Longitude, location.TimeZone);

        var phase = sun is { } times
            ? DayPhaseCalculator.Determine(localNow, sun: times)
            : DayPhaseCalculator.FromWallClock(localNow);

        // A reading that contradicts the sun is not trusted for the phase. At
        // 2 a.m. a broken "is_day: 1" must not produce a morning palette.
        var weather = reading?.Kind;

        return new AtmosphereSnapshot
        {
            Phase = phase,
            Weather = weather,
            Palette = PaletteFactory.Create(phase, weather, wallpaperAccent),
            Sun = sun,
            Reading = reading,
            Location = location,
            At = localNow,
            UsedSolar = sun is not null,
        };
    }

    /// <summary>
    /// When to recompute. Capped at fifteen minutes so a weather change is
    /// noticed, and so a suspended laptop wakes up and refreshes promptly.
    /// </summary>
    public DateTimeOffset NextRefreshAfter(AtmosphereSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var changeAt = DayPhaseCalculator.NextChangeAfter(snapshot.At, snapshot.Sun);
        var weatherAt = snapshot.Reading is { } r
            ? r.FetchedAt + _weather.MaxAge
            : DateTimeOffset.UtcNow;

        var soonest = new[] { changeAt ?? DateTimeOffset.MaxValue, weatherAt, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15) }
            .Where(t => t > DateTimeOffset.UtcNow)
            .DefaultIfEmpty(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1))
            .Min();

        return soonest;
    }

    /// <summary>Search words for the rotation engine, based on the sky right now.</summary>
    public string? SuggestSearchHint()
    {
        if (_current is not { } snapshot) return null;

        if (snapshot.Weather is { } weather)
        {
            return weather switch
            {
                WeatherKind.Rain => "rain",
                WeatherKind.Snow => "snow",
                WeatherKind.Mist => "fog",
                WeatherKind.Thunderstorm => "storm",
                WeatherKind.Overcast => "clouds",
                WeatherKind.PartlyCloudy => "clouds",
                _ => PhaseHint(snapshot.Phase),
            };
        }

        return PhaseHint(snapshot.Phase);
    }

    private static string PhaseHint(DayPhase phase) => phase switch
    {
        DayPhase.Night => "night sky",
        DayPhase.Dawn => "sunrise",
        DayPhase.Morning => "morning",
        DayPhase.Afternoon => "landscape",
        DayPhase.GoldenHour => "sunset",
        _ => "landscape",
    };
}
