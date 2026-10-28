using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lively.Discovery.Favourites;

/// <summary>
/// One starred wallpaper. Stored in a plain JSON file so it can be read, backed
/// up, diffed and repaired by hand. Deliberately not a database: favourites are
/// a small list, and a corrupt SQLite file is unrecoverable while a corrupt JSON
/// file usually is not.
/// </summary>
public sealed record FavouriteItem
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public string? Title { get; init; }
    public string? PageUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? FullUrl { get; init; }
    public string? LocalPath { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

public sealed record FavouritesDocument
{
    public int Version { get; init; } = 1;
    public List<FavouriteItem> Items { get; init; } = new();
}

/// <summary>
/// Thread-safe favourites file with atomic saves.
///
/// Atomic means: write a temporary file, then swap it in. A power cut in the
/// middle of a save can therefore never leave a half-written favourites file.
/// </summary>
public sealed class FavouriteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<FavouriteItem> _items = new();
    private bool _loaded;

    public FavouriteStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _items.Clear();
            if (File.Exists(_path))
            {
                try
                {
                    await using var stream = File.OpenRead(_path);
                    var doc = await JsonSerializer
                        .DeserializeAsync<FavouritesDocument>(stream, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);

                    if (doc?.Items is { } items)
                    {
                        // Drop malformed entries rather than fail the whole load.
                        _items.AddRange(items.Where(i => !string.IsNullOrWhiteSpace(i.Id)));
                    }
                }
                catch (JsonException)
                {
                    QuarantineCorruptFile();
                }
                catch (IOException)
                {
                    // File locked or unreadable: start empty, keep the file for later.
                }
            }

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<FavouriteItem> Items
    {
        get
        {
            lock (_items) return _items.ToArray();
        }
    }

    public bool Contains(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_items) return _items.Any(i => string.Equals(i.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Adds if absent. Returns true when the list actually changed.</summary>
    public async Task<bool> AddAsync(FavouriteItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_items.Any(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal))) return false;

            _items.Insert(0, item with { AddedAt = item.AddedAt == default ? DateTimeOffset.UtcNow : item.AddedAt });
            await SaveUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes by id. Returns true when something was removed.</summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _items.RemoveAll(i => string.Equals(i.Id, id, StringComparison.Ordinal)) > 0;
            if (removed) await SaveUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveUnsafeAsync(CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = _path + ".tmp";

        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer
                .SerializeAsync(stream, new FavouritesDocument { Items = _items.ToList() }, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // File.Move with overwrite is atomic on NTFS for same-volume moves.
        File.Move(temp, _path, overwrite: true);
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            File.Move(_path, $"{_path}.corrupt-{stamp}", overwrite: true);
        }
        catch (IOException)
        {
            // Best effort only. Losing the quarantine copy must not lose the app.
        }
    }
}
