using System.Net;
using System.Text;
using Lively.Discovery;
using Lively.Discovery.Time;
using Lively.Discovery.Weather;
using Xunit;

namespace Lively.Discovery.Tests;

internal sealed class RoutingHandler : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> _router;

    public RoutingHandler(Func<string, HttpResponseMessage> router) => _router = router;

    public List<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        return Task.FromResult(_router(url));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class FixedClock : TimeProvider
{
    private readonly DateTimeOffset _now;
    public FixedClock(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

public class WeatherProviderTests
{
    private const string LocationBody = """
    {
      "city": "London",
      "country_name": "United Kingdom",
      "latitude": 51.5074,
      "longitude": -0.1278,
      "timezone": "Europe/London"
    }
    """;

    private const string ForecastBody = """
    {
      "latitude": 51.5,
      "longitude": -0.13,
      "timezone": "Europe/London",
      "current": { "time": "2026-06-21T12:00", "temperature_2m": 21.4, "weather_code": 3, "is_day": 1 }
    }
    """;

    private static RoutingHandler WorkingHandler() => new(url =>
        url.Contains("open-meteo.com", StringComparison.Ordinal)
            ? RoutingHandler.Json(ForecastBody)
            : RoutingHandler.Json(LocationBody));

    [Fact]
    public async Task Reads_temperature_weather_and_city()
    {
        var provider = new OpenMeteoWeatherProvider(new HttpClient(WorkingHandler()));

        var snapshot = await provider.CurrentAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(21.4, snapshot!.TemperatureC);
        Assert.Equal(WeatherKind.Overcast, snapshot.Kind);
        Assert.Equal("London", snapshot.City);
        Assert.Equal(51.5074, snapshot.Location!.Latitude, 4);
        Assert.False(snapshot.IsStale);
    }

    [Fact]
    public async Task A_fresh_reading_is_reused_without_asking_again()
    {
        var handler = WorkingHandler();
        var provider = new OpenMeteoWeatherProvider(new HttpClient(handler), maxAge: TimeSpan.FromMinutes(30));

        await provider.CurrentAsync();
        var before = handler.Requests.Count;
        await provider.CurrentAsync();

        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_old_reading_but_marks_it_stale()
    {
        var failing = false;
        var handler = new RoutingHandler(url =>
        {
            if (failing) return RoutingHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
            return url.Contains("open-meteo.com", StringComparison.Ordinal)
                ? RoutingHandler.Json(ForecastBody)
                : RoutingHandler.Json(LocationBody);
        });

        var provider = new OpenMeteoWeatherProvider(new HttpClient(handler), maxAge: TimeSpan.Zero);

        var first = await provider.CurrentAsync();
        Assert.False(first!.IsStale);

        failing = true;
        var second = await provider.CurrentAsync();

        Assert.NotNull(second);
        Assert.True(second!.IsStale);
        Assert.Equal(first.TemperatureC, second.TemperatureC);
    }

    [Fact]
    public async Task With_nothing_working_the_answer_is_null_rather_than_a_guess()
    {
        var handler = new RoutingHandler(_ => RoutingHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        var provider = new OpenMeteoWeatherProvider(new HttpClient(handler));

        Assert.Null(await provider.CurrentAsync());
    }

    [Fact]
    public async Task Location_falls_back_to_the_second_provider()
    {
        var handler = new RoutingHandler(url =>
        {
            if (url.Contains("ipapi.co", StringComparison.Ordinal))
            {
                return RoutingHandler.Json("{}", HttpStatusCode.TooManyRequests);
            }

            if (url.Contains("ipwho.is", StringComparison.Ordinal))
            {
                return RoutingHandler.Json("""
                {
                  "success": true,
                  "city": "Paris",
                  "country": "France",
                  "latitude": 48.8566,
                  "longitude": 2.3522,
                  "timezone": { "id": "Europe/Paris" }
                }
                """);
            }

            return RoutingHandler.Json(ForecastBody);
        });

        var provider = new OpenMeteoWeatherProvider(new HttpClient(handler));
        var snapshot = await provider.CurrentAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(48.8566, snapshot!.Location!.Latitude, 4);
        Assert.Contains(handler.Requests, u => u.Contains("ipwho.is", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Forgetting_forces_a_new_lookup()
    {
        var handler = WorkingHandler();
        var provider = new OpenMeteoWeatherProvider(new HttpClient(handler));

        await provider.CurrentAsync();
        var before = handler.Requests.Count;

        provider.Forget();
        await provider.CurrentAsync();

        Assert.True(handler.Requests.Count > before);
    }

    [Fact]
    public async Task Location_is_saved_and_restored_between_launches()
    {
        var path = Path.Combine(Path.GetTempPath(), "lively-loc-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var location = new LocationInfo
            {
                Latitude = 51.5074,
                Longitude = -0.1278,
                City = "London",
                Country = "United Kingdom",
                TimeZone = TimeZoneInfo.Utc,
                ResolvedAt = DateTimeOffset.UtcNow,
            };

            await LocationStore.SaveAsync(path, location);
            var loaded = await LocationStore.LoadAsync(path);

            Assert.NotNull(loaded);
            Assert.Equal(51.5074, loaded!.Latitude, 4);
            Assert.Equal("London", loaded.City);

            // A saved location means the network is not asked where we are.
            var handler = new RoutingHandler(url =>
                url.Contains("open-meteo.com", StringComparison.Ordinal)
                    ? RoutingHandler.Json(ForecastBody)
                    : throw new InvalidOperationException("should not look up the IP again"));

            var provider = new OpenMeteoWeatherProvider(new HttpClient(handler), locationCachePath: path);
            var snapshot = await provider.CurrentAsync();

            Assert.NotNull(snapshot);
            Assert.DoesNotContain(handler.Requests, u => u.Contains("ipapi", StringComparison.Ordinal));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_corrupt_location_file_is_ignored_rather_than_crashing()
    {
        var path = Path.Combine(Path.GetTempPath(), "lively-loc-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            await File.WriteAllTextAsync(path, "this is not json");
            Assert.Null(await LocationStore.LoadAsync(path));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }
}

public class AtmosphereServiceTests
{
    private sealed class StubWeather : IWeatherProvider
    {
        private readonly WeatherSnapshot? _snapshot;

        public StubWeather(WeatherSnapshot? snapshot, TimeSpan? maxAge = null)
        {
            _snapshot = snapshot;
            MaxAge = maxAge ?? TimeSpan.FromMinutes(30);
        }

        public TimeSpan MaxAge { get; }
        public Task<WeatherSnapshot?> CurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);
        public void Forget() { }
    }

    private static readonly LocationInfo London = new()
    {
        Latitude = 51.5074,
        Longitude = -0.1278,
        City = "London",
        TimeZone = TimeZoneInfo.Utc,
        ResolvedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Midsummer_noon_in_London_is_not_night()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        var weather = new StubWeather(new WeatherSnapshot
        {
            FetchedAt = clock.GetUtcNow(),
            Kind = WeatherKind.Clear,
            Location = London,
        });

        var service = new AtmosphereService(weather, clock);
        var snapshot = await service.RefreshAsync();

        Assert.True(snapshot.UsedSolar);
        Assert.True(snapshot.Phase is DayPhase.Morning or DayPhase.Afternoon);
        Assert.False(snapshot.Palette.IsDark);
    }

    [Fact]
    public async Task Midnight_in_London_is_night()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 12, 21, 0, 30, 0, TimeSpan.Zero));
        var weather = new StubWeather(new WeatherSnapshot
        {
            FetchedAt = clock.GetUtcNow(),
            Kind = WeatherKind.Clear,
            Location = London,
        });

        var service = new AtmosphereService(weather, clock);
        var snapshot = await service.RefreshAsync();

        Assert.Equal(DayPhase.Night, snapshot.Phase);
        Assert.True(snapshot.Palette.IsDark);
    }

    [Fact]
    public async Task Without_a_location_the_clock_is_used_and_the_app_still_works()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        var service = new AtmosphereService(new StubWeather(null), clock);

        var snapshot = await service.RefreshAsync();

        Assert.False(snapshot.UsedSolar);
        Assert.Equal(DayPhaseCalculator.FromWallClock(clock.GetLocalNow()), snapshot.Phase);
        Assert.NotNull(snapshot.Palette);
    }

    [Fact]
    public async Task A_broken_weather_provider_does_not_break_the_snapshot()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));

        var service = new AtmosphereService(new ThrowingWeather(), clock);
        var snapshot = await service.RefreshAsync();

        Assert.NotNull(snapshot.Palette);
        Assert.Null(snapshot.Weather);
    }

    [Fact]
    public async Task The_wallpaper_colour_is_applied_to_the_palette()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        var service = new AtmosphereService(new StubWeather(null), clock);

        var plain = await service.RefreshAsync();
        var matched = await service.RefreshAsync(0xFFC81E1E);

        Assert.NotEqual(plain.Palette.Accent, matched.Palette.Accent);
    }

    [Fact]
    public async Task The_next_refresh_is_soon_and_in_the_future()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        var service = new AtmosphereService(new StubWeather(null), clock);

        var snapshot = await service.RefreshAsync();
        var next = service.NextRefreshAfter(snapshot);

        Assert.True(next > DateTimeOffset.UtcNow);
        Assert.True(next <= DateTimeOffset.UtcNow.AddMinutes(16));
    }

    [Fact]
    public async Task Search_hints_follow_the_sky()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        var rain = new StubWeather(new WeatherSnapshot
        {
            FetchedAt = clock.GetUtcNow(),
            Kind = WeatherKind.Rain,
            Location = London,
        });

        var service = new AtmosphereService(rain, clock);
        await service.RefreshAsync();

        Assert.Equal("rain", service.SuggestSearchHint());
    }

    private sealed class ThrowingWeather : IWeatherProvider
    {
        public TimeSpan MaxAge => TimeSpan.FromMinutes(30);
        public Task<WeatherSnapshot?> CurrentAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("offline");
        public void Forget() { }
    }
}
