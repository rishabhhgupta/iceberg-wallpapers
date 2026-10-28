using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lively.Common.Services;
using Lively.Discovery;
using Lively.Discovery.Favourites;
using Lively.Discovery.Theming;
using Lively.Discovery.Time;
using Lively.Discovery.Weather;
using Lively.Imaging.Processing;
using Lively.Models;
using Lively.Sources;
using Lively.UI.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;

namespace Lively.UI.WinUI.ViewModels
{
    /// <summary>One switch in the Sources panel.</summary>
    public partial class SourceToggle : ObservableObject
    {
        private readonly Func<SourceToggle, Task> changed;

        public SourceToggle(string id, string name, bool isEnabled, Func<SourceToggle, Task> changed)
        {
            Id = id;
            Name = name;
            this.isEnabled = isEnabled;
            this.changed = changed;
        }

        public string Id { get; }

        public string Name { get; }

        [ObservableProperty]
        private bool isEnabled;

        partial void OnIsEnabledChanged(bool value) => _ = changed(this);
    }

    /// <summary>One tile in the Home feed.</summary>
    public partial class HomeTile : ObservableObject
    {
        public HomeTile(WallpaperItem item, bool isFavourite, string sourceName)
        {
            Item = item;
            SourceName = string.IsNullOrWhiteSpace(sourceName) ? item.SourceId : sourceName;
            this.isFavourite = isFavourite;
        }

        public WallpaperItem Item { get; }

        public string SourceName { get; }

        /// <summary>The wallpaper's own name, or nothing at all. Never a filler word.</summary>
        public string Title => Item.Title ?? string.Empty;

        /// <summary>Shown only in the full preview: category and source, joined.</summary>
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                var category = Item.Category;

                // Some sites report "all" when no category was chosen. That is
                // not a category, so fall back to the kind of picture instead.
                if (string.IsNullOrWhiteSpace(category) ||
                    string.Equals(category, "all", StringComparison.OrdinalIgnoreCase))
                {
                    category = Item.MediaType switch
                    {
                        WallpaperMediaType.Illustration => "Illustration",
                        WallpaperMediaType.Video => "Video",
                        WallpaperMediaType.Gif => "GIF",
                        _ => "Photo",
                    };
                }

