using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lively.Common.Services;
using Lively.Grpc.Client;
using Lively.Models;
using Lively.Models.Enums;
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

        [ObservableProperty]
        private bool startWithWindows;

        [ObservableProperty]
        private bool showTrayIcon;

        [ObservableProperty]
        private bool rotationEnabled = true;

        // 0 = time of day, 1 = timer.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTimerMode))]
        private int rotationModeIndex;

        // 0 = daily at midnight, 1 = every 24 hours, 2 = custom.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustomTimer))]
        private int timerModeIndex;

        [ObservableProperty]
        private double customHours = 6;

        [ObservableProperty]
        private double customMinutes;

        [ObservableProperty]
        private bool allowVideoInRotation;

        [ObservableProperty]
        private bool rotateLockScreenToo;

        [ObservableProperty]
        private string lockScreenSummary = "Not set by Iceberg yet.";

        [ObservableProperty]
        private string rotationSummary = string.Empty;

        [ObservableProperty]
        private string nextChangeSummary = string.Empty;

        [ObservableProperty]
        private string cacheSummary = "Calculating…";

        [ObservableProperty]
        private string statusMessage = string.Empty;

        public bool IsTimerMode => RotationModeIndex == 1;

        public bool IsCustomTimer => TimerModeIndex == 2;

        public async Task InitializeAsync()
        {
            await service.InitializeAsync();

            var settings = service.Current;

            loading = true;

            ThemeIndex = (int)userSettings.Settings.ApplicationTheme;
            StartWithWindows = userSettings.Settings.Startup;
            ShowTrayIcon = userSettings.Settings.SysTrayIcon;

            RotationEnabled = settings.RotationEnabled;
            RotationModeIndex = settings.RotationMode == "timer" ? 1 : 0;
            TimerModeIndex = settings.TimerMode switch
            {
                "every24" => 1,
                "custom" => 2,
                _ => 0,
            };
            CustomHours = settings.CustomHours;
            CustomMinutes = settings.CustomMinutes;
            AllowVideoInRotation = settings.AllowVideoInRotation;
            RotateLockScreenToo = settings.RotateLockScreenToo;
            UpdateLockScreenSummary();

            loading = false;

            UpdateRotationSummary();
            await RefreshCacheSummaryAsync();
        }

        partial void OnRotationEnabledChanged(bool value) => _ = SaveAsync();

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

        partial void OnStartWithWindowsChanged(bool value)
        {
            if (loading) return;

            userSettings.Settings.Startup = value;
            userSettings.Save<SettingsModel>();
        }

        partial void OnShowTrayIconChanged(bool value)
        {
            if (loading) return;

            userSettings.Settings.SysTrayIcon = value;
            userSettings.Save<SettingsModel>();
        }

        partial void OnRotationModeIndexChanged(int value) => _ = SaveAsync();

        partial void OnTimerModeIndexChanged(int value) => _ = SaveAsync();

        partial void OnCustomHoursChanged(double value) => _ = SaveAsync();

        partial void OnCustomMinutesChanged(double value) => _ = SaveAsync();

        partial void OnAllowVideoInRotationChanged(bool value) => _ = SaveAsync();

        partial void OnRotateLockScreenTooChanged(bool value) => _ = SaveAsync();

        private async Task SaveAsync()
        {
            if (loading) return;

            await service.Settings.UpdateAsync(s => s with
            {
                RotationEnabled = RotationEnabled,
                RotationMode = RotationModeIndex == 1 ? "timer" : "timeofday",
                TimerMode = TimerModeIndex switch
                {
                    1 => "every24",
                    2 => "custom",
                    _ => "daily",
                },
                CustomHours = (int)Math.Round(CustomHours),
                CustomMinutes = (int)Math.Round(CustomMinutes),
                AllowVideoInRotation = AllowVideoInRotation,
                RotateLockScreenToo = RotateLockScreenToo,
            });

            UpdateRotationSummary();
            StatusMessage = "Saved.";
        }

        private void UpdateRotationSummary()
        {
            if (!RotationEnabled)
            {
                RotationSummary = "Rotation is off. Pick a wallpaper from Home whenever you like.";
                NextChangeSummary = string.Empty;
                return;
            }

            if (RotationModeIndex == 0)
            {
                RotationSummary = "Changes at 7 in the morning and 7 in the evening: something bright by day, something dark by night.";
                NextChangeSummary = $"Next change {DescribeNextDayNightChange()}.";
                return;
            }

            RotationSummary = TimerModeIndex switch
            {
                1 => "Changes every 24 hours, counting from the last change.",
                2 => $"Changes every {DescribeInterval()}.",
                _ => "Changes every day at midnight.",
            };

            NextChangeSummary = $"Next change {DescribeNextTimerChange()}.";
        }

        private string DescribeInterval()
        {
            var hours = (int)Math.Round(CustomHours);
            var minutes = (int)Math.Round(CustomMinutes);

            if (hours == 0 && minutes == 0) return "0 minutes — that would never stop";

            var parts = new System.Collections.Generic.List<string>();
            if (hours > 0) parts.Add(hours == 1 ? "1 hour" : $"{hours} hours");
            if (minutes > 0) parts.Add(minutes == 1 ? "1 minute" : $"{minutes} minutes");

            return string.Join(" ", parts);
        }

        private string DescribeNextDayNightChange()
        {
            var now = DateTimeOffset.Now;
            var today = new DateTimeOffset(now.Date, now.Offset);
            var next = now < today.AddHours(7) ? today.AddHours(7)
                : now < today.AddHours(19) ? today.AddHours(19)
                : today.AddDays(1).AddHours(7);

            return $"at {next:HH:mm}";
        }

        private string DescribeNextTimerChange()
        {
            var now = DateTimeOffset.Now;
            var last = service.Current.LastRotationAt?.ToLocalTime();

            if (last is null) return "the next time Iceberg checks (within a minute)";

            DateTimeOffset next = TimerModeIndex switch
            {
                1 => last.Value.AddHours(24),
                2 => last.Value.Add(service.Current.CustomInterval),
                _ => new DateTimeOffset(last.Value.Date, last.Value.Offset).AddDays(1),
            };

            if (next <= now) return "the next time Iceberg checks (within a minute)";

            var span = next - now;
            if (span.TotalHours >= 1) return $"in {(int)span.TotalHours}h {span.Minutes}m";
            return $"in {Math.Max(1, (int)span.TotalMinutes)} minutes";
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
        private async Task UseLastAsLockScreenAsync()
        {
            StatusMessage = "Setting the lock screen…";

            try
            {
                await service.SetLockScreenFromLastAsync();
                StatusMessage = "Lock screen set.";
                UpdateLockScreenSummary();
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
        }

        private void UpdateLockScreenSummary()
        {
            LockScreenSummary = string.IsNullOrWhiteSpace(service.Current.LockScreenTitle)
                ? "Not set by Iceberg yet. Open a wallpaper on Home and choose Set lock screen."
                : $"Last set from {service.Current.LockScreenTitle}.";
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
