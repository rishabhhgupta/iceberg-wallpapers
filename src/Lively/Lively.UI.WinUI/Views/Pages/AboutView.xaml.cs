using System;
using Lively.UI.Shared.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Lively.UI.WinUI.Views.Pages
{
    /// <summary>
    /// What Iceberg is, which version this is, and the people it stands on.
    /// </summary>
    public sealed partial class AboutView : Page
    {
        public AboutView()
        {
            this.InitializeComponent();
            this.DataContext = App.Services.GetRequiredService<AboutViewModel>();

            ProjectLink.NavigateUri = new Uri("https://github.com/rishabhhgupta/iceberg-wallpapers");
            LicenseLink.NavigateUri = new Uri("https://www.gnu.org/licenses/gpl-3.0.html");
            PrivacyLink.NavigateUri = new Uri("https://github.com/rishabhhgupta/iceberg-wallpapers/blob/main/PRIVACY.md");
        }
    }
}
