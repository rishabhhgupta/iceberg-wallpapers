using System.Globalization;
using System.Text.Json;

namespace Lively.Discovery.Weather;

/// <summary>Where the person is, and which clock they keep.</summary>
public sealed record LocationInfo
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public DateTimeOffset ResolvedAt { get; init; }
}

/// <summary>One reading of the sky.</summary>
public sealed record WeatherSnapshot
{
    public required DateTimeOffset FetchedAt { get; init; }
    public double? TemperatureC { get; init; }
    public int? Code { get; init; }
    public WeatherKind? Kind { get; init; }
    public bool? IsDay { get; init; }
    public string? City { get; init; }
    public LocationInfo? Location { get; init; }

    /// <summary>True when this is a previous reading served because the network failed.</summary>
    public bool IsStale { get; init; }
}

public interface IWeatherProvider
{
    /// <summary>How long a reading is considered fresh.</summary>
    TimeSpan MaxAge { get; }

    Task<WeatherSnapshot?> CurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops every cache, so the next call re-locates and re-fetches.</summary>
    void Forget();
}

/// <summary>
/// Weather from Open-Meteo, which needs no API key and is free for this volume,
/// plus a coarse location from the caller's IP address.
///
/// Three rules make this safe to put on a timer:
///   1. A reading younger than <c>maxAge</c> is reused, so the app does not hit
///      the network every minute.
///   2. If a refresh fails but an older reading exists, the older one is
///      returned marked stale. The sky does not suddenly become unknown.
///   3. If nothing has ever worked, the result is null and the caller falls back
///      to the clock. Weather is a bonus, never a requirement.
/// </summary>
public sealed class OpenMeteoWeatherProvider : IWeatherProvider
{
    private const string IpApiEndpoint = "https://ipapi.co/json/";
    private const string IpWhoEndpoint = "https://ipwho.is/";
    private const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";

    private readonly HttpClient _http;
    private readonly TimeSpan _maxAge;
    private readonly string? _locationCachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WeatherSnapshot? _last;
    private LocationInfo? _location;

    public OpenMeteoWeatherProvider(
        HttpClient httpClient,
        TimeSpan? maxAge = null,
        string? locationCachePath = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _maxAge = maxAge ?? TimeSpan.FromMinutes(30);
        _locationCachePath = locationCachePath;
    }

    public TimeSpan MaxAge => _maxAge;

