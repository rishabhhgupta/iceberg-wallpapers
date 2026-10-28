using System.Text.Json;

namespace Lively.Discovery.Settings;

/// <summary>
/// Everything the person can change about Iceberg, in one flat file.
///
/// Deliberately a record: every change produces a new value, so nothing can
/// mutate settings behind the UI's back and leave two screens disagreeing.
/// </summary>
public sealed record IcebergSettings
{
    public int Version { get; init; } = 1;

    /// <summary>Empty means "whatever works out of the box".</summary>
    public List<string> EnabledSources { get; init; } = new();

    public bool RotationEnabled { get; init; } = true;

    /// <summary>"timeofday" (day/night) or "timer". One trigger, not two.</summary>
    public string RotationMode { get; init; } = "timeofday";

    /// <summary>When <see cref="RotationMode"/> is timer: "daily", "every24" or "custom".</summary>
    public string TimerMode { get; init; } = "daily";

    public int CustomHours { get; init; } = 6;

    public int CustomMinutes { get; init; }

    /// <summary>Videos are large, so they only join the rotation when asked for.</summary>
    public bool AllowVideoInRotation { get; init; }

    /// <summary>"any", "photo", "illustration", "video" or "gif". Chosen in Filters.</summary>
    public string MediaType { get; init; } = "any";

    /// <summary>Per-source filter choices, keyed by source id then filter id.</summary>
    public Dictionary<string, Dictionary<string, string>> SourceFilters { get; init; } = new();

    /// <summary>"featured", "top", "newest" or "random". Chosen on Home.</summary>
    public string Sort { get; init; } = "featured";

    /// <summary>"landscape", "portrait" or "any". Chosen on Home.</summary>
    public string Orientation { get; init; } = "landscape";

    /// <summary>Also put each rotated wallpaper on the Windows lock screen.</summary>
    public bool RotateLockScreenToo { get; init; }

    /// <summary>The last lock screen picture Iceberg copied for Windows.</summary>
    public string? LockScreenPath { get; init; }

    public string? LockScreenKey { get; init; }

    public string? LockScreenTitle { get; init; }

    public int CacheMegabytes { get; init; } = 512;

    /// <summary>Videos are bigger than pictures, so the ceiling is generous.</summary>
    public long MaxImageMegabytes { get; init; } = 150;

    /// <summary>So a restart does not immediately repeat the same wallpaper.</summary>
    public string? LastAppliedKey { get; init; }

    /// <summary>When rotation last changed the wallpaper, so the interval survives a restart.</summary>
    public DateTimeOffset? LastRotationAt { get; init; }

    /// <summary>Newest first, capped. Mirrors the rotation planner's memory.</summary>
    public List<string> RecentKeys { get; init; } = new();

    public long CacheBytes => Math.Clamp(CacheMegabytes, 64, 8192) * 1024L * 1024L;

    public long MaxImageBytes => Math.Clamp(MaxImageMegabytes, 2, 400) * 1024L * 1024L;

    /// <summary>The gap between timer changes, when the timer is set to "custom".</summary>
    public TimeSpan CustomInterval =>
        TimeSpan.FromMinutes(Math.Clamp(CustomHours, 0, 168) * 60 + Math.Clamp(CustomMinutes, 0, 59));

    /// <summary>Clamps hand-edited or older files into values the app can survive.</summary>
    public IcebergSettings Normalised() => this with
    {
        CacheMegabytes = Math.Clamp(CacheMegabytes, 64, 8192),
        MaxImageMegabytes = Math.Clamp(MaxImageMegabytes, 2, 400),
        CustomHours = Math.Clamp(CustomHours, 0, 168),
        CustomMinutes = Math.Clamp(CustomMinutes, 0, 59),
        RotationMode = OneOf(RotationMode, "timeofday", "timeofday", "timer"),
        TimerMode = OneOf(TimerMode, "daily", "daily", "every24", "custom"),
        MediaType = OneOf(MediaType, "any", "any", "photo", "illustration", "video", "gif"),
        Sort = OneOf(Sort, "featured", "featured", "top", "newest", "random"),
        Orientation = OneOf(Orientation, "landscape", "landscape", "portrait", "any"),
        EnabledSources = (EnabledSources ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
        RecentKeys = (RecentKeys ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(50).ToList(),
        SourceFilters = (SourceFilters ?? new())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Where(choice => !string.IsNullOrWhiteSpace(choice.Key) && !string.IsNullOrWhiteSpace(choice.Value))
                    .ToDictionary(choice => choice.Key, choice => choice.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
    };

    private static string OneOf(string? value, string fallback, params string[] allowed)
    {
        var lower = (value ?? string.Empty).Trim().ToLowerInvariant();
        return allowed.Contains(lower) ? lower : fallback;
    }
}

/// <summary>
/// Loads and saves <see cref="IcebergSettings"/> as one small JSON file.
///
/// Writes are atomic (temp file then move) so a crash mid-save leaves the old
/// settings intact rather than a truncated file that resets everything.
/// A corrupt file is treated as "no settings yet", not as a reason to crash.
/// </summary>
public sealed class IcebergSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IcebergSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    public IcebergSettings Current { get; private set; } = new();

    public async Task<IcebergSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                Current = new IcebergSettings();
                return Current;
            }

            try
            {
                await using var stream = File.OpenRead(_path);
                var loaded = await JsonSerializer
                    .DeserializeAsync<IcebergSettings>(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                Current = (loaded ?? new IcebergSettings()).Normalised();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Current = new IcebergSettings();
            }

            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IcebergSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current = settings.Normalised();

            var folder = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, Current, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Read-modify-write, so two callers cannot overwrite each other.</summary>
    public async Task<IcebergSettings> UpdateAsync(
        Func<IcebergSettings, IcebergSettings> change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        var next = change(Current);
        await SaveAsync(next, cancellationToken).ConfigureAwait(false);
        return Current;
    }
}
