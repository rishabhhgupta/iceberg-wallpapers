using Lively.Discovery.Solar;
using Lively.Discovery.Time;
using Lively.Discovery.Weather;
using Xunit;

namespace Lively.Discovery.Tests;

public class SolarTests
{
    private static TimeZoneInfo London =>
        TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    [Fact]
    public void London_midsummer_sunrise_matches_reality()
    {
        var instant = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.FromHours(1));
        var sun = SolarCalculator.Get(instant, 51.5074, -0.1278, London);

        Assert.True(sun.HasValue);
        var value = sun.Value;

        var sunriseLocal = TimeZoneInfo.ConvertTime(value.Sunrise, London);
        var sunsetLocal = TimeZoneInfo.ConvertTime(value.Sunset, London);

        // Real values for London on 21 June are about 04:43 and 21:21.
        Assert.Equal(4, sunriseLocal.Hour);
        Assert.InRange(sunriseLocal.Minute, 30, 55);
        Assert.Equal(21, sunsetLocal.Hour);
        Assert.InRange(sunsetLocal.Minute, 0, 45);
        Assert.True(value.Sunset > value.Sunrise);
    }

    [Fact]
    public void London_midwinter_has_a_short_day()
    {
        var instant = new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero);
        var sun = SolarCalculator.Get(instant, 51.5074, -0.1278, London);

        Assert.NotNull(sun);
        var hours = (sun!.Value.Sunset - sun.Value.Sunrise).TotalHours;
        Assert.InRange(hours, 7.5, 8.5);
    }

    [Fact]
    public void Polar_day_returns_null_instead_of_a_lying_sunrise()
    {
        // Tromso in midsummer: the sun never sets.
        var instant = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.FromHours(2));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        var sun = SolarCalculator.Get(instant, 69.6492, 18.9553, zone);

        Assert.Null(sun);
    }

    [Fact]
    public void Invalid_coordinates_are_rejected()
    {
        var instant = DateTimeOffset.UtcNow;
        var zone = London;

        Assert.Null(SolarCalculator.Get(instant, 91, 0, zone));
        Assert.Null(SolarCalculator.Get(instant, 0, 181, zone));
        Assert.Null(SolarCalculator.Get(instant, double.NaN, 0, zone));
    }
}

public class DayPhaseTests
{
    private static SunTimes NoonToEightPm => new(
        new DateTimeOffset(2026, 6, 21, 6, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 6, 21, 20, 0, 0, TimeSpan.Zero));

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 6, 21, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3, 0, DayPhase.Night)]
    [InlineData(5, 45, DayPhase.Dawn)]
    [InlineData(10, 0, DayPhase.Morning)]
    [InlineData(14, 0, DayPhase.Afternoon)]
    [InlineData(19, 30, DayPhase.GoldenHour)]
    [InlineData(21, 0, DayPhase.Night)]
    public void Phases_follow_the_sun(int hour, int minute, DayPhase expected)
    {
        Assert.Equal(expected, DayPhaseCalculator.Determine(At(hour, minute), NoonToEightPm));
    }

    [Fact]
    public void Golden_hour_is_centred_on_sunset()
    {
        // Sunset 20:00, window -45 / +35 minutes.
        Assert.Equal(DayPhase.GoldenHour, DayPhaseCalculator.Determine(At(19, 15), NoonToEightPm));
        Assert.Equal(DayPhase.GoldenHour, DayPhaseCalculator.Determine(At(20, 35), NoonToEightPm));
        Assert.Equal(DayPhase.Afternoon, DayPhaseCalculator.Determine(At(19, 14), NoonToEightPm));
        Assert.Equal(DayPhase.Night, DayPhaseCalculator.Determine(At(20, 36), NoonToEightPm));
    }

    [Fact]
    public void Without_sun_data_it_falls_back_to_the_clock()
    {
        Assert.Equal(DayPhase.Night, DayPhaseCalculator.Determine(At(2), null));
        Assert.Equal(DayPhase.Dawn, DayPhaseCalculator.Determine(At(6), null));
        Assert.Equal(DayPhase.Morning, DayPhaseCalculator.Determine(At(9), null));
        Assert.Equal(DayPhase.Afternoon, DayPhaseCalculator.Determine(At(13), null));
        Assert.Equal(DayPhase.GoldenHour, DayPhaseCalculator.Determine(At(18), null));
        Assert.Equal(DayPhase.Night, DayPhaseCalculator.Determine(At(22), null));
    }

    [Fact]
    public void A_broken_sun_pair_falls_back_rather_than_mislabelling()
    {
        var backwards = new SunTimes(At(20), At(6));
        Assert.Equal(DayPhase.Night, DayPhaseCalculator.Determine(At(2), backwards));
    }

    [Fact]
    public void Next_change_is_in_the_future_and_genuinely_different()
    {
        var next = DayPhaseCalculator.NextChangeAfter(At(12), NoonToEightPm);
        Assert.NotNull(next);
        Assert.True(next!.Value > At(12));
        Assert.NotEqual(
            DayPhaseCalculator.Determine(At(12), NoonToEightPm),
            DayPhaseCalculator.Determine(next.Value, NoonToEightPm));
    }

    [Fact]
    public void Next_change_is_null_when_today_has_no_boundaries_left()
    {
        Assert.Null(DayPhaseCalculator.NextChangeAfter(At(23), NoonToEightPm));
    }
}

public class WeatherCodeTests
{
    [Theory]
    [InlineData(0, WeatherKind.Clear)]
    [InlineData(1, WeatherKind.Clear)]
    [InlineData(2, WeatherKind.PartlyCloudy)]
    [InlineData(3, WeatherKind.Overcast)]
    [InlineData(45, WeatherKind.Mist)]
    [InlineData(48, WeatherKind.Mist)]
    [InlineData(53, WeatherKind.Rain)]
    [InlineData(65, WeatherKind.Rain)]
    [InlineData(71, WeatherKind.Snow)]
    [InlineData(81, WeatherKind.Rain)]
    [InlineData(86, WeatherKind.Snow)]
    [InlineData(95, WeatherKind.Thunderstorm)]
    [InlineData(99, WeatherKind.Thunderstorm)]
    public void Known_codes_map_to_the_right_kind(int code, WeatherKind expected)
    {
        Assert.Equal(expected, WeatherCode.ToKind(code));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(4)]
    [InlineData(60)]
    public void Unknown_codes_are_unknown_not_clear(int code)
    {
        Assert.Null(WeatherCode.ToKind(code));
    }

    [Fact]
    public void Null_is_unknown()
    {
        Assert.Null(WeatherCode.ToKind(null));
    }

    [Theory]
    [InlineData(WeatherKind.Clear, false)]
    [InlineData(WeatherKind.PartlyCloudy, true)]
    [InlineData(WeatherKind.Rain, true)]
    [InlineData(null, false)]
    public void Only_meaningful_weather_overrides_the_clock(WeatherKind? kind, bool expected)
    {
        Assert.Equal(expected, WeatherCode.IsMeaningful(kind));
    }
}
