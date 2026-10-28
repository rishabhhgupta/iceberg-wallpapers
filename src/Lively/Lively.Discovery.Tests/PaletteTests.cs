using Lively.Discovery.Theming;
using Lively.Discovery.Time;
using Lively.Discovery.Weather;
using Xunit;

namespace Lively.Discovery.Tests;

public class PaletteTests
{
    public static TheoryData<DayPhase> AllPhases => new()
    {
        DayPhase.Night,
        DayPhase.Dawn,
        DayPhase.Morning,
        DayPhase.Afternoon,
        DayPhase.GoldenHour,
    };

    [Fact]
    public void Night_is_dark_and_morning_is_light()
    {
        Assert.True(PaletteFactory.Create(DayPhase.Night).IsDark);
        Assert.False(PaletteFactory.Create(DayPhase.Morning).IsDark);
        Assert.False(PaletteFactory.Create(DayPhase.Afternoon).IsDark);
    }

    [Theory]
    [MemberData(nameof(AllPhases))]
    public void Text_is_readable_on_every_phase(DayPhase phase)
    {
        var palette = PaletteFactory.Create(phase);

        var contrast = ColorMath.ContrastRatio(palette.TextPrimary, palette.BackgroundTop);

        Assert.True(contrast >= 4.5, $"{phase} text contrast was only {contrast:0.0}:1");
    }

    [Theory]
    [MemberData(nameof(AllPhases))]
    public void Accent_stays_visible_against_the_background(DayPhase phase)
    {
        var palette = PaletteFactory.Create(phase);

        Assert.True(ColorMath.ContrastRatio(palette.Accent, palette.BackgroundTop) >= 3.0);
    }

    [Theory]
    [InlineData(WeatherKind.Rain)]
    [InlineData(WeatherKind.Snow)]
    [InlineData(WeatherKind.Thunderstorm)]
    [InlineData(WeatherKind.Mist)]
    [InlineData(WeatherKind.Overcast)]
    public void Weather_never_breaks_readability(WeatherKind weather)
    {
        foreach (var phase in AllPhases)
        {
            var palette = PaletteFactory.Create(phase, weather);

            Assert.True(
                ColorMath.ContrastRatio(palette.TextPrimary, palette.BackgroundTop) >= 4.5,
                $"{phase} in {weather} was unreadable");
        }
    }

    [Fact]
    public void Snow_at_two_in_the_morning_does_not_make_the_app_white()
    {
        var palette = PaletteFactory.Create(DayPhase.Night, WeatherKind.Snow);

        Assert.True(palette.IsDark);
        Assert.True(ColorMath.Luminance(palette.BackgroundTop) < 0.20);
    }

    [Fact]
    public void Thunderstorm_at_noon_does_not_make_the_app_black()
    {
        var palette = PaletteFactory.Create(DayPhase.Afternoon, WeatherKind.Thunderstorm);

        Assert.False(palette.IsDark);
        Assert.True(ColorMath.Luminance(palette.BackgroundTop) > 0.30);
    }

    [Fact]
    public void Rain_looks_different_from_a_clear_sky()
    {
        var clear = PaletteFactory.Create(DayPhase.Afternoon, WeatherKind.Clear);
        var rain = PaletteFactory.Create(DayPhase.Afternoon, WeatherKind.Rain);

        Assert.NotEqual(clear.BackgroundTop, rain.BackgroundTop);
        Assert.True(ColorMath.Saturation(rain.BackgroundTop) <= ColorMath.Saturation(clear.BackgroundTop) + 1e-9);
    }

    [Fact]
    public void A_clear_sky_falls_back_to_time_of_day_rather_than_forcing_a_preset()
    {
        var withClear = PaletteFactory.Create(DayPhase.Night, WeatherKind.Clear);
        var withoutWeather = PaletteFactory.Create(DayPhase.Night);

        Assert.Equal(withoutWeather.BackgroundTop, withClear.BackgroundTop);
    }

    [Fact]
    public void An_unknown_weather_kind_is_ignored()
    {
        var unknown = PaletteFactory.Create(DayPhase.Morning, (WeatherKind)999);
        var plain = PaletteFactory.Create(DayPhase.Morning);

        Assert.Equal(plain.BackgroundTop, unknown.BackgroundTop);
    }

