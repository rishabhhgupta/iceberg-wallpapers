namespace Lively.Discovery.Solar;

/// <summary>Sunrise and sunset for one local calendar day.</summary>
public readonly record struct SunTimes(DateTimeOffset Sunrise, DateTimeOffset Sunset);

/// <summary>
/// Sunrise/sunset from the standard Meeus low-precision solar model.
///
/// Ported from Nagomi's <c>auto-atmosphere.ts</c>, which is already tested
/// against api.sunrise-sunset.org across a timezone matrix. Keeping the same
/// maths here means the wallpaper app and the koi pond agree about when it is
/// dark outside.
///
/// Everything is solved in absolute UTC and then anchored to the caller's local
/// calendar day, which is what makes it correct across DST changes and across
/// the international date line.
/// </summary>
public static class SolarCalculator
{
    private const double MsPerDay = 86_400_000d;
    private const double MsPerMinute = 60_000d;

    /// <summary>Solar altitude of the upper limb at rise/set, including refraction.</summary>
    private const double SolarEventAltitudeDeg = -0.833;

    private const double SolsticeSin = -0.014538080344184856; // sin(-0.833 deg)

    /// <summary>Julian Date of the Unix epoch, 1970-01-01T00:00:00Z.</summary>
    private const double JdUnixEpoch = 2_440_587.5;

    /// <summary>Julian Date of J2000.0, 2000-01-01T12:00:00Z.</summary>
    private const double JdJ2000 = 2_451_545.0;

    private const double JulianCenturyDays = 36_525d;

    private static double ToRad(double degrees) => degrees * Math.PI / 180d;
    private static double ToDeg(double radians) => radians * 180d / Math.PI;

    private static double Wrap360(double d)
    {
        var r = d % 360d;
        return r < 0 ? r + 360d : r;
    }

    private static double Wrap180(double d)
    {
        var r = (d + 180d) % 360d;
        if (r < 0) r += 360d;
        return r - 180d;
    }

    private readonly record struct SolarGeometry(double Declination, double EquationOfTimeMin);

