using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lively.Common;
using Lively.Common.Helpers.Storage;
using Lively.Common.Services;
using Lively.Discovery;
using Lively.Discovery.Favourites;
using Lively.Discovery.Rotation;
using Lively.Discovery.Settings;
using Lively.Grpc.Client;
using Lively.Imaging.Caching;
using Lively.Imaging.Loading;
using Lively.Imaging.Processing;
using Lively.Models;
using Lively.Models.Enums;
using Lively.Sources;

namespace Lively.UI.WinUI.Services
{
    /// <summary>
    /// The one place that knows how Iceberg's pieces fit together: settings on
    /// disk, the feed, the image cache, favourites, the atmosphere, and how to
    /// actually put a picture on the desktop.
    ///
    /// Screens talk to this instead of talking to each other, so there is exactly
    /// one image cache and one list of favourites in the process.
    /// </summary>
    public sealed class IcebergService
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly IDesktopCoreClient desktopCore;
        private readonly IUserSettingsClient userSettings;
        private readonly IDisplayManagerClient displayManager;

        private readonly HttpClient apiHttp;
        private readonly HttpClient imageHttp;
        private readonly Dictionary<string, string> imported = new(StringComparer.Ordinal);

        private CancellationTokenSource rotationCts;
        private DateTimeOffset lastAtmosphereRefresh = DateTimeOffset.MinValue;
        private WallpaperItem? preloadedItem;
        private byte[]? preloadedBytes;
        private readonly SemaphoreSlim preloadLock = new(1, 1);

        public IcebergService(
            IDesktopCoreClient desktopCore,
            IUserSettingsClient userSettings,
            IDisplayManagerClient displayManager)
        {
            this.desktopCore = desktopCore ?? throw new ArgumentNullException(nameof(desktopCore));
            this.userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
            this.displayManager = displayManager ?? throw new ArgumentNullException(nameof(displayManager));

            apiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            apiHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "IcebergWallpaper/1.0 (+https://icebergwallpaper.app)");

