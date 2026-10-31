using Lively.Common.Services;
using Lively.Models.Enums;
using Lively.UI.WinUI.Views.Pages;
using Lively.UI.WinUI.Views.Pages.Settings;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace Lively.UI.WinUI.Services
{
    public class MainNavigator : IMainNavigator
    {
        private object? frame;

        /// <inheritdoc/>
        public event EventHandler<ContentPageType>? ContentPageChanged;

        public object? RootFrame { get; set; }

        /// <inheritdoc/>
        public object? Frame
        {
            get => frame;
            set
            {
                if (frame is Frame previous)
                {
                    previous.Navigated -= Frame_Navigated;
                }

                frame = value;

                if (frame is Frame next)
                {
                    next.Navigated += Frame_Navigated;
                }
            }
        }

        public ContentPageType? CurrentPage { get; private set; } = null;

        public void NavigateTo(ContentPageType contentPage, object navArgs = null)
        {
            if (CurrentPage == contentPage)
                return;

            // Switching tabs should settle in place; only a page opened from
            // inside another page slides in from the right.
            NavigationTransitionInfo transition = IsMenuTab(contentPage)
                ? new EntranceNavigationTransitionInfo()
                : new DrillInNavigationTransitionInfo();

            InternalNavigateTo(contentPage, transition, navArgs);
        }

        public void Reload()
        {
            if (CurrentPage == null)
                return;

            InternalNavigateTo(CurrentPage.Value, new EntranceNavigationTransitionInfo());
        }

        private void InternalNavigateTo(ContentPageType contentPage, NavigationTransitionInfo transition, object navArgs = null)
        {
            Type pageType = PageTypeFor(contentPage);

            if (Frame is Frame f)
            {
                f.Navigate(pageType, navArgs, transition);

                // A menu tab is a fresh start, not another step in the trail.
                // Without this, opening Settings while inside one of its
                // sub-pages left the sub-page behind, and Back jumped into it.
                if (IsMenuTab(contentPage))
                {
                    f.BackStack.Clear();
                }

                CurrentPage = contentPage;
                ContentPageChanged?.Invoke(this, contentPage);
            }
        }

        /// <summary>
        /// Keeps the recorded page in step with the real history, so pressing
        /// Back and then opening the same sub-page again still works.
        /// </summary>
        private void Frame_Navigated(object sender, NavigationEventArgs e)
        {
            if (PageForType(e.SourcePageType) is { } page)
            {
                CurrentPage = page;
            }
        }

        private static Type PageTypeFor(ContentPageType contentPage) => contentPage switch
        {
            ContentPageType.home => typeof(HomeView),
            ContentPageType.library => typeof(LibraryView),
            ContentPageType.favourites => typeof(FavouritesView),
            ContentPageType.appupdate => typeof(AppUpdateView),
            ContentPageType.settingsHub => typeof(SettingsPage),
            ContentPageType.settingsGeneral => typeof(SettingsGeneralView),
            ContentPageType.settingsPerformance => typeof(SettingsPerformanceView),
            ContentPageType.settingsScreensaver => typeof(SettingsScreensaverView),
            ContentPageType.settingsWallpaper => typeof(SettingsWallpaperView),
            ContentPageType.settingsSystem => typeof(SettingsSystemView),
            _ => throw new NotImplementedException(),
        };

        private static ContentPageType? PageForType(Type pageType) => pageType switch
        {
            var t when t == typeof(HomeView) => ContentPageType.home,
            var t when t == typeof(LibraryView) => ContentPageType.library,
            var t when t == typeof(FavouritesView) => ContentPageType.favourites,
            var t when t == typeof(AppUpdateView) => ContentPageType.appupdate,
            var t when t == typeof(SettingsPage) => ContentPageType.settingsHub,
            var t when t == typeof(SettingsGeneralView) => ContentPageType.settingsGeneral,
            var t when t == typeof(SettingsPerformanceView) => ContentPageType.settingsPerformance,
            var t when t == typeof(SettingsScreensaverView) => ContentPageType.settingsScreensaver,
            var t when t == typeof(SettingsWallpaperView) => ContentPageType.settingsWallpaper,
            var t when t == typeof(SettingsSystemView) => ContentPageType.settingsSystem,
            _ => null,
        };

        /// <summary>Pages reached from the left menu, not from another page.</summary>
        private static bool IsMenuTab(ContentPageType contentPage) => contentPage is
            ContentPageType.home or
            ContentPageType.library or
            ContentPageType.favourites or
            ContentPageType.settingsHub or
            ContentPageType.appupdate;
    }
}
