using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lively.Common.Services;
using Lively.Sources;
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
    public sealed partial class HomeView : Page
    {
        public HomeViewModel ViewModel { get; }

        public HomeView()
        {
            this.InitializeComponent();

            // Keep the page (and its pictures) alive when the person visits
            // Settings and comes back. Without this the grid was rebuilt from
            // scratch and every picture downloaded again.
            this.NavigationCacheMode = NavigationCacheMode.Required;

            ViewModel = new HomeViewModel(
                App.Services.GetRequiredService<IcebergService>(),
                App.Services.GetRequiredService<IDispatcherService>());

            this.DataContext = ViewModel;
            this.Loaded += HomeView_Loaded;
        }

        private async void HomeView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                ViewModel.ErrorMessage = ex.Message;
            }
        }

        private void FeedScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (sender is not ScrollViewer scroll) return;

            // Start fetching before the bottom is reached so the next row is
            // usually ready by the time it is needed.
            if (scroll.ScrollableHeight - scroll.VerticalOffset < 700)
            {
                _ = ViewModel.LoadMoreAsync();
            }
        }

        private async void Tile_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // The star is a button inside the card. Clicking it must not also
            // open the preview, so anything inside a button is ignored here.
            if (IsInsideButton(e.OriginalSource)) return;

            if ((sender as FrameworkElement)?.Tag is not HomeTile tile) return;

            await ViewModel.OpenPreviewCommand.ExecuteAsync(tile);
        }

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

        /// <summary>
        /// One switch per site. Turning one off takes effect immediately and the
        /// feed reloads, so the result is visible without a second button.
        /// </summary>
        private async void Sources_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel { Spacing = 6 };

            panel.Children.Add(new TextBlock
            {
                Text = "Pictures come from every site that is switched on. Turn one off to leave it out of the feed and out of the rotation.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
            });

            foreach (var source in ViewModel.Sources)
            {
                var toggle = new ToggleSwitch
                {
                    Header = source.Name,
                    IsOn = source.IsEnabled,
                    MinWidth = 280,
                };

                toggle.Toggled += (_, _) => source.IsEnabled = toggle.IsOn;
                panel.Children.Add(toggle);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Sources",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 460,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                CloseButtonText = "Done",
                DefaultButton = ContentDialogButton.Close,
            };

            await dialog.ShowAsync();

            // If every switch was turned off, the view model puts one back on.
            // Re-read the collection so the panel matches what really happened.
            ViewModel.RefreshSources();
        }

        /// <summary>
        /// One dialog: the shared choices first, then a block per switched-on
        /// site with that site's own options, exactly as the site understands them.
        /// </summary>
        private async void Filters_Click(object sender, RoutedEventArgs e)
        {
            var media = new ComboBox
            {
                Header = "What kind",
                ItemsSource = new[] { "Everything", "Photos", "Illustrations", "Videos" },
                SelectedIndex = ViewModel.MediaTypeIndex,
                MinWidth = 300,
            };

            var sort = new ComboBox
            {
                Header = "Sort by",
                ItemsSource = new[] { "Featured", "Top rated", "Newest", "Random" },
                SelectedIndex = ViewModel.SortIndex,
                MinWidth = 300,
            };

            var orientation = new ComboBox
            {
                Header = "Shape",
                ItemsSource = new[] { "Landscape", "Portrait", "Any shape" },
                SelectedIndex = ViewModel.OrientationIndex,
                MinWidth = 300,
            };

            var panel = new StackPanel
            {
                Spacing = 12,
                MinWidth = 420,
            };
            panel.Children.Add(media);
            panel.Children.Add(sort);
            panel.Children.Add(orientation);

            var choices = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in ViewModel.AllSources)
            {
                var enabled = ViewModel.Sources.Any(s => s.Id == source.Id && s.IsEnabled);
                if (!enabled) continue;

                panel.Children.Add(new MenuFlyoutSeparator
                {
                    Margin = new Thickness(0, 10, 0, 0),
                });

                panel.Children.Add(new TextBlock
                {
                    Text = source.DisplayName,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 2, 0, 2),
                });

                if (source.Filters.Count == 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "No extra options for this site.",
                        FontSize = 12,
                        Opacity = 0.6,
                    });
                    continue;
                }

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                choices[source.Id] = map;

                foreach (var group in source.Filters)
                {
                    var options = group.Options.ToList();
                    var current = ViewModel.SourceFilters.TryGetValue(source.Id, out var existing)
                                  && existing.TryGetValue(group.Id, out var value)
                        ? value
                        : group.DefaultId;

                    var selected = options.FindIndex(o => string.Equals(o.Id, current, StringComparison.OrdinalIgnoreCase));
                    if (selected < 0) selected = Math.Max(0, options.FindIndex(o => o.Id == group.DefaultId));

                    var combo = new ComboBox
                    {
                        Header = group.Label,
                        ItemsSource = options.Select(o => o.Label).ToList(),
                        SelectedIndex = selected,
                        MinWidth = 300,
                    };

                    map[group.Id] = options[selected].Id;

                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedIndex >= 0 && combo.SelectedIndex < options.Count)
                        {
                            map[group.Id] = options[combo.SelectedIndex].Id;
                        }
                    };

                    panel.Children.Add(combo);
                }
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Filters",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 520,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "Apply",
                SecondaryButtonText = "Reset",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.SetFiltersAsync(
                    sort.SelectedIndex,
                    orientation.SelectedIndex,
                    media.SelectedIndex,
                    choices);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ViewModel.SetFiltersAsync(0, 0, 0, new Dictionary<string, Dictionary<string, string>>());
            }
        }

        private void ClosePreview_Click(object sender, RoutedEventArgs e) => ViewModel.ClosePreview();

        private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            CardHover.Show(sender, 1);

            if ((sender as FrameworkElement)?.Tag is HomeTile tile)
            {
                tile.IsHovered = true;
            }
        }

        private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            CardHover.Show(sender, 0);

            if ((sender as FrameworkElement)?.Tag is HomeTile tile)
            {
                tile.IsHovered = false;
            }
        }

        /// <summary>
        /// The small preview video must never make a sound: it is wallpaper, not
        /// a video player. It loops until the mouse leaves.
        /// </summary>
        private void PreviewPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not MediaPlayerElement element) return;

            element.RegisterPropertyChangedCallback(
                MediaPlayerElement.SourceProperty,
                (s, _) =>
                {
                    if (s is not MediaPlayerElement playerElement) return;
                    if (playerElement.MediaPlayer is not { } player) return;

                    player.IsMuted = true;
                    player.IsLoopingEnabled = true;
                    player.Play();
                });
        }

        private async void Favourite_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not HomeTile tile) return;

            await ViewModel.ToggleFavouriteAsync(tile);
        }

        private async void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            ViewModel.SearchText = args.QueryText;
            await ViewModel.RefreshAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // The page is cached, so this is not a real close. Only the full
            // preview is dismissed; the feed stays exactly where it was.
            ViewModel.OnClosed();
        }
    }
}
