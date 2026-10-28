using System;
using Lively.Common.Services;
using Lively.UI.WinUI.Services;
using Lively.UI.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Lively.UI.WinUI.Views.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsPage()
        {
            this.InitializeComponent();

            ViewModel = new SettingsViewModel(
                App.Services.GetRequiredService<IcebergService>(),
                App.Services.GetRequiredService<IMainNavigator>());

            this.DataContext = ViewModel;
            this.Loaded += SettingsPage_Loaded;
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = ex.Message;
            }
        }
    }
}