    public async Task<WeatherSnapshot?> CurrentAsync(CancellationToken cancellationToken = default)
    {
        // Serialised: without this, a dashboard and a rotation tick firing at the
        // same moment would both refresh and both write the cache.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;

            if (_last is not null && now - _last.FetchedAt < _maxAge)
            {
                return _last;
            }

            try
            {
                var location = await EnsureLocationAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = await FetchAsync(location, now, cancellationToken).ConfigureAwait(false);

                _last = snapshot;
                return snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Stale weather beats no weather, but a lie about the time it was
                // taken would be worse, so the flag travels with it.
                return _last is null ? null : _last with { IsStale = true };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Forget()
    {
        _last = null;
        _location = null;
    }

    private async Task<LocationInfo> EnsureLocationAsync(CancellationToken cancellationToken)
    {
        if (_location is not null) return _location;

        if (!string.IsNullOrWhiteSpace(_locationCachePath))
        {
            var cached = await LocationStore.LoadAsync(_locationCachePath!, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _location = cached;
                return cached;
            }
        }

        var location = await LocateAsync(cancellationToken).ConfigureAwait(false);
        _location = location;

        if (!string.IsNullOrWhiteSpace(_locationCachePath))
        {
            try { await LocationStore.SaveAsync(_locationCachePath!, location, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { }
        }

        return location;
    }

    private async Task<LocationInfo> LocateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await GetJsonAsync(IpApiEndpoint, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            var lat = Number(root, "latitude");
            var lon = Number(root, "longitude");

            if (lat is not null && lon is not null)
            {
                return new LocationInfo
                {
                    Latitude = lat.Value,
                    Longitude = lon.Value,
                    City = Text(root, "city"),
                    Country = Text(root, "country_name"),
                    TimeZone = ResolveTimeZone(Text(root, "timezone")),
                    ResolvedAt = DateTimeOffset.UtcNow,
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to the second provider.
        }

        using var fallback = await GetJsonAsync(IpWhoEndpoint, cancellationToken).ConfigureAwait(false);
        var fallbackRoot = fallback.RootElement;

        var flat = Number(fallbackRoot, "latitude");
        var flon = Number(fallbackRoot, "longitude");

        if (flat is null || flon is null)
        {
            throw new InvalidOperationException("Could not work out the location from the IP address.");
        }

        string? zoneId = null;
        if (fallbackRoot.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.Object)
        {
            zoneId = Text(tz, "id");
        }

        return new LocationInfo
        {
            Latitude = flat.Value,
            Longitude = flon.Value,
            City = Text(fallbackRoot, "city"),
            Country = Text(fallbackRoot, "country"),
            TimeZone = ResolveTimeZone(zoneId),
            ResolvedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task<WeatherSnapshot> FetchAsync(LocationInfo location, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var url = FormattableString.Invariant(
            $"{ForecastEndpoint}?latitude={location.Latitude:0.####}&longitude={location.Longitude:0.####}&current=temperature_2m,weather_code,is_day&timezone=auto");

        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        int? code = null;
        double? temperature = null;
        bool? isDay = null;

        if (root.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.Object)
        {
            code = Integer(current, "weather_code") ?? Integer(current, "weathercode");
            temperature = Number(current, "temperature_2m");
            isDay = Integer(current, "is_day") is { } d ? d == 1 : null;
        }

        return new WeatherSnapshot
        {
            FetchedAt = now,
            Code = code,
            Kind = WeatherCode.ToKind(code),
            TemperatureC = temperature,
            IsDay = isDay,
            City = location.City,
            Location = location,
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "IcebergWallpaper/1.0");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// IANA ids ("Asia/Kolkata") need ICU. If the machine cannot resolve one,
    /// the local zone is a better guess than UTC: it keeps sunrise roughly right.
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id!); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Local;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int? Integer(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var i)
            ? i
            : null;
}

/// <summary>
/// Remembers the location between launches as a small JSON file. Without it,
/// every cold start would ask an IP service where the user is, which is slow and
/// rude. Thirty days is long enough to save calls and short enough to notice a
/// move.
/// </summary>
public static class LocationStore
{
    private sealed record Dto(
        double Latitude,
        double Longitude,
        string? City,
        string? Country,
        string? TimeZoneId,
        DateTimeOffset ResolvedAt);

    public static TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);

    public static async Task<LocationInfo?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            var dto = await JsonSerializer.DeserializeAsync<Dto>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (dto is null) return null;
            if (DateTimeOffset.UtcNow - dto.ResolvedAt > MaxAge) return null;

            TimeZoneInfo zone;
            try
            {
                zone = string.IsNullOrWhiteSpace(dto.TimeZoneId)
                    ? TimeZoneInfo.Local
                    : TimeZoneInfo.FindSystemTimeZoneById(dto.TimeZoneId!);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                zone = TimeZoneInfo.Local;
            }

            return new LocationInfo
            {
                Latitude = dto.Latitude,
                Longitude = dto.Longitude,
                City = dto.City,
                Country = dto.Country,
                TimeZone = zone,
                ResolvedAt = dto.ResolvedAt,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task SaveAsync(string path, LocationInfo location, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(path)) return;

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

        var dto = new Dto(
            location.Latitude,
            location.Longitude,
            location.City,
            location.Country,
            location.TimeZone.Id,
            location.ResolvedAt);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, dto, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
