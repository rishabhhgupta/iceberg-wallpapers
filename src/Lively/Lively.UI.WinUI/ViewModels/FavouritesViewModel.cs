using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lively.Common.Services;
using Lively.Discovery;
using Lively.Discovery.Favourites;
using Lively.Imaging.Processing;
using Lively.Sources;
using Lively.UI.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lively.UI.WinUI.ViewModels
{
    public partial class FavouriteTile : ObservableObject
    {
        public FavouriteTile(FavouriteItem item, string sourceName)
        {
            Item = item;
            SourceName = sourceName;
        }

        public FavouriteItem Item { get; }

        public string SourceName { get; }

        public string Title => string.IsNullOrWhiteSpace(Item.Title) ? "Saved wallpaper" : Item.Title!;

        public string Subtitle
        {
            get
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(Item.Category) &&
                    !string.Equals(Item.Category, "all", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(Item.Category!);
                }

                parts.Add(SourceName);
                return string.Join(" · ", parts);
            }
        }

        [ObservableProperty]
        private ImageSource image;

        [ObservableProperty]
        private bool isApplying;
    }

    /// <summary>
    /// The Favourites page. Favourites are a plain JSON file, not a database, so
    /// they survive a reinstall by copying one file and they can be read by hand.
    /// </summary>
    public partial class FavouritesViewModel : ObservableObject
    {
        private const int ThumbnailWidth = 520;
        private const int PreviewWidth = 1800;

        private readonly IcebergService service;
        private readonly IDispatcherService dispatcher;
        private CancellationTokenSource loadCts;
        private CancellationTokenSource previewCts;
        private bool initialised;

        public FavouritesViewModel(IcebergService service, IDispatcherService dispatcher)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public ObservableCollection<FavouriteTile> Tiles { get; } = new();

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Nothing saved yet. Tap the star on any wallpaper.";

        [ObservableProperty]
        private bool isEmpty = true;

        [ObservableProperty]
        private Brush backgroundBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        [ObservableProperty]
        private Brush textBrush = new SolidColorBrush(Microsoft.UI.Colors.White);

        [ObservableProperty]
        private ElementTheme elementTheme = ElementTheme.Default;

        [ObservableProperty]
        private bool isPreviewOpen;

        [ObservableProperty]
        private FavouriteTile? previewTile;

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

        partial void OnIsPreviewApplyingChanged(bool value) => OnPropertyChanged(nameof(CanPreviewApply));

        partial void OnPreviewErrorChanged(string? value) =>
            OnPropertyChanged(nameof(HasPreviewError));

        partial void OnPreviewNoticeChanged(string? value) =>
            OnPropertyChanged(nameof(HasPreviewNotice));

        public async Task InitializeAsync()
        {
            if (initialised) return;
            initialised = true;

            service.AtmosphereChanged += Service_AtmosphereChanged;
            ApplyAtmosphere(service.Atmosphere.Current);

            await service.InitializeAsync();
            await RefreshAsync();
        }

        private void Service_AtmosphereChanged(object sender, AtmosphereSnapshot e) =>
            _ = dispatcher.TryEnqueue(() => ApplyAtmosphere(e));

        private void ApplyAtmosphere(AtmosphereSnapshot snapshot)
        {
            if (snapshot is null) return;

            BackgroundBrush = PaletteBrushes.Background(snapshot.Palette);
            TextBrush = PaletteBrushes.Text(snapshot.Palette);
            ElementTheme = snapshot.Palette.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            loadCts?.Cancel();
            loadCts?.Dispose();
            loadCts = new CancellationTokenSource();
            var token = loadCts.Token;

            IsLoading = true;

            try
            {
                Tiles.Clear();

                foreach (var favourite in service.Favourites.Items.OrderByDescending(f => f.AddedAt))
                {
                    var sourceName = service.Registry.Get(favourite.SourceId)?.DisplayName ?? favourite.SourceId;
                    var tile = new FavouriteTile(favourite, sourceName);
                    Tiles.Add(tile);
                    _ = LoadThumbnailAsync(tile, token);
                }

                IsEmpty = Tiles.Count == 0;
                StatusMessage = IsEmpty
                    ? "Nothing saved yet. Tap the star on any wallpaper."
                    : $"{Tiles.Count} saved";
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task LoadThumbnailAsync(FavouriteTile tile, CancellationToken token)
        {
            try
            {
                var url = string.IsNullOrWhiteSpace(tile.Item.ThumbnailUrl)
                    ? tile.Item.FullUrl
                    : tile.Item.ThumbnailUrl!;

                if (string.IsNullOrWhiteSpace(url)) return;

                var bytes = await service.Images.GetAsync(url!, token);

                if (bytes is null
                    && !string.Equals(url, tile.Item.FullUrl, StringComparison.OrdinalIgnoreCase))
                {
                    bytes = await service.Images.GetAsync(tile.Item.FullUrl, token);
                }

                if (bytes is null || token.IsCancellationRequested) return;

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
            }
            catch (Exception)
            {
                // Leave the placeholder.
            }
        }

        [RelayCommand]
        public async Task ApplyAsync(FavouriteTile tile)
        {
            if (tile is null || tile.IsApplying) return;

            tile.IsApplying = true;
            StatusMessage = "Setting wallpaper…";

            try
            {
                await service.ApplyAsync(ToWallpaper(tile.Item));
                StatusMessage = $"Wallpaper set · {tile.Item.SourceId}";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                tile.IsApplying = false;
            }
        }

        [RelayCommand]
        public async Task OpenPreviewAsync(FavouriteTile tile)
        {
            if (tile is null) return;

            previewCts?.Cancel();
            previewCts?.Dispose();
            previewCts = new CancellationTokenSource();
            var token = previewCts.Token;

            PreviewTile = tile;
            // The plan is explicit: the real name, or nothing at all. Never a
            // filler word like "Saved wallpaper".
            PreviewTitle = tile.Item.Title ?? string.Empty;
            PreviewSubtitle = tile.Subtitle;
            PreviewPageUrl = tile.Item.PageUrl ?? string.Empty;
            PreviewMeta = $"Saved {tile.Item.AddedAt.LocalDateTime:d MMM yyyy}";
            PreviewImage = tile.Image;
            PreviewError = null;
            PreviewNotice = null;
            IsPreviewOpen = true;
            IsPreviewLoading = true;

            try
            {
                var url = tile.Item.FullUrl;
                if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(tile.Item.LocalPath))
                {
                    url = tile.Item.LocalPath;
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    PreviewError = "This saved wallpaper has no picture address. Remove it and save it again.";
                    return;
                }

                var bytes = await service.Images.GetAsync(url!, token);
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
            PreviewPageUrl = string.Empty;
            PreviewNotice = null;
            PreviewError = null;
        }

        /// <summary>Opens the wallpaper's own page in the default browser.</summary>
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
                await service.ApplyAsync(ToWallpaper(tile.Item));
                PreviewNotice = "Desktop wallpaper set.";
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
                await service.SetLockScreenAsync(ToWallpaper(tile.Item));
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
        private async Task RemovePreviewAsync()
        {
            if (PreviewTile is not { } tile) return;

            await RemoveAsync(tile);

            if (!Tiles.Contains(tile))
            {
                ClosePreview();
            }
        }

        [RelayCommand]
        public async Task RemoveAsync(FavouriteTile tile)
        {
            if (tile is null) return;

            try
            {
                await service.Favourites.RemoveAsync(tile.Item.Id);
                Tiles.Remove(tile);
                IsEmpty = Tiles.Count == 0;
                StatusMessage = IsEmpty ? "Nothing saved yet." : $"{Tiles.Count} saved";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        }

        /// <summary>
        /// Favourites are stored with a "source:id" id so two sites cannot collide.
        /// This splits it back apart for the downloader.
        /// </summary>
        private static WallpaperItem ToWallpaper(FavouriteItem favourite)
        {
            var key = favourite.Id ?? string.Empty;
            var separator = key.IndexOf(':');

            return new WallpaperItem
            {
                Id = separator > 0 ? key[(separator + 1)..] : key,
                SourceId = favourite.SourceId,
                FullUrl = favourite.FullUrl ?? string.Empty,
                ThumbnailUrl = favourite.ThumbnailUrl,
                Title = favourite.Title,
                PageUrl = favourite.PageUrl,
            };
        }

        public void OnClosed()
        {
            service.AtmosphereChanged -= Service_AtmosphereChanged;
            initialised = false;
            loadCts?.Cancel();
            ClosePreview();
        }
    }
}
