using System;
using Lively.Common.Services;
using Lively.UI.WinUI.Services;
using Lively.UI.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Lively.UI.WinUI.Views.Pages
{
    public sealed partial class FavouritesView : Page
    {
        public FavouritesViewModel ViewModel { get; }

        public FavouritesView()
        {
            this.InitializeComponent();

            ViewModel = new FavouritesViewModel(
                App.Services.GetRequiredService<IcebergService>(),
                App.Services.GetRequiredService<IDispatcherService>());

            this.DataContext = ViewModel;
            this.Loaded += FavouritesView_Loaded;
        }

        private async void FavouritesView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.InitializeAsync();

                // Coming back to the page after starring something must show it.
                await ViewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = ex.Message;
            }
        }

        private async void Tile_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // The remove star sits on top of the card. Clicking it must not also
            // open the preview as a side effect.
            if (IsInsideButton(e.OriginalSource)) return;

            if ((sender as FrameworkElement)?.Tag is not FavouriteTile tile) return;

            await ViewModel.OpenPreviewAsync(tile);
        }

        private void ClosePreview_Click(object sender, RoutedEventArgs e) =>
            ViewModel.ClosePreview();

        private static bool IsInsideButton(object source)
        {
            var current = source as DependencyObject;

            while (current != null)
            {
                if (current is Button) return true;
                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) =>
            CardHover.Show(sender, 1);

        private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) =>
            CardHover.Show(sender, 0);

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not FavouriteTile tile) return;

            await ViewModel.RemoveAsync(tile);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.OnClosed();
        }
    }
}
