using System;
using Lively.Common.Services;
using Lively.Grpc.Client;
using Lively.Models.Enums;
using Lively.UI.WinUI.Services;
using Lively.UI.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Lively.UI.WinUI.Views.Pages
{
    public sealed partial class SettingsPage : Page
    {
        // Survives the page being recreated by navigation, so coming back from a
        // sub-page lands where the person left off instead of at the top.
        private static double lastOffset;
        private bool isLoaded;
        private bool isRestoring;

        public SettingsViewModel ViewModel { get; }

        public SettingsPage()
        {
            this.InitializeComponent();

            ViewModel = new SettingsViewModel(
                App.Services.GetRequiredService<IcebergService>(),
                App.Services.GetRequiredService<IMainNavigator>(),
                App.Services.GetRequiredService<IUserSettingsClient>());

            this.DataContext = ViewModel;
            this.Loaded += SettingsPage_Loaded;
            this.SettingsScroll.ViewChanged += SettingsScroll_ViewChanged;
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            RestoreScroll();

            try
            {
                await ViewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = ex.Message;
            }

            // The page is taller once the cards are filled in, so restore again.
            RestoreScroll();
            isLoaded = true;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (SettingsScroll != null)
            {
                lastOffset = SettingsScroll.VerticalOffset;
            }
            base.OnNavigatedFrom(e);
        }

        private void SettingsScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (isLoaded && !isRestoring)
            {
                lastOffset = SettingsScroll.VerticalOffset;
            }
        }

        private void RestoreScroll()
        {
            if (lastOffset <= 0) return;

            // ChangeView before the first layout pass is ignored, so ask the
            // dispatcher to run it once the scroll viewer knows its size.
            isRestoring = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                SettingsScroll.ChangeView(null, lastOffset, null, true);
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => isRestoring = false);
            });
        }

        private void Card_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string page })
            {
                if (string.Equals(page, "about", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/rishabhhgupta/iceberg-wallpapers"));
                    return;
                }

                if (Enum.TryParse<ContentPageType>(page, out var type))
                {
                    lastOffset = SettingsScroll.VerticalOffset;
                    ViewModel.OpenPageCommand.Execute(page);
                }
            }
        }
    }
}