    [Fact]
    public void The_wallpaper_colour_pulls_the_accent_towards_it()
    {
        var red = ColorMath.Parse("C81E1E");

        var plain = PaletteFactory.Create(DayPhase.Morning);
        var matched = PaletteFactory.Create(DayPhase.Morning, null, red);

        Assert.NotEqual(plain.Accent, matched.Accent);
        Assert.True(
            HueDistance(matched.Accent, red) < HueDistance(plain.Accent, red),
            "the matched accent should be closer in hue to the wallpaper");
        Assert.Contains("Matched", matched.Name);
    }

    [Fact]
    public void Matching_a_very_dark_wallpaper_still_leaves_readable_text()
    {
        var nearlyBlack = ColorMath.Parse("050505");

        var palette = PaletteFactory.Create(DayPhase.Morning, null, nearlyBlack);

        Assert.True(ColorMath.ContrastRatio(palette.TextPrimary, palette.BackgroundTop) >= 4.5);
        Assert.False(palette.IsDark);
    }

    [Fact]
    public void Neutral_follows_the_requested_light_or_dark()
    {
        Assert.True(PaletteFactory.Neutral(dark: true).IsDark);
        Assert.False(PaletteFactory.Neutral(dark: false).IsDark);
    }

    [Fact]
    public void The_gradient_has_exactly_two_stops()
    {
        Assert.Equal(2, PaletteFactory.Create(DayPhase.Dawn).Gradient.Count);
    }

    [Fact]
    public void Palette_names_read_like_english()
    {
        var name = PaletteFactory.Create(DayPhase.GoldenHour, WeatherKind.Rain).Name;

        Assert.Equal("Golden hour · Rain", name);
    }

    private static double HueDistance(uint a, uint b)
    {
        var (ha, _, _) = ColorMath.ToHsl(a);
        var (hb, _, _) = ColorMath.ToHsl(b);
        var diff = Math.Abs(ha - hb) % 360;
        return Math.Min(diff, 360 - diff);
    }
}

public class ColorMathTests
{
    [Theory]
    [InlineData("#FF0000", 0xFFFF0000u)]   // no alpha means fully opaque
    [InlineData("00FF00", 0xFF00FF00u)]
    [InlineData("#800000FF", 0x800000FFu)]
    public void Parses_hex_with_and_without_alpha(string input, uint expected)
    {
        Assert.Equal(expected, ColorMath.Parse(input));
    }

    [Fact]
    public void Rejects_nonsense()
    {
        Assert.Throws<FormatException>(() => ColorMath.Parse("#12345"));
        Assert.ThrowsAny<ArgumentException>(() => ColorMath.Parse("  "));
    }

    [Fact]
    public void Contrast_of_black_on_white_is_the_maximum()
    {
        Assert.Equal(21.0, ColorMath.ContrastRatio(0xFF000000, 0xFFFFFFFF), 1);
    }

    [Fact]
    public void Contrast_of_a_colour_with_itself_is_one()
    {
        Assert.Equal(1.0, ColorMath.ContrastRatio(0xFF336699, 0xFF336699), 3);
    }

    [Fact]
    public void Readable_text_picks_black_on_white_and_white_on_black()
    {
        Assert.Equal(0xFF101418u, ColorMath.ReadableText(0xFFFFFFFF));
        Assert.Equal(0xFFFFFFFFu, ColorMath.ReadableText(0xFF000000));
    }

    [Fact]
    public void Hsl_round_trips()
    {
        var original = ColorMath.Parse("2F6FA8");
        var (h, s, l) = ColorMath.ToHsl(original);
        var again = ColorMath.FromHsl(h, s, l);

        Assert.Equal(ColorMath.R(original), ColorMath.R(again));
        Assert.Equal(ColorMath.G(original), ColorMath.G(again));
        Assert.Equal(ColorMath.B(original), ColorMath.B(again));
    }

    [Fact]
    public void Greys_have_no_saturation()
    {
        Assert.Equal(0, ColorMath.Saturation(ColorMath.Parse("808080")), 3);
    }

    [Fact]
    public void Ensure_contrast_leaves_an_already_readable_colour_alone()
    {
        var accent = ColorMath.Parse("1144FF");
        Assert.Equal(accent, ColorMath.EnsureContrast(accent, 0xFFFFFFFF));
    }

    [Fact]
    public void Ensure_contrast_fixes_an_invisible_colour()
    {
        var nearlyWhite = ColorMath.Parse("F8F8F8");

        var fixedColour = ColorMath.EnsureContrast(nearlyWhite, 0xFFFFFFFF, 3.0);

        Assert.True(ColorMath.ContrastRatio(fixedColour, 0xFFFFFFFF) >= 3.0);
    }
}