    /// <summary>
    /// Apparent solar declination and the equation of time for an absolute
    /// instant. A pure function of UTC, so it never consults the machine clock.
    /// </summary>
    private static SolarGeometry Geometry(double instantMs)
    {
        var julianDay = instantMs / MsPerDay + JdUnixEpoch;
        var t = (julianDay - JdJ2000) / JulianCenturyDays;

        var meanLongitude = Wrap360(280.46646 + 36000.76983 * t + 0.0003032 * t * t);
        var meanAnomaly = Wrap360(357.52911 + 35999.05029 * t - 0.0001537 * t * t);

        var center =
            (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(ToRad(meanAnomaly)) +
            (0.019993 - 0.000101 * t) * Math.Sin(ToRad(2 * meanAnomaly)) +
            0.000289 * Math.Sin(ToRad(3 * meanAnomaly));

        var trueLongitude = meanLongitude + center;

        var omega = 125.04 - 1934.136 * t;
        var nutationInLongitude = -0.00478 * Math.Sin(ToRad(omega));
        var apparentLongitude = trueLongitude - 0.00569 + nutationInLongitude;

        var meanObliquityDeg =
            23d + (26d + (21.448 - 46.815 * t - 0.00059 * t * t + 0.001813 * t * t * t) / 60d) / 60d;
        var obliquityDeg = meanObliquityDeg + 0.00256 * Math.Cos(ToRad(omega));
        var obliquityRad = ToRad(obliquityDeg);

        var declination = ToDeg(Math.Asin(Math.Sin(obliquityRad) * Math.Sin(ToRad(apparentLongitude))));

        var apparentRightAscension = Wrap360(
            ToDeg(Math.Atan2(
                Math.Sin(ToRad(apparentLongitude)) * Math.Cos(obliquityRad),
                Math.Cos(ToRad(apparentLongitude)))));

        // Must use the *mean* longitude here; using the apparent longitude drops
        // the equation of centre and shifts every transit by minutes.
        var equationOfTimeMin =
            4d * Wrap180(meanLongitude - 0.0057183 - apparentRightAscension
                         + nutationInLongitude * Math.Cos(obliquityRad));

        return new SolarGeometry(declination, equationOfTimeMin);
    }

    private readonly record struct DayEvents(double SunriseMs, double SunsetMs);

    /// <summary>Sunrise/sunset for the UTC day beginning at <paramref name="utcDayStartMs"/>.</summary>
    private static DayEvents? EventsForUtcDay(double utcDayStartMs, double latitude, double longitude)
    {
        var (declination, equationOfTimeMin) = Geometry(utcDayStartMs);

        var latRad = ToRad(latitude);
        var decRad = ToRad(declination);
        var denominator = Math.Cos(latRad) * Math.Cos(decRad);
        if (Math.Abs(denominator) < double.Epsilon) return null;

        var cosHourAngle = (SolsticeSin - Math.Sin(latRad) * Math.Sin(decRad)) / denominator;

        // Outside [-1, 1] the sun never reaches -0.833 deg: polar day or polar night.
        if (double.IsNaN(cosHourAngle) || double.IsInfinity(cosHourAngle)) return null;
        if (cosHourAngle > 1d || cosHourAngle < -1d) return null;

        var hourAngleDeg = ToDeg(Math.Acos(Math.Clamp(cosHourAngle, -1d, 1d)));

        // Solar transit, in minutes after the start of the UTC day.
        var transitMin = 720d - 4d * longitude - equationOfTimeMin;

        return new DayEvents(
            utcDayStartMs + (transitMin - 4d * hourAngleDeg) * MsPerMinute,
            utcDayStartMs + (transitMin + 4d * hourAngleDeg) * MsPerMinute);
    }

    /// <summary>
    /// Sunrise and sunset for the local calendar day containing
    /// <paramref name="instant"/>, in <paramref name="timeZone"/>.
    /// Returns null inside the polar day/night bands, or for invalid input.
    /// </summary>
    public static SunTimes? Get(DateTimeOffset instant, double latitude, double longitude, TimeZoneInfo timeZone)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude)) return null;
        if (latitude < -90d || latitude > 90d) return null;
        if (longitude < -180d || longitude > 180d) return null;
        ArgumentNullException.ThrowIfNull(timeZone);

        var local = TimeZoneInfo.ConvertTime(instant, timeZone);

        // Local calendar noon, expressed as a UTC instant. Noon is never
        // ambiguous or skipped by a DST transition, so this is always safe.
        var localNoon = new DateTime(local.Year, local.Month, local.Day, 12, 0, 0, DateTimeKind.Unspecified);
        var localNoonUtcMs = new DateTimeOffset(localNoon, timeZone.GetUtcOffset(localNoon))
            .ToUniversalTime()
            .ToUnixTimeMilliseconds();

        var anchorDayStart = Math.Floor(localNoonUtcMs / MsPerDay) * MsPerDay;

        // The pair for a local date usually lands in the adjacent UTC day, which
        // the signed transit/hour-angle maths already handles. At extreme
        // latitudes the pair can straddle, so nudge until it is centred on local
        // noon. Bounded, so a degenerate case can never spin.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var events = EventsForUtcDay(anchorDayStart, latitude, longitude);
            if (events is null) return null;

            var offsetFromNoon = events.Value.SunriseMs - localNoonUtcMs;
            if (offsetFromNoon > 12d * 3_600_000d)
            {
                anchorDayStart -= MsPerDay;
                continue;
            }

            if (offsetFromNoon < -12d * 3_600_000d)
            {
                anchorDayStart += MsPerDay;
                continue;
            }

            var sunrise = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(events.Value.SunriseMs));
            var sunset = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(events.Value.SunsetMs));
            return new SunTimes(sunrise, sunset);
        }

        return null;
    }
}