            // Images are much bigger than API replies and are streamed to disk.
            imageHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            // Some picture sites refuse requests that do not say who is asking.
            imageHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "IcebergWallpaper/1.0 (+https://icebergwallpaper.app)");

            var icebergSettingsFile = Path.Combine(DataDirectory, "IcebergSettings.json");
            var oldSettingsFile = Path.Combine(DataDirectory, "settings.json");
            if (!File.Exists(icebergSettingsFile) && File.Exists(oldSettingsFile))
            {
                try { File.Copy(oldSettingsFile, icebergSettingsFile); } catch { }
            }
            Settings = new IcebergSettingsStore(icebergSettingsFile);
            Favourites = new FavouriteStore(Path.Combine(DataDirectory, "favourites.json"));
        }

        public static string DataDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Iceberg Wallpaper");

        public static string LibraryDirectory => Constants.CommonPaths.IcebergLibraryDir;

        public IcebergSettingsStore Settings { get; }

        public FavouriteStore Favourites { get; }

        public SourceRegistry Registry { get; private set; }

        public FeedService Feed { get; private set; }

        public ImageLoader Images { get; private set; }

        public AtmosphereService Atmosphere { get; private set; }

        public RotationPlanner Planner { get; } = new(recentWindow: 30);

        public IcebergSettings Current => Settings.Current;

        /// <summary>Raised when the phase of the day changes.</summary>
        public event EventHandler<AtmosphereSnapshot> AtmosphereChanged;

        /// <summary>The last picture put on the desktop in this session, for "use as lock screen".</summary>
        public WallpaperItem? LastAppliedItem { get; private set; }

        public bool IsInitialised { get; private set; }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (IsInitialised) return;

            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LibraryDirectory);

            await Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            await Favourites.LoadAsync(cancellationToken).ConfigureAwait(false);
            await LoadImportedMapAsync(cancellationToken).ConfigureAwait(false);

            BuildPipeline();
            Planner.Restore(Settings.Current.RecentKeys);

            IsInitialised = true;

            // Rotation lives in the UI process, which stays alive in the tray
            // while Lively is running, so it keeps working with the window closed.
            StartRotation();

            // The phase of the day comes from the clock alone: no weather call
            // and nothing to wait for at start-up.
            _ = RefreshAtmosphereAsync();
        }

        public void BuildPipeline()
        {
            var settings = Settings.Current;

            Registry = new SourceRegistry(
                apiHttp,
                SourceKeys.Defaults,
                settings.EnabledSources.Count == 0 ? null : settings.EnabledSources);

            Feed = new FeedService(Registry);

            var disk = new DiskImageCache(Path.Combine(DataDirectory, "images"), settings.CacheBytes);
            Images = new ImageLoader(
                imageHttp,
                new MemoryImageCache(Math.Clamp(settings.CacheBytes, 64L * 1024 * 1024, 256L * 1024 * 1024)),
                disk,
                settings.MaxImageBytes);

            // No weather lookup and no location: the phase of the day comes from
            // the clock alone, so this never touches the network.
            Atmosphere = new AtmosphereService(null!);
        }

        /// <summary>
        /// Recomputes the phase of the day from the clock. Deliberately offline:
        /// Iceberg does not look up the weather, so there is nothing to wait for.
        /// </summary>
        public Task<AtmosphereSnapshot> RefreshAtmosphereAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = Atmosphere.Compute(null);
            AtmosphereChanged?.Invoke(this, snapshot);
            return Task.FromResult(snapshot);
        }

        public Task ApplyAsync(WallpaperItem item, CancellationToken cancellationToken = default) =>
            ApplyAsync(item, null, cancellationToken);

        public async Task ApplyAsync(WallpaperItem item, byte[]? preloadedBytes, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);

            var bytes = preloadedBytes ?? await Images.GetAsync(item.FullUrl, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                throw new InvalidOperationException("That picture could not be downloaded.");
            }

            var package = await EnsurePackageAsync(item, bytes, cancellationToken).ConfigureAwait(false);

            var monitor = userSettings.Settings.SelectedDisplay
                          ?? displayManager.DisplayMonitors.FirstOrDefault(x => x.IsPrimary)
                          ?? displayManager.DisplayMonitors.FirstOrDefault();

            if (monitor is null)
            {
                throw new InvalidOperationException("No display was found to put the wallpaper on.");
            }

            // Only the folder is read by the core, but the rest makes the item
            // valid if it is ever shown in a list.
            var model = new LibraryModel
            {
                LivelyInfoFolderPath = package.Folder,
                FilePath = package.File,
                Title = string.IsNullOrWhiteSpace(item.Title) ? "Iceberg wallpaper" : item.Title,
                Author = item.Author ?? item.SourceId,
            };

            Logger.Info($"Iceberg: asking the core to set '{model.LivelyInfoFolderPath}'");
            await desktopCore.SetWallpaper(model, monitor).ConfigureAwait(false);

            try
            {
                var poster = package.PosterPath;
                if (!string.IsNullOrWhiteSpace(poster) && File.Exists(poster))
                {
                    _ = SystemWallpaperService.SetAsync(poster);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to update system-wide desktop wallpaper: {ex.Message}");
            }

            LastAppliedItem = item;

            await Settings.UpdateAsync(s => s with
            {
                LastAppliedKey = item.Key,
                LastRotationAt = DateTimeOffset.UtcNow,
                RecentKeys = new[] { item.Key }.Concat(s.RecentKeys).Distinct().Take(50).ToList(),
            }, cancellationToken).ConfigureAwait(false);

            Planner.MarkUsed(item.Key);
            _ = PreloadNextAsync(CancellationToken.None);
        }

        /// <summary>
        /// Copies a picture to a fresh file and asks Windows to use it on the
        /// lock screen. A fresh name every time is required by Windows: setting
        /// a new image over one with the same name silently fails.
        /// </summary>
        public async Task SetLockScreenAsync(WallpaperItem item, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);

            var bytes = await Images.GetAsync(item.FullUrl, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                throw new InvalidOperationException("That picture could not be downloaded.");
            }

            var sourceName = Registry.Get(item.SourceId)?.DisplayName ?? item.SourceId;
            var title = string.IsNullOrWhiteSpace(item.Title) ? sourceName : item.Title;

            await SetLockScreenFromBytesAsync(bytes, item.Key, title, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reuses the last desktop wallpaper after a restart, when the in-memory
        /// item is gone. The package on disk is enough; the picture does not need
        /// to be downloaded again.
        /// </summary>
        public async Task SetLockScreenFromLastAsync(CancellationToken cancellationToken = default)
        {
            if (LastAppliedItem is { } item)
            {
                await SetLockScreenAsync(item, cancellationToken).ConfigureAwait(false);
                return;
            }

            var key = Settings.Current.LastAppliedKey;
            if (string.IsNullOrWhiteSpace(key)
                || !imported.TryGetValue(key, out var folder)
                || !Directory.Exists(folder))
            {
                throw new InvalidOperationException("Pick a wallpaper on Home first, then try again.");
            }

            var imagePath = Directory.EnumerateFiles(folder, "wallpaper.*").FirstOrDefault();
            if (imagePath is null || !File.Exists(imagePath))
            {
                throw new InvalidOperationException("The last wallpaper file is no longer on disk. Pick one on Home again.");
            }

            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            var sourceId = key.Split(':')[0];
            var sourceName = Registry.Get(sourceId)?.DisplayName ?? sourceId;

            await SetLockScreenFromBytesAsync(bytes, key, sourceName, cancellationToken).ConfigureAwait(false);
        }

        private async Task SetLockScreenFromBytesAsync(
            byte[] bytes,
            string key,
            string title,
            CancellationToken cancellationToken)
        {
            var folder = Path.Combine(DataDirectory, "lockscreen");
            Directory.CreateDirectory(folder);

            var extension = ExtensionFor(bytes);
            var name = $"lock-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Hash(key)[..8]}{extension}";
            var path = Path.Combine(folder, name);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

            var changed = await LockScreenService.SetAsync(path).ConfigureAwait(false);
            if (!changed)
            {
                try { File.Delete(path); } catch (IOException) { }

                throw new InvalidOperationException(
                    "Windows would not let Iceberg change the lock screen. " +
                    "Check Settings > Personalisation > Lock screen, or ask your organisation if it manages this PC.");
            }

            await Settings.UpdateAsync(s => s with
            {
                LockScreenPath = path,
                LockScreenKey = key,
                LockScreenTitle = title,
            }, cancellationToken).ConfigureAwait(false);

            CleanupLockScreens(folder, keep: 3);
        }

        private static void CleanupLockScreens(string folder, int keep)
        {
            try
            {
                foreach (var old in Directory.EnumerateFiles(folder, "lock-*")
                             .Select(path => new FileInfo(path))
                             .OrderByDescending(info => info.CreationTimeUtc)
                             .Skip(keep))
                {
                    try { old.Delete(); } catch (IOException) { }
                }
            }
            catch (IOException)
            {
                // Housekeeping only; never a reason to fail the lock screen.
            }
        }

        private static WallpaperSort ParseSort(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "top" => WallpaperSort.Top,
            "newest" => WallpaperSort.Newest,
            "random" => WallpaperSort.Random,
            _ => WallpaperSort.Featured,
        };

        private static WallpaperOrientation ParseOrientation(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "portrait" => WallpaperOrientation.Portrait,
            "any" => WallpaperOrientation.Any,
            _ => WallpaperOrientation.Landscape,
        };

        /// <summary>Picks the next wallpaper from the chosen rotation source.</summary>
        /// <summary>Picks the next wallpaper from the chosen rotation source.</summary>
        public async Task<WallpaperItem> ApplyNextAsync(CancellationToken cancellationToken = default)
        {
            var settings = Settings.Current;

            // Fast path: if a wallpaper was preloaded ahead of time, apply it instantly!
            if (preloadedItem is { } preloaded && preloadedBytes is { } pBytes && preloaded.Key != LastAppliedItem?.Key)
            {
                preloadedItem = null;
                preloadedBytes = null;
                try
                {
                    await ApplyAsync(preloaded, pBytes, cancellationToken).ConfigureAwait(false);
                    return preloaded;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to apply preloaded wallpaper: {ex.Message}, falling back to live pick.");
                }
            }

            if (settings.RotationSource == "favourites")
            {
                return await ApplyFavouriteAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            var ordered = await PickNextCandidatesAsync(settings, cancellationToken).ConfigureAwait(false);
            if (ordered.Count == 0)
            {
                throw new InvalidOperationException("No wallpapers came back. Check your connection.");
            }

            Exception? lastError = null;
            foreach (var item in ordered.Take(3))
            {
                try
                {
                    await ApplyAsync(item, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return item;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw lastError ?? new InvalidOperationException("No wallpapers came back. Check your connection.");
        }

        private async Task<List<WallpaperItem>> PickNextCandidatesAsync(
            IcebergSettings settings,
            CancellationToken cancellationToken)
        {
            var hint = settings.RotationMode == "timeofday" && Atmosphere.Current is { } a
                ? PhaseHint(a)
                : null;

            WallpaperMediaType? media = null;
            if (settings.AllowVideoInRotation && Random.Shared.NextDouble() < 0.2)
            {
                media = WallpaperMediaType.Video;
            }

            var query = new WallpaperQuery
            {
                Page = Random.Shared.Next(1, 4),
                PerPage = 24,
                Text = hint,
                SafeSearch = true,
                Sort = WallpaperSort.Random,
                Orientation = ParseOrientation(settings.Orientation),
                MediaType = media,
            };

            IWallpaperSource? onlySource = null;
            if (settings.RotationSource == "predefined")
            {
                onlySource = Registry.Get(settings.RotationSourceId ?? string.Empty)
                    ?? throw new InvalidOperationException("Choose a site for the rotation first.");

                query = query with
                {
                    SourceFilters = BuildRotationFilter(onlySource, settings.RotationCollection),
                    MediaType = media is WallpaperMediaType.Video
                               && !onlySource.MediaTypes.Contains(WallpaperMediaType.Video)
                        ? null
                        : media,
                };
            }

            var page = await FetchForRotationAsync(query, onlySource, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0 && hint is not null)
            {
                page = await FetchForRotationAsync(query with { Text = null, Page = 1 }, onlySource, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (page.Items.Count == 0) return new List<WallpaperItem>();

            var candidates = page.Items.Select(i => new WallpaperCandidate(i.Key, i.SourceId)).ToList();
            var chosen = Planner.Pick(candidates);

            var ordered = new List<WallpaperItem>();
            if (page.Items.FirstOrDefault(i => i.Key == chosen?.Id) is { } first)
            {
                ordered.Add(first);
            }

            ordered.AddRange(page.Items
                .Where(i => ordered.All(o => o.Key != i.Key))
                .OrderBy(_ => Random.Shared.Next()));

            return ordered;
        }

        public async Task PreloadNextAsync(CancellationToken cancellationToken = default)
        {
            var settings = Settings.Current;
            if (!settings.RotationEnabled) return;

            if (!await preloadLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (settings.RotationSource == "favourites")
                {
                    var favs = Favourites.Items.Where(f => !string.IsNullOrWhiteSpace(f.FullUrl)).ToList();
                    if (favs.Count > 0)
                    {
                        var candidates = favs.Select(f => new WallpaperCandidate(f.Id, f.SourceId)).ToList();
                        var picked = Planner.Pick(candidates);
                        var favItem = favs.FirstOrDefault(f => f.Id == picked?.Id) ?? favs[0];
                        if ($"{favItem.SourceId}:{favItem.Id}" != LastAppliedItem?.Key)
                        {
                            var full = new WallpaperItem
                            {
                                Id = favItem.Id,
                                SourceId = favItem.SourceId,
                                FullUrl = favItem.FullUrl,
                                ThumbnailUrl = favItem.ThumbnailUrl,
                                Title = favItem.Title,
                                Category = favItem.Category,
                                PageUrl = favItem.PageUrl,
                            };
                            var bytes = await Images.GetAsync(full.FullUrl, cancellationToken).ConfigureAwait(false);
                            if (bytes != null && bytes.Length > 0)
                            {
                                preloadedItem = full;
                                preloadedBytes = bytes;
                                Logger.Info($"Preloaded favourite for next rotation: {full.Key}");
                            }
                        }
                    }
                    return;
                }

                var ordered = await PickNextCandidatesAsync(settings, cancellationToken).ConfigureAwait(false);
                foreach (var item in ordered.Where(i => i.Key != LastAppliedItem?.Key).Take(2))
                {
                    var bytes = await Images.GetAsync(item.FullUrl, cancellationToken).ConfigureAwait(false);
                    if (bytes is not null && bytes.Length > 0)
                    {
                        preloadedItem = item;
                        preloadedBytes = bytes;
                        Logger.Info($"Preloaded next rotation wallpaper: {item.Key} ({bytes.Length} bytes)");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Preload next wallpaper failed: {ex.Message}");
            }
            finally
            {
                preloadLock.Release();
            }
        }

        /// <summary>
        /// Rotation from the starred list. Nothing is downloaded from a website:
        /// the favourites already carry their full image address.
        /// </summary>
        private async Task<WallpaperItem> ApplyFavouriteAsync(
            IcebergSettings settings,
            CancellationToken cancellationToken)
        {
            var favourites = Favourites.Items
                .Where(f => !string.IsNullOrWhiteSpace(f.FullUrl))
                .ToList();

            if (favourites.Count == 0)
            {
                throw new InvalidOperationException(
                    "No favourites yet. Star a few wallpapers on Home and the rotation will use them.");
            }

            Exception? lastError = null;

            foreach (var favourite in favourites.OrderBy(_ => Random.Shared.Next()).Take(5))
            {
                var item = new WallpaperItem
                {
                    Id = favourite.Id,
                    SourceId = favourite.SourceId,
                    FullUrl = favourite.FullUrl!,
                    ThumbnailUrl = favourite.ThumbnailUrl,
                    Title = favourite.Title,
                    Category = favourite.Category,
                    PageUrl = favourite.PageUrl,
                };

                try
                {
                    await ApplyAsync(item, cancellationToken).ConfigureAwait(false);
                    return item;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw lastError ?? new InvalidOperationException("None of your favourites could be used.");
        }

        private async Task<RotationPage> FetchForRotationAsync(
            WallpaperQuery query,
            IWallpaperSource? onlySource,
            CancellationToken cancellationToken)
        {
            if (onlySource is null)
            {
                var page = await Feed.GetPageAsync(query, cancellationToken).ConfigureAwait(false);
                return new RotationPage(page.Items, page.Failures.FirstOrDefault()?.Message);
            }

            try
            {
                var page = await onlySource.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                return new RotationPage(page.Items, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SourceException ex)
            {
                return new RotationPage(Array.Empty<WallpaperItem>(), ex.Message);
            }
            catch (OperationCanceledException)
            {
                return new RotationPage(Array.Empty<WallpaperItem>(), "The source took too long.");
            }
            catch (Exception ex)
            {
                return new RotationPage(Array.Empty<WallpaperItem>(), ex.Message);
            }
        }

        /// <summary>Only the chosen collection, and only for the chosen site.</summary>
        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildRotationFilter(
            IWallpaperSource source,
            string? collection)
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(collection)) return result;

            var group = source.Filters.FirstOrDefault(f => f.Id.Equals("category", StringComparison.OrdinalIgnoreCase))
                ?? source.Filters.FirstOrDefault(f =>
                    f.Options.Any(o => o.Id.Equals(collection, StringComparison.OrdinalIgnoreCase)));

            if (group is null) return result;

            result[source.Id] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [group.Id] = collection,
            };

            return result;
        }

        private sealed record RotationPage(IReadOnlyList<WallpaperItem> Items, string? Failure);

        public void StartRotation()
        {
            if (rotationCts is not null) return;

            rotationCts = new CancellationTokenSource();
            _ = RotationLoopAsync(rotationCts.Token);
        }

        public void StopRotation()
        {
            rotationCts?.Cancel();
            rotationCts?.Dispose();
            rotationCts = null;
        }

        private async Task RotationLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                // A short pause gives the desktop layer a moment to settle after log-in.
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

                var settings = Settings.Current;
                if (settings.RotationEnabled)
                {
                    var now = DateTimeOffset.Now;
                    var last = settings.LastRotationAt?.ToLocalTime() ?? DateTimeOffset.MinValue;

                    if (IsRotationDue(settings, last, now))
                    {
                        Logger.Info("Rotation is due on start-up: applying next wallpaper.");
                        await RotationTickAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.Info("Rotation not due on start-up: keeping current wallpaper and preloading next in background.");
                        _ = PreloadNextAsync(cancellationToken);
                    }
                }

                await AtmosphereTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Start-up rotation check failed: {ex.Message}");
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await RotationTickAsync(cancellationToken).ConfigureAwait(false);
                        await AtmosphereTickAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // A failed rotation must never kill the timer: the next
                        // tick should try again.
                        Logger.Warn($"Wallpaper rotation failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        private async Task RotationTickAsync(CancellationToken cancellationToken)
        {
            var settings = Settings.Current;
            if (!settings.RotationEnabled) return;

            var now = DateTimeOffset.Now;
            var last = settings.LastRotationAt?.ToLocalTime() ?? DateTimeOffset.MinValue;

            if (!IsRotationDue(settings, last, now)) return;

            await ApplyNextAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// One trigger, two shapes. "Time of day" changes at the day and night
        /// times the person picked and catches up after the PC was off; "timer"
        /// counts from the last change, or lands on midnight.
        /// </summary>
        private static bool IsRotationDue(IcebergSettings settings, DateTimeOffset last, DateTimeOffset now)
        {
            if (settings.RotationMode == "timeofday")
            {
                return last < LastDayNightBoundary(settings, now);
            }

            return settings.TimerMode switch
            {
                "daily" => last < new DateTimeOffset(now.Date, now.Offset),
                "every24" => last + TimeSpan.FromHours(24) <= now,
                _ => last + settings.CustomInterval <= now,
            };
        }

        private static DateTimeOffset LastDayNightBoundary(IcebergSettings settings, DateTimeOffset now)
        {
            var today = new DateTimeOffset(now.Date, now.Offset);

            var first = today + settings.DayChange;
            var second = today + settings.NightChange;

            // The two times can be typed in any order; sort them so "last
            // boundary" always means the later one of the two.
            if (second < first) (first, second) = (second, first);

            if (now >= second) return second;
            if (now >= first) return first;

            return second.AddDays(-1);
        }

        private static WallpaperMediaType? ParseMedia(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "photo" => WallpaperMediaType.Photo,
            "illustration" => WallpaperMediaType.Illustration,
            "video" => WallpaperMediaType.Video,
            "gif" => WallpaperMediaType.Gif,
            _ => null,
        };

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> RotationFilters(
            IcebergSettings settings)
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in settings.SourceFilters)
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }

        private static string PhaseHint(AtmosphereSnapshot atmosphere) => atmosphere.Phase switch
        {
            Discovery.Time.DayPhase.Night => "night sky",
            Discovery.Time.DayPhase.Dawn => "sunrise",
            Discovery.Time.DayPhase.Morning => "morning",
            Discovery.Time.DayPhase.Afternoon => "landscape",
            Discovery.Time.DayPhase.GoldenHour => "sunset",
            _ => "landscape",
        };

        /// <summary>
        /// Re-reads the clock every few minutes so the phase stays correct when
        /// the PC has been asleep across a sunrise.
        /// </summary>
        private async Task AtmosphereTickAsync(CancellationToken cancellationToken)
        {
            if (DateTimeOffset.UtcNow - lastAtmosphereRefresh < TimeSpan.FromMinutes(5)) return;

            lastAtmosphereRefresh = DateTimeOffset.UtcNow;

            try
            {
                await RefreshAtmosphereAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not refresh the atmosphere: {ex.Message}");
            }
        }

        public async Task ClearCachesAsync(CancellationToken cancellationToken = default)
        {
            Images.Memory.Clear();
            if (Images.Disk is { } disk)
            {
                await disk.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<(long Bytes, int Files)> MeasureCacheAsync(CancellationToken cancellationToken = default)
        {
            var folder = Path.Combine(DataDirectory, "images");
            if (!Directory.Exists(folder)) return (0, 0);

            long bytes = 0;
            var files = 0;

            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.img", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch (IOException) { }
                }
            }, cancellationToken).ConfigureAwait(false);

            return (bytes, files);
        }

        /// <summary>
        /// Writes a small Lively wallpaper package for one downloaded picture and
        /// returns its folder and image path.
        /// </summary>
        /// <remarks>
        /// Lively's own "import" route opens a preview window on the desktop which
        /// needs a player program that is not always installed. A picture package is
        /// just a folder with LivelyInfo.json next to the image, so we write it
        /// directly and hand the folder to the core. That keeps applying instant and
        /// free of extra dependencies.
        /// </remarks>
        private async Task<(string Folder, string File, string PosterPath)> EnsurePackageAsync(
            WallpaperItem item,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            var name = Hash(item.Key);
            var folder = Path.Combine(LibraryDirectory, name);
            var extension = ExtensionFor(bytes, item.MediaType);
            var fileName = "wallpaper" + extension;
            var imagePath = Path.Combine(folder, fileName);
            var infoPath = Path.Combine(folder, "LivelyInfo.json");

            var type = item.MediaType switch
            {
                WallpaperMediaType.Video => WallpaperType.video,
                WallpaperMediaType.Gif => WallpaperType.gif,
                _ => WallpaperType.picture,
            };

            var still = type == WallpaperType.picture;

            if (imported.TryGetValue(item.Key, out var existing)
                && string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase)
                && File.Exists(infoPath)
                && File.Exists(imagePath))
            {
                var existingPoster = still ? imagePath : (Directory.EnumerateFiles(folder, "thumbnail.*").FirstOrDefault() ?? imagePath);
                return (folder, imagePath, existingPoster);
            }

            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken).ConfigureAwait(false);

            string? thumbFileName = null;
            if (still)
            {
                thumbFileName = fileName;
            }
            else
            {
                try
                {
                    var thumbUrl = item.Thumb;
                    if (!string.IsNullOrWhiteSpace(thumbUrl) && thumbUrl != item.FullUrl)
                    {
                        var thumbBytes = await Images.GetAsync(thumbUrl, cancellationToken).ConfigureAwait(false);
                        if (thumbBytes != null && thumbBytes.Length > 0)
                        {
                            var thumbExt = ExtensionFor(thumbBytes, WallpaperMediaType.Photo);
                            thumbFileName = "thumbnail" + thumbExt;
                            var thumbPath = Path.Combine(folder, thumbFileName);
                            await File.WriteAllBytesAsync(thumbPath, thumbBytes, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to save poster thumbnail for animated wallpaper: {ex.Message}");
                }
            }

            var info = new LivelyInfoModel
            {
                Title = string.IsNullOrWhiteSpace(item.Title) ? "Iceberg wallpaper" : item.Title,
                Type = type,
                FileName = fileName,
                IsAbsolutePath = false,
                Thumbnail = thumbFileName ?? (still ? fileName : null),
                Preview = thumbFileName ?? (still ? fileName : null),
                Author = item.Author ?? item.SourceId,
                Desc = item.SourceId,
            };

            JsonStorage<LivelyInfoModel>.StoreData(infoPath, info);

            imported[item.Key] = folder;

            // Keep the map bounded: it is only an optimisation, not a record.
            if (imported.Count > 500)
            {
                foreach (var key in imported.Keys.Take(imported.Count - 500).ToList())
                {
                    imported.Remove(key);
                }
            }

            await SaveImportedMapAsync(cancellationToken).ConfigureAwait(false);

            var poster = thumbFileName != null ? Path.Combine(folder, thumbFileName) : imagePath;
            return (folder, imagePath, poster);
        }

        private static string Hash(string value) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

        private async Task LoadImportedMapAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(DataDirectory, "imported.json");
            if (!File.Exists(path)) return;

            try
            {
                await using var stream = File.OpenRead(path);
                var map = await JsonSerializer
                    .DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (map is null) return;

                foreach (var pair in map)
                {
                    imported[pair.Key] = pair.Value;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A damaged map only costs one re-import.
            }
        }

        private async Task SaveImportedMapAsync(CancellationToken cancellationToken)
        {
            try
            {
                var path = Path.Combine(DataDirectory, "imported.json");
                var temp = path + ".tmp";

                await using (var stream = File.Create(temp))
                {
                    await JsonSerializer.SerializeAsync(stream, imported, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Move(temp, path, overwrite: true);
            }
            catch (IOException ex)
            {
                Logger.Warn($"Could not save the import map: {ex.Message}");
            }
        }

        private static string ExtensionFor(byte[] bytes, WallpaperMediaType mediaType = WallpaperMediaType.Photo)
        {
            // A video keeps its real container: the player picks the decoder from
            // the extension, so guessing ".mp4" for a WebM file would break it.
            if (bytes.Length >= 12
                && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
            {
                return ".mp4";
            }

            if (bytes.Length >= 4
                && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
            {
                return ".webm";
            }

            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
            if (bytes.Length >= 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
            if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) return ".bmp";

            return mediaType == WallpaperMediaType.Video ? ".mp4" : ".jpg";
        }

    }
}
