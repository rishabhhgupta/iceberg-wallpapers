using Lively.Common.Helpers.Pinvoke;
using Lively.Common.Services;
using Lively.Grpc.Client;
using Lively.Models;
using Lively.Models.Enums;
using Lively.UI.Shared.ViewModels;
using Lively.UI.WinUI.Extensions;
using Lively.UI.WinUI.Services;
using Lively.UI.WinUI.Views.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIEx;

namespace Lively.UI.WinUI
{
    public sealed partial class MainWindow : WindowEx
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly IDesktopCoreClient desktopCore;
        private readonly IUserSettingsClient userSettings;
        private readonly LibraryViewModel libraryVm;
        private readonly AppUpdateViewModel appUpdateVm;
        private readonly IDialogService dialogService;
        private readonly ICommandsClient commands;
        private readonly IMainNavigator navigator;
        private readonly IResourceService i18n;
        private bool fitted;
        private bool sessionEnding;
        private SubclassProc? closeGuard;

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc callback, UIntPtr id, IntPtr data);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        public MainWindow(IDesktopCoreClient desktopCore,
            MainViewModel mainVm,
            IDialogService dialogService,
            ICommandsClient commands,
            IUserSettingsClient userSettings,
            LibraryViewModel libraryVm,
            AppUpdateViewModel appUpdateVm,
            IResourceService i18n,
            IMainNavigator navigator)
        {
            this.desktopCore = desktopCore;
            this.libraryVm = libraryVm;
            this.userSettings = userSettings;
            this.dialogService = dialogService;
            this.commands = commands;
            this.appUpdateVm = appUpdateVm;
            this.navigator = navigator;
            this.i18n = i18n;

            this.InitializeComponent();
            Root.DataContext = mainVm;
            navigator.RootFrame = Root;
            navigator.Frame = contentFrame;
            this.SystemBackdrop = new MicaBackdrop();

            // Back walks the real history instead of always jumping Home.
            navView.BackRequested += NavView_BackRequested;
            contentFrame.Navigated += ContentFrame_Navigated;

            // A window taller than the screen is worse than a smaller one.
            Root.Loaded += (_, _) => FitToScreen();
            this.Activated += MainWindow_FirstActivated;

            // Ref: https://learn.microsoft.com/en-us/windows/apps/develop/title-bar?tabs=wasdk
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = this.AppWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonForegroundColor = ((SolidColorBrush)App.Current.Resources["WindowCaptionForeground"]).Color;

                AppTitleBar.Loaded += AppTitleBar_Loaded;
                AppTitleBar.SizeChanged += AppTitleBar_SizeChanged;
                this.Activated += MainWindow_Activated;
            }
            else
            {
                AppTitleBar.Visibility = Visibility.Collapsed;
                this.UseImmersiveDarkModeEx(userSettings.Settings.ApplicationTheme == AppTheme.Dark);
            }

            desktopCore.WallpaperChanged += DesktopCore_WallpaperChanged;
            _ = StdInListener();

            // Windows sends WM_QUERYENDSESSION before a shutdown. Remember it so
            // the close can go through instead of becoming a minimise.
            closeGuard = CloseGuardProc;
            SetWindowSubclass(this.GetWindowHandleEx(), closeGuard, (UIntPtr)1, IntPtr.Zero);
        }

        private void MainWindow_FirstActivated(object sender, WindowActivatedEventArgs args) =>
            FitToScreen();

        private void FitToScreen()
        {
            if (fitted || contentFrame.XamlRoot is not { } root)
            {
                return;
            }

            fitted = true;
            this.Activated -= MainWindow_FirstActivated;

            var scale = root.RasterizationScale <= 0 ? 1 : root.RasterizationScale;
            var area = Microsoft.UI.Windowing.DisplayArea
                .GetFromWindowId(this.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary)
                .WorkArea;

            var maxWidth = (area.Width / scale) - 32;
            var maxHeight = (area.Height / scale) - 32;

            if (this.Width <= maxWidth && this.Height <= maxHeight)
            {
                return;
            }

            this.Width = Math.Max(Math.Min(this.Width, maxWidth), this.MinWidth);
            this.Height = Math.Max(Math.Min(this.Height, maxHeight), this.MinHeight);
            this.CenterOnScreen();
        }

        private void DesktopCore_WallpaperChanged(object sender, EventArgs e)
        {
            _ = this.DispatcherQueue.TryEnqueue(() =>
            {
                // If its duplicate mode fire the animation more than once.
                if (userSettings.Settings.WallpaperArrangement != WallpaperArrangement.duplicate || desktopCore.Wallpapers.Count < 2)
                    activeWallpaperOffsetAnimation.Start();
            });
        }

        // This is actually WindowClosing, called before window closed.
        // Issue: https://github.com/microsoft/microsoft-ui-xaml/issues/5454
        private async void Window_Closed(object sender, WindowEventArgs args)
        {
            if (userSettings.Settings.IsFirstRun)
            {
                args.Handled = true;

                await dialogService.ShowWaitDialogAsync(new TrayMenuHelp(), 4);
                userSettings.Settings.IsFirstRun = false;
                userSettings.Save<SettingsModel>();

                this.Close();
            }
            else if (userSettings.Settings.IsUpdatedNotify)
            {
                args.Handled = true;

                userSettings.Settings.IsUpdatedNotify = false;
                userSettings.Save<SettingsModel>();

                this.Close();
            }
            else if (libraryVm.IsWorking || appUpdateVm.IsUpdateDownloading)
            {
                args.Handled = true;

                //Option 1: Show user prompt with choice to cancel.
                var result = await dialogService.ShowDialogAsync(i18n.GetString("TextConfirmCancel/Text"),
                                                            i18n.GetString("TitleDownloadProgress/Text"),
                                                            i18n.GetString("TextYes"),
                                                            i18n.GetString("TextWait/Text"),
                                                            false);
                if (result == DialogResult.primary)
                {
                    appUpdateVm.CancelCommand.Execute(null);
                    libraryVm.CancelAllDownloads();
                    libraryVm.IsBusy = true;

                    await Task.Delay(1500);
                    this.Close();
                }

                //Option 2: Keep UI client running and close after work completed.
                //contentFrame.Visibility = Visibility.Collapsed; //drop resource usage.
                //NativeMethods.ShowWindow(this.GetWindowHandleEx(), (uint)NativeMethods.SHOWWINDOW.SW_HIDE);
            }
            else if (dialogService.IsWorking)
            {
                // Wait for user to close the dialog and try again manually.
                args.Handled = true;
            }
            else if (!sessionEnding && RotationWantsToKeepRunning())
            {
                // Rotation runs inside this process. Closing the window to the
                // taskbar keeps wallpapers changing; the tray icon brings the
                // window back. Turning rotation off makes Close exit again.
                args.Handled = true;
                NativeMethods.ShowWindow(this.GetWindowHandleEx(), (uint)NativeMethods.SHOWWINDOW.SW_HIDE);
            }
            else
            {
                await commands.SaveRectUIAsync();
                App.ShutDown();
            }
        }

        private static bool RotationWantsToKeepRunning()
        {
            return App.Services.GetService(typeof(IcebergService)) is IcebergService iceberg &&
                iceberg.Settings.Current.RotationEnabled;
        }

        private IntPtr CloseGuardProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
        {
            if (message == 0x0011) // WM_QUERYENDSESSION
            {
                sessionEnding = true;
            }

            return DefSubclassProc(hWnd, message, wParam, lParam);
        }

        /// <summary>
        /// std I/O redirect.
        /// </summary>
        private async Task StdInListener()
        {
            try
            {
                await Task.Run(async () =>
                {
                    while (true)
                    {
                        var msg = await Console.In.ReadLineAsync();
                        if (string.IsNullOrEmpty(msg))
                        {
                            //When the redirected stream is closed, a null line is sent to the event handler. 
                            break;
                        }
                        var args = msg.Split(' ');
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (args[0].Equals("WM", StringComparison.OrdinalIgnoreCase))
                            {
                                if (args[1].Equals("SHOW", StringComparison.OrdinalIgnoreCase))
                                {
                                    contentFrame.Visibility = Visibility.Visible; //undo drop resource usage.
                                    NativeMethods.ShowWindow(this.GetWindowHandleEx(), (uint)NativeMethods.SHOWWINDOW.SW_RESTORE);
                                    Activate();
                                    NativeMethods.SetForegroundWindow(this.GetWindowHandleEx());
                                }
                            }
                            else if (args[0].Equals("LM", StringComparison.OrdinalIgnoreCase))
                            {
                                if (args[1].Equals("SHOWBUSY", StringComparison.OrdinalIgnoreCase))
                                {
                                    libraryVm.IsBusy = true;
                                }
                                else if (args[1].Equals("HIDEBUSY", StringComparison.OrdinalIgnoreCase))
                                {
                                    libraryVm.IsBusy = false;
                                }
                                else if (args[1].Equals("SHOWCUSTOMISEPANEL", StringComparison.OrdinalIgnoreCase))
                                {
                                    _ = dialogService.ShowControlPanelDialogAsync();
                                }
                                else if (args[1].Equals("SHOWAPPUPDATEPAGE", StringComparison.OrdinalIgnoreCase))
                                {
                                    navigator.NavigateTo(ContentPageType.appupdate);
                                }
                            }
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
        {
            this.SetDragRegionForCustomTitleBar(RightPaddingColumn,
                LeftPaddingColumn,
                IconColumn,
                TitleColumn,
                LeftDragColumn,
                RightDragColumn,
                SearchColumn,
                TitleTextBlock,
                AppTitleBar);
        }

        private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (this.AppWindow.TitleBar.ExtendsContentIntoTitleBar)
                this.SetDragRegionForCustomTitleBar(RightPaddingColumn,
                         LeftPaddingColumn,
                         IconColumn,
                         TitleColumn,
                         LeftDragColumn,
                         RightDragColumn,
                         SearchColumn,
                         TitleTextBlock,
                         AppTitleBar);
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            TitleTextBlock.Foreground = args.WindowActivationState == WindowActivationState.Deactivated ? 
                (SolidColorBrush)App.Current.Resources["WindowCaptionForegroundDisabled"] : (SolidColorBrush)App.Current.Resources["WindowCaptionForeground"];
        }

        private void WebViewRequired_InfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => sender.IsOpen = false;

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (contentFrame.CanGoBack)
            {
                contentFrame.GoBack();
            }
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            // The arrow is hidden on Home because there is nothing behind it,
            // and shown everywhere else only when there is somewhere to go back to.
            navView.IsBackButtonVisible = contentFrame.CanGoBack && e.SourcePageType != typeof(HomeView)
                ? NavigationViewBackButtonVisible.Visible
                : NavigationViewBackButtonVisible.Collapsed;
        }

        /// <summary>One wallpaper, right now, from the enabled sources.</summary>
        private async void Surprise_Click(object sender, RoutedEventArgs e)
        {
            if (App.Services.GetService(typeof(IcebergService)) is not IcebergService iceberg) return;

            try
            {
                await iceberg.ApplyNextAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }
}
