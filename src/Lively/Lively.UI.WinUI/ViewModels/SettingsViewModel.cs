using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lively.Common.Services;
using Lively.Grpc.Client;
using Lively.Models;
using Lively.Models.Enums;
using Lively.Sources;
using Lively.UI.WinUI.Services;

namespace Lively.UI.WinUI.ViewModels
{
    /// <summary>
    /// Iceberg's settings: one page, grouped, with no nesting.
    ///
    /// Every change is written immediately rather than on a Save button: a
    /// wallpaper app is left running for days, and a forgotten Save button is the
    /// classic way to lose a setting.
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IcebergService service;
        private readonly IMainNavigator navigator;
        private readonly IUserSettingsClient userSettings;
        private readonly List<string> siteIds = new();
        private readonly List<string> collectionIds = new();
        private readonly List<string> collectionLabels = new();
        private bool loading = true;

        public SettingsViewModel(IcebergService service, IMainNavigator navigator, IUserSettingsClient userSettings)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
            this.userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        }

        // 0 = follow Windows, 1 = light, 2 = dark.
        [ObservableProperty]
        private int themeIndex;

        // 0 = favourites, 1 = one site and collection, 2 = every site.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPredefinedSource))]
        private int rotationSourceIndex = 2;

        [ObservableProperty]
        private int siteIndex;

        [ObservableProperty]
        private int collectionIndex;

        // 0 = off, 1 = time of day, 2 = timer.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTimeOfDayMode))]
        [NotifyPropertyChangedFor(nameof(IsTimerMode))]
        [NotifyPropertyChangedFor(nameof(IsCustomTimer))]
        private int rotationModeIndex = 1;

        // 0 = daily at midnight, 1 = every 24 hours, 2 = custom.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustomTimer))]
        private int timerModeIndex;

        [ObservableProperty]
        private double customHours = 6;

        [ObservableProperty]
        private double customMinutes;

        [ObservableProperty]
        private TimeSpan dayTime = TimeSpan.FromHours(7);

        [ObservableProperty]
        private TimeSpan nightTime = TimeSpan.FromHours(19);

        [ObservableProperty]
        private bool allowVideoInRotation;

        [ObservableProperty]
        private bool rotateLockScreenToo;

        [ObservableProperty]
        private string rotationSummary = string.Empty;

        [ObservableProperty]
        private string cacheSummary = "Calculating…";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
        private string statusMessage = string.Empty;

        public ObservableCollection<string> SiteNames { get; } = new();

        public ObservableCollection<string> CollectionNames { get; } = new();

        public bool IsPredefinedSource => RotationSourceIndex == 1;

        public bool IsTimeOfDayMode => RotationModeIndex == 1;

        public bool IsTimerMode => RotationModeIndex == 2;

        public bool IsCustomTimer => RotationModeIndex == 2 && TimerModeIndex == 2;

        public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

        public async Task InitializeAsync()
        {
            await service.InitializeAsync();

            var settings = service.Current;

            loading = true;

            ThemeIndex = (int)userSettings.Settings.ApplicationTheme;

            RotationModeIndex = !settings.RotationEnabled
                ? 0
                : settings.RotationMode == "timer" ? 2 : 1;

            RotationSourceIndex = settings.RotationSource switch
            {
                "favourites" => 0,
                "predefined" => 1,
                _ => 2,
            };

            BuildSites();
            var wantedSite = string.IsNullOrWhiteSpace(settings.RotationSourceId) ? 0 : siteIds.FindIndex(
                id => string.Equals(id, settings.RotationSourceId, StringComparison.OrdinalIgnoreCase));
            SiteIndex = Math.Max(0, wantedSite);

            BuildCollections();
            var wantedCollection = string.IsNullOrWhiteSpace(settings.RotationCollection) ? 0 : collectionIds.FindIndex(
                id => string.Equals(id, settings.RotationCollection, StringComparison.OrdinalIgnoreCase));
            CollectionIndex = Math.Max(0, wantedCollection);

            TimerModeIndex = settings.TimerMode switch
            {
                "every24" => 1,
                "custom" => 2,
                _ => 0,
            };

            CustomHours = settings.CustomHours;
            CustomMinutes = settings.CustomMinutes;
            DayTime = settings.DayChange;
            NightTime = settings.NightChange;
            AllowVideoInRotation = settings.AllowVideoInRotation;
            RotateLockScreenToo = settings.RotateLockScreenToo;

            EnsureAlwaysOn();

            loading = false;

            UpdateRotationSummary();
            await RefreshCacheSummaryAsync();
        }

        /// <summary>
        /// Starts with Windows and sits in the tray, with no switch to turn it
        /// off. The switch was removed on purpose; this keeps older settings
        /// files from quietly leaving the app behind.
        /// </summary>
        private void EnsureAlwaysOn()
        {
            if (userSettings.Settings.Startup && userSettings.Settings.SysTrayIcon)
            {
                return;
            }

            userSettings.Settings.Startup = true;
            userSettings.Settings.SysTrayIcon = true;
            userSettings.Save<SettingsModel>();

            if (App.Services.GetService(typeof(ICommandsClient)) is ICommandsClient commands)
            {
                _ = commands.AutomationCommandAsync(new[] { "--startup", "true" });
            }
        }

        private void BuildSites()
        {
            siteIds.Clear();
            SiteNames.Clear();

            foreach (var source in service.Registry.Enabled
                         .Where(s => !s.RequiresApiKey || s.IsConfigured)
                         .OrderBy(s => s.DisplayName, StringComparer.CurrentCulture))
            {
                siteIds.Add(source.Id);
                SiteNames.Add(source.DisplayName);
            }
        }

        private IWallpaperSource? CurrentSite =>
            SiteIndex >= 0 && SiteIndex < siteIds.Count ? service.Registry.Get(siteIds[SiteIndex]) : null;

        private void BuildCollections()
        {
            collectionIds.Clear();
            collectionLabels.Clear();
            CollectionNames.Clear();

            var source = CurrentSite;
            if (source is null)
            {
                return;
            }

            // The category group is the one that behaves like a collection. Some
            // sites also have a content or purity group, which is not a genre.
            var group = source.Filters.FirstOrDefault(f => f.Id.Equals("category", StringComparison.OrdinalIgnoreCase))
                ?? source.Filters.FirstOrDefault();

            if (group is null || group.Options.Count == 0)
            {
                collectionIds.Add(string.Empty);
                collectionLabels.Add("All");
                CollectionNames.Add("All");
                return;
            }

            foreach (var option in group.Options)
            {
                collectionIds.Add(option.Id);
                collectionLabels.Add(option.Label);
                CollectionNames.Add(option.Label);
            }
        }

        partial void OnThemeIndexChanged(int value)
        {
            if (loading) return;

            userSettings.Settings.ApplicationTheme = (AppTheme)Math.Clamp(value, 0, 2);
            userSettings.Save<SettingsModel>();

            if (App.Services.GetService(typeof(MainWindow)) is MainWindow window)
            {
                window.ApplyTheme();
            }
        }

        partial void OnRotationSourceIndexChanged(int value)
        {
            UpdateRotationSummary();
            _ = SaveAsync();
        }

        partial void OnSiteIndexChanged(int value)
        {
            if (loading) return;

            BuildCollections();
            CollectionIndex = 0;

            UpdateRotationSummary();
            _ = SaveAsync();
        }

        partial void OnCollectionIndexChanged(int value)
        {
            UpdateRotationSummary();
            _ = SaveAsync();
        }

        partial void OnRotationModeIndexChanged(int value)
        {
            UpdateRotationSummary();
            _ = SaveAsync();
        }

        partial void OnTimerModeIndexChanged(int value)
        {
            UpdateRotationSummary();
            _ = SaveAsync();
        }

        partial void OnCustomHoursChanged(double value) => _ = SaveAsync();

        partial void OnCustomMinutesChanged(double value) => _ = SaveAsync();

        partial void OnDayTimeChanged(TimeSpan value)
        {
            UpdateRotationSummary();
            _ = SaveAsync();
        }

        partial void OnNightTimeChanged(TimeSpan value)
        {
            UpdateRotationSummary();
            _ = SaveAsync();
        }

        partial void OnAllowVideoInRotationChanged(bool value) => _ = SaveAsync();

        partial void OnRotateLockScreenTooChanged(bool value) => _ = SaveAsync();

        private async Task SaveAsync()
        {
            if (loading) return;

            var siteId = SiteIndex >= 0 && SiteIndex < siteIds.Count ? siteIds[SiteIndex] : null;
            var collection = CollectionIndex >= 0 && CollectionIndex < collectionIds.Count
                ? collectionIds[CollectionIndex]
                : null;

            await service.Settings.UpdateAsync(s => s with
            {
                RotationEnabled = RotationModeIndex != 0,
                RotationMode = RotationModeIndex == 2 ? "timer" : "timeofday",
                RotationSource = RotationSourceIndex switch
                {
                    0 => "favourites",
                    1 => "predefined",
                    _ => "random",
                },
                RotationSourceId = siteId,
                RotationCollection = collection,
                TimerMode = TimerModeIndex switch
                {
                    1 => "every24",
                    2 => "custom",
                    _ => "daily",
                },
                CustomHours = (int)Math.Round(CustomHours),
                CustomMinutes = (int)Math.Round(CustomMinutes),
                DayHour = DayTime.Hours,
                DayMinute = DayTime.Minutes,
                NightHour = NightTime.Hours,
                NightMinute = NightTime.Minutes,
                AllowVideoInRotation = AllowVideoInRotation,
                RotateLockScreenToo = RotateLockScreenToo,
            });

            StatusMessage = "Saved.";
        }

        private void UpdateRotationSummary()
        {
            if (RotationModeIndex == 0)
            {
                RotationSummary = "Off. Nothing changes unless you pick a wallpaper yourself.";
                return;
            }

            var schedule = RotationModeIndex == 1
                ? $"twice a day, at {Clock(DayTime)} and {Clock(NightTime)}"
                : TimerModeIndex switch
                {
                    1 => "every 24 hours",
                    2 => $"every {DescribeInterval()}",
                    _ => "every day at midnight",
                };

            RotationSummary = RotationSourceIndex switch
            {
                0 => $"Your favourites, {schedule}.",
                1 => PredefinedSummary(schedule),
                _ => $"Every site you have switched on, {schedule}.",
            };
        }

        private string PredefinedSummary(string schedule)
        {
            var site = SiteIndex >= 0 && SiteIndex < SiteNames.Count ? SiteNames[SiteIndex] : "a site";
            var collection = CollectionIndex >= 0 && CollectionIndex < collectionLabels.Count
                ? collectionLabels[CollectionIndex]
                : "All";

            return collection.Equals("All", StringComparison.OrdinalIgnoreCase)
                ? $"{site}, {schedule}."
                : $"{site} · {collection}, {schedule}.";
        }

        private static string Clock(TimeSpan time) =>
            DateTime.Today.Add(time).ToString("h:mm tt");

        private string DescribeInterval()
        {
            var hours = (int)Math.Round(CustomHours);
            var minutes = (int)Math.Round(CustomMinutes);

            if (hours == 0 && minutes == 0) return "0 minutes — that would never stop";

            var parts = new List<string>();
            if (hours > 0) parts.Add(hours == 1 ? "1 hour" : $"{hours} hours");
            if (minutes > 0) parts.Add(minutes == 1 ? "1 minute" : $"{minutes} minutes");

            return string.Join(" ", parts);
        }

        [RelayCommand]
        private async Task RefreshCacheSummaryAsync()
        {
            var (bytes, files) = await service.MeasureCacheAsync();
            CacheSummary = files == 0
                ? "No images cached yet."
                : $"{files} images · {bytes / (1024.0 * 1024.0):0.0} MB";
        }

        [RelayCommand]
        private async Task ClearCacheAsync()
        {
            CacheSummary = "Clearing…";

            try
            {
                await service.ClearCachesAsync();
                StatusMessage = "Cache cleared.";
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }

            await RefreshCacheSummaryAsync();
        }

        [RelayCommand]
        private void OpenPage(string page)
        {
            if (Enum.TryParse<ContentPageType>(page, out var type))
            {
                navigator.NavigateTo(type);
            }
        }
    }
}