                parts.Add(category);
                parts.Add(SourceName);
                return string.Join(" · ", parts);
            }
        }

        public bool IsVideo => Item.MediaType == WallpaperMediaType.Video;

        public bool ShowVideoPreview => IsVideo && IsHovered;

        /// <summary>"1920 × 1080 · 2.1 MB" for the hover card. Empty when unknown.</summary>
        public string SizeLabel
        {
            get
            {
                var parts = new List<string>();

                if (Item.Width is int width && Item.Height is int height && width > 0 && height > 0)
                {
                    parts.Add($"{width} × {height}");
                }

                if (Item.FileSizeBytes is { } bytes && bytes > 0)
                {
                    parts.Add($"{bytes / (1024.0 * 1024.0):0.0} MB");
                }

                return string.Join(" · ", parts);
            }
        }

        public bool HasSourceLink => !string.IsNullOrWhiteSpace(Item.PageUrl);

        /// <summary>The small file that plays under the mouse. Null for still pictures.</summary>
        [ObservableProperty]
        private MediaSource previewSource;

        [ObservableProperty]
        private bool isHovered;

        partial void OnIsHoveredChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowVideoPreview));

            if (!value || !IsVideo || string.IsNullOrWhiteSpace(Item.PreviewVideoUrl))
            {
                PreviewSource = null;
                return;
            }

            try
            {
                PreviewSource = MediaSource.CreateFromUri(new Uri(Item.PreviewVideoUrl!));
            }
            catch (UriFormatException)
            {
                PreviewSource = null;
            }
        }

        [ObservableProperty]
        private ImageSource image;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FavouriteGlyph))]
        private bool isFavourite;

        public string FavouriteGlyph => IsFavourite ? "\uE735" : "\uE734";

        [ObservableProperty]
        private bool isApplying;

        [ObservableProperty]
        private bool failed;
    }

    /// <summary>
    /// Drives the Home feed: endless scroll, search, favourites, and applying a
    /// picture to the desktop.
    ///
    /// The loading rules are the important part. Only one page is ever in flight,
    /// a refresh cancels the previous one, and an empty answer from one provider
    /// never leaves the list spinning forever.
    /// </summary>
    public partial class HomeViewModel : ObservableObject
    {
        private const int ThumbnailWidth = 520;
        private const int PreviewWidth = 1800;

        private readonly IcebergService service;
        private readonly IDispatcherService dispatcher;

        private CancellationTokenSource pageCts;
        private CancellationTokenSource imageCts;
        private CancellationTokenSource previewCts;
        private int refreshVersion;
        private bool isInitialised;
        private bool networkHooked;
        private bool lastLoadHadFailures;
        private DateTimeOffset lastNetworkRetry = DateTimeOffset.MinValue;

        public HomeViewModel(IcebergService service, IDispatcherService dispatcher)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            // "Nothing here yet" must disappear the moment the first items arrive,
            // and must not appear while the first page is still loading.
            Tiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        }

        public ObservableCollection<HomeTile> Tiles { get; } = new();

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private bool isLoadingMore;

        [ObservableProperty]
        private bool hasMore = true;

        [ObservableProperty]
        private string statusLine = "Getting things ready…";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string errorMessage;

        [ObservableProperty]
        private string searchText;

        [ObservableProperty]
        private string sourceSummary;

        /// <summary>A friendly time-of-day line: "Good evening", "Good night"...</summary>
        [ObservableProperty]
        private string greeting = "Welcome";

        /// <summary>The small line at the bottom: greeting, plus a notice if one is showing.</summary>
        public string FooterSummary =>
            string.IsNullOrWhiteSpace(StatusLine) ? Greeting : $"{Greeting} · {StatusLine}";

        partial void OnGreetingChanged(string value) => OnPropertyChanged(nameof(FooterSummary));

        partial void OnStatusLineChanged(string value) => OnPropertyChanged(nameof(FooterSummary));

        // 0 = featured, 1 = top, 2 = newest, 3 = random. Matches WallpaperSort.
        [ObservableProperty]
        private int sortIndex;

        // 0 = landscape, 1 = portrait, 2 = any. Matches WallpaperOrientation.
        [ObservableProperty]
        private int orientationIndex;

        // 0 = any, 1 = photos, 2 = illustrations, 3 = videos, 4 = GIFs.
        [ObservableProperty]
        private int mediaTypeIndex;

        /// <summary>Per-source filter choices, keyed by source id then filter id.</summary>
        public Dictionary<string, Dictionary<string, string>> SourceFilters { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The switches in the Sources panel, moved here from Settings.</summary>
        public ObservableCollection<SourceToggle> Sources { get; } = new();

        /// <summary>Every source the app knows about, for the Filters dialog.</summary>
        public IReadOnlyList<IWallpaperSource> AllSources => service.Registry.All;

        /// <summary>Re-reads the switches after the Sources dialog closes.</summary>
        public void RefreshSources() => LoadSources();

        [ObservableProperty]
        private string filterSummary = "Featured · Landscape";

        [ObservableProperty]
        private bool isPreviewOpen;

        [ObservableProperty]
        private HomeTile? previewTile;

        [ObservableProperty]
        private ImageSource? previewImage;

        [ObservableProperty]
        private bool isPreviewLoading;

        [ObservableProperty]
        private bool isPreviewApplying;

        [ObservableProperty]
        private string previewTitle = string.Empty;

        [ObservableProperty]
        private string previewSubtitle = string.Empty;

        /// <summary>The wallpaper's public page. The subtitle opens it when set.</summary>
        [ObservableProperty]
        private string previewPageUrl = string.Empty;

        public bool HasPreviewSourceLink => !string.IsNullOrWhiteSpace(PreviewPageUrl);

        partial void OnPreviewPageUrlChanged(string value) =>
            OnPropertyChanged(nameof(HasPreviewSourceLink));

        [ObservableProperty]
        private string previewMeta = string.Empty;

        [ObservableProperty]
        private string? previewNotice;

        [ObservableProperty]
        private string? previewError;

        public bool HasPreviewError => !string.IsNullOrWhiteSpace(PreviewError);

        public bool HasPreviewNotice => !string.IsNullOrWhiteSpace(PreviewNotice);

        public bool CanPreviewApply => !IsPreviewApplying;

        public string PreviewFavouriteGlyph => PreviewTile?.IsFavourite == true ? "\uE735" : "\uE734";

        partial void OnIsPreviewApplyingChanged(bool value) => OnPropertyChanged(nameof(CanPreviewApply));

        partial void OnPreviewTileChanged(HomeTile? value) =>
            OnPropertyChanged(nameof(PreviewFavouriteGlyph));

        partial void OnPreviewErrorChanged(string? value) =>
            OnPropertyChanged(nameof(HasPreviewError));

        partial void OnPreviewNoticeChanged(string? value) =>
            OnPropertyChanged(nameof(HasPreviewNotice));

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        public bool IsEmpty => !IsLoading && Tiles.Count == 0;

        partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

        public int NextPage { get; private set; } = 1;

        public async Task InitializeAsync()
        {
            if (isInitialised) return;
            isInitialised = true;

            // Show the spinner, not the "nothing here" message, while the first
            // page is still being fetched. Without this the page looks broken
            // for the first few seconds of every launch.
            IsLoading = true;

            try
            {
                await service.InitializeAsync();

                service.AtmosphereChanged += Service_AtmosphereChanged;

                ApplyAtmosphere(service.Atmosphere.Current);

                var settings = service.Current;
                SortIndex = ParseSortIndex(settings.Sort);
                OrientationIndex = ParseOrientationIndex(settings.Orientation);
                MediaTypeIndex = ParseMediaIndex(settings.MediaType);
                SourceFilters = settings.SourceFilters.ToDictionary(
                    pair => pair.Key,
                    pair => new Dictionary<string, string>(pair.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

                LoadSources();
                UpdateFilterSummary();
                UpdateSourceSummary();

                HookNetwork();

                await RefreshAsync();
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static int ParseSortIndex(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "top" => 1,
            "newest" => 2,
            "random" => 3,
            _ => 0,
        };

        private static int ParseOrientationIndex(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "portrait" => 1,
            "any" => 2,
            _ => 0,
        };

        private static int ParseMediaIndex(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "photo" => 1,
            "illustration" => 2,
            "video" => 3,
            "gif" => 4,
            _ => 0,
        };

        private static string MediaValue(int index) => index switch
        {
            1 => "photo",
            2 => "illustration",
            3 => "video",
            4 => "gif",
            _ => "any",
        };

        private void LoadSources()
        {
            Sources.Clear();

            foreach (var source in service.Registry.All)
            {
                var isOn = service.Registry.Enabled.Any(s => s.Id == source.Id);
                Sources.Add(new SourceToggle(source.Id, source.DisplayName, isOn, OnSourceToggledAsync));
            }
        }

        private async Task OnSourceToggledAsync(SourceToggle toggle)
        {
            var enabled = Sources.Where(s => s.IsEnabled).Select(s => s.Id).ToList();

            if (enabled.Count == 0)
            {
                // Never let the person turn everything off and then stare at an
                // empty Home page wondering why.
                toggle.IsEnabled = true;
                return;
            }

            await service.Settings.UpdateAsync(s => s with { EnabledSources = enabled });
            service.BuildPipeline();
            UpdateSourceSummary();
            await RefreshAsync();
        }

        private void UpdateFilterSummary()
        {
            var sort = SortIndex switch
            {
                1 => "Top rated",
                2 => "Newest",
                3 => "Random",
                _ => "Featured",
            };

            var orientation = OrientationIndex switch
            {
                1 => "Portrait",
                2 => "Any shape",
                _ => "Landscape",
            };

            var media = MediaTypeIndex switch
            {
                1 => "Photos",
                2 => "Illustrations",
                3 => "Videos",
                4 => "GIFs",
                _ => null,
            };

            FilterSummary = media is null
                ? $"{sort} · {orientation}"
                : $"{media} · {sort} · {orientation}";
        }

        /// <summary>Reconnects are the common case on a laptop: Wi-Fi drops for a
        /// minute and the feed must fill itself in again without a restart.</summary>
        private void HookNetwork()
        {
            if (networkHooked) return;
            networkHooked = true;

            try
            {
                NetworkChange.NetworkAvailabilityChanged += Network_AvailabilityChanged;
            }
            catch (Exception)
            {
                // Some stripped-down Windows builds do not raise this event.
                // The "Try again" button still covers those machines.
            }
        }

        private void Network_AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable) return;

            _ = dispatcher.TryEnqueue(async () =>
            {
                if (DateTimeOffset.UtcNow - lastNetworkRetry < TimeSpan.FromSeconds(3)) return;
                lastNetworkRetry = DateTimeOffset.UtcNow;

                // A provider that failed while offline is still in its cooldown.
                // Clear it, or the feed would stay empty for three more minutes.
                service.Feed.ClearCooldowns();

                if (Tiles.Count == 0 || lastLoadHadFailures || HasError)
                {
                    await RefreshAsync();
                }
            });
        }

        private void Service_AtmosphereChanged(object sender, AtmosphereSnapshot e)
        {
            _ = dispatcher.TryEnqueue(() => ApplyAtmosphere(e));
        }

        private void ApplyAtmosphere(AtmosphereSnapshot snapshot)
        {
            if (snapshot is null) return;

            Greeting = snapshot.Phase switch
            {
                DayPhase.Night => "Good night",
                DayPhase.Dawn => "Early morning",
                DayPhase.Morning => "Good morning",
                DayPhase.Afternoon => "Good afternoon",
                DayPhase.GoldenHour => "Good evening",
                _ => "Welcome",
            };

            // The greeting already says the time of day, so the status line is
            // kept free for real notices (for example "Wallpaper set").
            StatusLine = string.Empty;
        }

        /// <summary>
        /// Says the wallpaper changed for a few seconds, then puts the time of
        /// day back. The line is the only place it is shown, so it should not be
        /// replaced for good.
        /// </summary>
        private async Task ShowSetNoticeAsync(string sourceId)
        {
            var notice = $"Wallpaper set · {sourceId}";
            StatusLine = notice;

            await Task.Delay(TimeSpan.FromSeconds(4));

            if (StatusLine != notice) return;

            StatusLine = string.Empty;
        }

        private void UpdateSourceSummary()
        {
            var enabled = service.Registry.Enabled;
            if (enabled.Count == 0)
            {
                SourceSummary = "No sources are on";
                return;
            }

            var names = enabled.Select(s => s.DisplayName).ToList();
            var shown = names.Take(4).ToList();
            var tail = names.Count > shown.Count ? "…" : string.Empty;

            SourceSummary = $"Sources: {string.Join(" · ", shown)}{tail}";
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            // A refresh is allowed to replace one that is still in flight. The
            // version number keeps the loser from clearing the spinner or the
            // tiles that the winner is about to fill in.
            var version = Interlocked.Increment(ref refreshVersion);

            pageCts?.Cancel();
            pageCts?.Dispose();
            pageCts = new CancellationTokenSource();
            var token = pageCts.Token;

            imageCts?.Cancel();
            imageCts?.Dispose();
            imageCts = new CancellationTokenSource();

            IsLoading = true;
            ErrorMessage = null;
            HasMore = true;
            NextPage = 1;

            try
            {
                var page = await service.Feed.GetPageAsync(BuildQuery(1), token);

                if (version != refreshVersion) return;

                // A refresh that failed on every source must never wipe the grid.
                // The person keeps what they were looking at and sees a warning.
                var failed = page.IsEmpty && page.Failures.Count > 0;
                if (!failed)
                {
                    Tiles.Clear();
                }

                Append(page, token);

                HasMore = page.HasMore;
                NextPage = 2;
                lastLoadHadFailures = page.Failures.Count > 0;

                if (page.IsEmpty)
                {
                    ErrorMessage = failed
                        ? page.Failures.FirstOrDefault()?.Message
                        : "Nothing came back. Try another source or a different search.";
                }
                else
                {
                    // Some providers worked, so the feed is usable. A warning bar
                    // for a single slow site would only get in the way.
                    ErrorMessage = null;
                }
            }
            catch (OperationCanceledException)
            {
                // A newer refresh replaced this one.
            }
            catch (Exception ex)
            {
                if (version == refreshVersion)
                {
                    ErrorMessage = ex.Message;
                    HasMore = false;
                }
            }
            finally
            {
                if (version == refreshVersion)
                {
                    IsLoading = false;
                    OnPropertyChanged(nameof(HasError));
                    OnPropertyChanged(nameof(IsEmpty));
                }
            }
        }

        [RelayCommand]
        public async Task LoadMoreAsync()
        {
            if (IsLoading || IsLoadingMore || !HasMore) return;

            var version = refreshVersion;
            IsLoadingMore = true;

            try
            {
                var page = await service.Feed.GetPageAsync(
                    BuildQuery(NextPage),
                    pageCts?.Token ?? CancellationToken.None);

                if (version != refreshVersion) return;

                Append(page, pageCts?.Token ?? CancellationToken.None);

                HasMore = page.HasMore;
                NextPage++;

                if (!page.HasMore && Tiles.Count > 0 && page.Items.Count == 0)
                {
                    ErrorMessage = null;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Stop asking after a failure, otherwise fast scrolling hammers a
                // dead network dozens of times a second.
                if (version == refreshVersion)
                {
                    HasMore = false;
                    ErrorMessage = ex.Message;
                }
            }
            finally
            {
                IsLoadingMore = false;
                if (version == refreshVersion)
                {
                    OnPropertyChanged(nameof(IsEmpty));
                }
            }
        }

        private WallpaperQuery BuildQuery(int page) => new()
        {
            Page = page,
            PerPage = 24,
            Text = CleanSearch(),
            Sort = (WallpaperSort)Math.Clamp(SortIndex, 0, 3),
            Orientation = (WallpaperOrientation)Math.Clamp(OrientationIndex, 0, 2),
            MediaType = MediaTypeIndex switch
            {
                1 => WallpaperMediaType.Photo,
                2 => WallpaperMediaType.Illustration,
                3 => WallpaperMediaType.Video,
                4 => WallpaperMediaType.Gif,
                _ => null,
            },
            SourceFilters = SourceFilters.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
        };

        private void Append(FeedPage page, CancellationToken token)
        {
            var existing = new HashSet<string>(Tiles.Select(t => t.Item.Key), StringComparer.Ordinal);

            foreach (var item in page.Items)
            {
                if (!existing.Add(item.Key)) continue;

                var sourceName = service.Registry.Get(item.SourceId)?.DisplayName ?? item.SourceId;
                var tile = new HomeTile(item, service.Favourites.Contains(item.Key), sourceName);
                Tiles.Add(tile);

                _ = LoadThumbnailAsync(tile, token);
            }
        }

        private async Task LoadThumbnailAsync(HomeTile tile, CancellationToken token)
        {
            try
            {
                var bytes = await service.Images.GetAsync(tile.Item.Thumb, token);

                // A preview link can go stale while the full picture still works.
                if (bytes is null
                    && !string.IsNullOrWhiteSpace(tile.Item.ThumbnailUrl)
                    && !string.Equals(tile.Item.ThumbnailUrl, tile.Item.FullUrl, StringComparison.OrdinalIgnoreCase))
                {
                    bytes = await service.Images.GetAsync(tile.Item.FullUrl, token);
                }

                if (bytes is null || token.IsCancellationRequested)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _ = dispatcher.TryEnqueue(() => tile.Failed = true);
                    }

                    return;
                }

                var resized = Thumbnailer.Resize(bytes, ThumbnailWidth) ?? bytes;

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(resized).AsRandomAccessStream())
                {
                    await bitmap.SetSourceAsync(stream);
                }

                _ = dispatcher.TryEnqueue(() => tile.Image = bitmap);
            }
            catch (OperationCanceledException)
            {
                // Scrolled away or refreshed: leave the placeholder.
            }
            catch (Exception)
            {
                _ = dispatcher.TryEnqueue(() => tile.Failed = true);
            }
        }

        [RelayCommand]
        public async Task SearchAsync()
        {
            await RefreshAsync();
        }

        [RelayCommand]
        public async Task ClearSearch()
        {
            if (string.IsNullOrEmpty(SearchText)) return;
            SearchText = string.Empty;
            await RefreshAsync();
        }

        /// <summary>
        /// Applies the two choices from the Filters dialog and reloads the feed.
        /// The choice is stored, so the next launch starts with the same view.
        /// </summary>
        public async Task SetFiltersAsync(
            int sortIndex,
            int orientationIndex,
            int mediaTypeIndex,
            Dictionary<string, Dictionary<string, string>> sourceFilters)
        {
            SortIndex = Math.Clamp(sortIndex, 0, 3);
            OrientationIndex = Math.Clamp(orientationIndex, 0, 2);
            MediaTypeIndex = Math.Clamp(mediaTypeIndex, 0, 4);
            SourceFilters = sourceFilters ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            UpdateFilterSummary();

            await service.Settings.UpdateAsync(s => s with
            {
                Sort = ((WallpaperSort)SortIndex).ToString().ToLowerInvariant(),
                Orientation = ((WallpaperOrientation)OrientationIndex).ToString().ToLowerInvariant(),
                MediaType = MediaValue(MediaTypeIndex),
                SourceFilters = SourceFilters,
            });

            await RefreshAsync();
        }

        [RelayCommand]
        public async Task ApplyAsync(HomeTile tile)
        {
            if (tile is null || tile.IsApplying) return;

            tile.IsApplying = true;
            ErrorMessage = null;

            try
            {
                await service.ApplyAsync(tile.Item);
                _ = ShowSetNoticeAsync(tile.Item.SourceId);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                tile.IsApplying = false;
                OnPropertyChanged(nameof(HasError));
            }
        }

        /// <summary>
        /// Opens the full-size view. The tile's small picture appears instantly,
        /// then the sharp version replaces it when it arrives.
        /// </summary>
        [RelayCommand]
        public async Task OpenPreviewAsync(HomeTile? tile)
        {
            if (tile is null) return;

            previewCts?.Cancel();
            previewCts?.Dispose();
            previewCts = new CancellationTokenSource();
            var token = previewCts.Token;

            PreviewTile = tile;
            PreviewTitle = tile.Title;
            PreviewSubtitle = tile.Subtitle;
            PreviewPageUrl = tile.Item.PageUrl ?? string.Empty;
            PreviewMeta = BuildPreviewMeta(tile.Item);
            PreviewImage = tile.Image;
            PreviewError = null;
            PreviewNotice = null;
            IsPreviewOpen = true;
            IsPreviewLoading = true;

            try
            {
                var bytes = await service.Images.GetAsync(tile.Item.FullUrl, token);
                if (bytes is null)
                {
                    if (!token.IsCancellationRequested)
                    {
                        PreviewError = "The full picture could not be loaded. You can still set it as a wallpaper.";
                    }

                    return;
                }

                var resized = await Task.Run(() => Thumbnailer.Resize(bytes, PreviewWidth) ?? bytes, token);
                if (token.IsCancellationRequested) return;

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(resized).AsRandomAccessStream())
                {
                    await bitmap.SetSourceAsync(stream);
                }

                if (!token.IsCancellationRequested)
                {
                    PreviewImage = bitmap;
                }
            }
            catch (OperationCanceledException)
            {
                // Closed or replaced by another preview.
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    PreviewError = ex.Message;
                }
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsPreviewLoading = false;
                }
            }
        }

        [RelayCommand]
        public void ClosePreview()
        {
            previewCts?.Cancel();
            previewCts?.Dispose();
            previewCts = null;

            IsPreviewOpen = false;
            IsPreviewLoading = false;
            IsPreviewApplying = false;
            PreviewImage = null;
            PreviewTile = null;
            PreviewNotice = null;
            PreviewError = null;
            PreviewPageUrl = string.Empty;
        }

        /// <summary>
        /// Opens the wallpaper's own page in the default browser. The preview
        /// shows the category and source, never a raw web address.
        /// </summary>
        [RelayCommand]
        private async Task OpenPreviewSourceAsync()
        {
            if (string.IsNullOrWhiteSpace(PreviewPageUrl)) return;

            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(PreviewPageUrl));
            }
            catch (Exception ex)
            {
                PreviewError = ex.Message;
            }
        }

        [RelayCommand]
        private async Task ApplyPreviewDesktopAsync()
        {
            if (PreviewTile is not { } tile || IsPreviewApplying) return;

            IsPreviewApplying = true;
            PreviewError = null;
            PreviewNotice = "Setting the desktop wallpaper…";

            try
            {
                await service.ApplyAsync(tile.Item);
                PreviewNotice = "Desktop wallpaper set.";
                _ = ShowSetNoticeAsync(tile.Item.SourceId);
            }
            catch (Exception ex)
            {
                PreviewError = ex.Message;
                PreviewNotice = null;
            }
            finally
            {
                IsPreviewApplying = false;
            }
        }

        [RelayCommand]
        private async Task ApplyPreviewLockScreenAsync()
        {
            if (PreviewTile is not { } tile || IsPreviewApplying) return;

            IsPreviewApplying = true;
            PreviewError = null;
            PreviewNotice = "Setting the lock screen…";

            try
            {
                await service.SetLockScreenAsync(tile.Item);
                PreviewNotice = "Windows lock screen set.";
            }
            catch (Exception ex)
            {
                PreviewError = ex.Message;
                PreviewNotice = null;
            }
            finally
            {
                IsPreviewApplying = false;
            }
        }

        [RelayCommand]
        private async Task TogglePreviewFavouriteAsync()
        {
            if (PreviewTile is not { } tile) return;

            await ToggleFavouriteAsync(tile);
            OnPropertyChanged(nameof(PreviewFavouriteGlyph));
        }

        private string BuildPreviewSubtitle(WallpaperItem item)
        {
            var source = service.Registry.Get(item.SourceId)?.DisplayName ?? item.SourceId;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Category)) parts.Add(item.Category!);
            parts.Add(source);

            return string.Join(" · ", parts);
        }

        private static string BuildPreviewMeta(WallpaperItem item)
        {
            var parts = new List<string>();

            if (item.Width is { } width && item.Height is { } height && width > 0 && height > 0)
            {
                parts.Add($"{width} × {height}");
            }

            if (item.FileSizeBytes is { } bytes && bytes > 0)
            {
                parts.Add($"{bytes / (1024.0 * 1024.0):0.0} MB");
            }

            return string.Join(" · ", parts);
        }

        [RelayCommand]
        public Task ToggleFavouriteAsync(HomeTile tile)
        {
            if (tile is null) return Task.CompletedTask;

            tile.IsFavourite = !tile.IsFavourite;

            if (tile.IsFavourite)
            {
                return service.Favourites.AddAsync(new FavouriteItem
                {
                    Id = tile.Item.Key,
                    SourceId = tile.Item.SourceId,
                    Title = tile.Item.Title,
                    Category = tile.Item.Category,
                    PageUrl = tile.Item.PageUrl,
                    ThumbnailUrl = tile.Item.Thumb,
                    FullUrl = tile.Item.FullUrl,
                    AddedAt = DateTimeOffset.UtcNow,
                });
            }

            return service.Favourites.RemoveAsync(tile.Item.Key);
        }

        [RelayCommand]
        public async Task SurpriseAsync()
        {
            if (IsLoading) return;

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                var item = await service.ApplyNextAsync();
                _ = ShowSetNoticeAsync(item.SourceId);

                if (Tiles.FirstOrDefault(t => t.Item.Key == item.Key) is { } tile)
                {
                    tile.IsApplying = false;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasError));
            }
        }

        public void OnClosed()
        {
            // The page is cached so the grid survives a trip to Settings. Only
            // the preview is torn down; the feed, its pictures and the network
            // watcher stay exactly as they were.
            previewCts?.Cancel();
        }

        private string CleanSearch() => string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
    }
}
