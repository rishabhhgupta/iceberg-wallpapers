using Lively.Common;
using Lively.Common.Factories;
using Lively.Common.Helpers;
using Lively.Common.Services;
using Lively.Core;
using Lively.Core.Display;
using Lively.Core.Suspend;
using Lively.Models;
using Lively.Models.Enums;
using Lively.Models.Services;
using Lively.Themes;
using Lively.Views;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;

namespace Lively.Services
{
    public class Systray : ISystray
    {
        private readonly Dictionary<TrayMenuItem, ToolStripMenuItem> trayMenuItems = [];
        private readonly static Icon appIcon = Properties.Icons.appicon;
        private readonly static Icon appIconGrey = Properties.Icons.appicon_grey;
        private readonly NotifyIcon notifyIcon = new();
        private readonly Random rng = new();
        private bool disposedValue;

        private readonly IRunnerService runner;
        private readonly IResourceService i18n;
        private readonly IDesktopCore desktopCore;
        private readonly IDisplayManager displayManager;
        private readonly IUserSettingsService userSettings;
        private readonly IAppUpdaterService appUpdater;
        private readonly IWallpaperLibraryFactory wallpaperLibraryFactory;

        private AppTheme? currentTheme = null;

        public Systray(IResourceService i18n,
            IRunnerService runner,
            IUserSettingsService userSettings,
            IDesktopCore desktopCore,
            IAppUpdaterService appUpdater,
            IDisplayManager displayManager,
            IPlayback playbackMonitor,
            IWindowService windowService,
            IWallpaperLibraryFactory wallpaperLibraryFactory)
        {
            this.i18n = i18n;
            this.runner = runner;
            this.desktopCore = desktopCore;
            this.userSettings = userSettings;
            this.displayManager = displayManager;
            this.appUpdater = appUpdater;
            this.wallpaperLibraryFactory = wallpaperLibraryFactory;

            // NotifyIcon Issue: "The root Visual of a VisualTarget cannot have a parent.."
            // Ref: https://stackoverflow.com/questions/28833702/wpf-notifyicon-crash-on-first-run-the-root-visual-of-a-visualtarget-cannot-hav/29116917
            System.Windows.Controls.ToolTip tt = new System.Windows.Controls.ToolTip();
            tt.IsOpen = true;
            tt.IsOpen = false;

            // Properties
            notifyIcon.DoubleClick += (s, args) => runner.ShowUI();
            notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            notifyIcon.Icon = appIcon;
            notifyIcon.Text = "Iceberg Wallpapers";
            notifyIcon.Visible = userSettings.Settings.SysTrayIcon;
            var toolStripColor = Color.FromArgb(55, 55, 55);
            notifyIcon.ContextMenuStrip = new ContextMenuStrip
            {
                Padding = new Padding(0),
                Margin = new Padding(0),
                //Font = new System.Drawing.Font("Segoe UI", 10F),
            };
            SetTheme(userSettings.Settings.ApplicationTheme);
            notifyIcon.ContextMenuStrip.Opening += ContextMenuStrip_Opening;

            // Menu registrations
            var openAppTrayMenu = new ToolStripMenuItem(GetMenuItemString(TrayMenuItem.openApp), Properties.Icons.icons8_application_window_96);
            openAppTrayMenu.Click += (s, e) => runner.ShowUI();
            notifyIcon.ContextMenuStrip.Items.Add(openAppTrayMenu);
            trayMenuItems[TrayMenuItem.openApp] = openAppTrayMenu;

            notifyIcon.ContextMenuStrip.Items.Add(CreateToolStripSeparator(toolStripColor));

            var pauseTrayMenu = new ToolStripMenuItem(GetMenuItemString(TrayMenuItem.pauseWallpaper), null);
            pauseTrayMenu.Click += (s, e) =>
            {
                playbackMonitor.WallpaperPlaybackPolicy = playbackMonitor.WallpaperPlaybackPolicy == PlaybackPolicy.automatic ? PlaybackPolicy.alwaysPaused : PlaybackPolicy.automatic;
            };
            notifyIcon.ContextMenuStrip.Items.Add(pauseTrayMenu);
            trayMenuItems[TrayMenuItem.pauseWallpaper] = pauseTrayMenu;

            var changeWallpaperTrayMenu = new ToolStripMenuItem(GetMenuItemString(TrayMenuItem.changeWallpaper), null);
            changeWallpaperTrayMenu.Click += async (s, e) =>
            {
                try
                {
                    runner.RotateWallpaperNow();
                }
                catch
                {
                    await SetRandomWallpapers();
                }
            };
            notifyIcon.ContextMenuStrip.Items.Add(changeWallpaperTrayMenu);
            trayMenuItems[TrayMenuItem.changeWallpaper] = changeWallpaperTrayMenu;

            notifyIcon.ContextMenuStrip.Items.Add(CreateToolStripSeparator(toolStripColor));

            var updateTrayMenu = new ToolStripMenuItem(GetMenuItemString(TrayMenuItem.updateApp), null);
            updateTrayMenu.Click += (s, e) => LinkUtil.OpenBrowser("ms-windows-store://pdp/?productid=9P8TKTPF87V4");
            notifyIcon.ContextMenuStrip.Items.Add(updateTrayMenu);
            trayMenuItems[TrayMenuItem.updateApp] = updateTrayMenu;

            var reportBugTrayMenu = new ToolStripMenuItem(GetMenuItemString(TrayMenuItem.reportBug), Properties.Icons.icons8_website_bug_96);
            reportBugTrayMenu.Click += (s, e) => LinkUtil.OpenBrowser("https://github.com/rishabhhgupta/iceberg-wallpapers/issues");
            notifyIcon.ContextMenuStrip.Items.Add(reportBugTrayMenu);
            trayMenuItems[TrayMenuItem.reportBug] = reportBugTrayMenu;

            notifyIcon.ContextMenuStrip.Items.Add(CreateToolStripSeparator(toolStripColor));

            var exitAppTrayMenu = new ToolStripMenuItem(GetMenuItemString(TrayMenuItem.exitApp), Properties.Icons.icons8_close_96);
            exitAppTrayMenu.Click += (s, e) => App.QuitApp();
            notifyIcon.ContextMenuStrip.Items.Add(exitAppTrayMenu);
            trayMenuItems[TrayMenuItem.exitApp] = exitAppTrayMenu;

            UpdatePauseMenuState();

            playbackMonitor.PlaybackPolicyChanged += Playback_PlaybackStateChanged;
            desktopCore.WallpaperChanged += DesktopCore_WallpaperChanged;
            appUpdater.UpdateChecked += (s, e) => { SetUpdateMenu(e.UpdateStatus); };
            i18n.CultureChanged += I18n_CultureChanged;
        }

        public void Visibility(bool visible)
        {
            notifyIcon.Visible = visible;
        }

        public void ShowBalloonNotification(int timeout, string title, string msg)
        {
            notifyIcon.ShowBalloonTip(timeout, title, msg, ToolTipIcon.None);
        }

        public void SetTheme(AppTheme theme)
        {
            theme = theme == AppTheme.Auto ? ThemeUtil.GetWindowsTheme() : theme;
            if (currentTheme != null && currentTheme == theme)
                return;

            switch (theme)
            {
                case AppTheme.Auto: // not applicable
                case AppTheme.Dark:
                    {
                        notifyIcon.ContextMenuStrip.ForeColor = Color.AliceBlue;
                        ToolStripManager.Renderer = new ToolStripRendererDark();
                    }
                    break;
                case AppTheme.Light:
                    {
                        notifyIcon.ContextMenuStrip.ForeColor = Color.Black;
                        ToolStripManager.Renderer = new ToolStripRendererLight();
                    }
                    break;
            }
            currentTheme = theme;
        }


        /// <summary>
        /// Fix for traymenu opening to the nearest screen instead of the screen in which cursor is located.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ContextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdatePauseMenuState();
            ContextMenuStrip menuStrip = sender as ContextMenuStrip;
            if (displayManager.IsMultiScreen())
            {
                //Finding screen in which cursor is present.
                var screen = displayManager.GetDisplayMonitorFromPoint(Cursor.Position);

                var mousePos = Cursor.Position;
                //Converting global cursor pos. to given screen pos.
                mousePos.X += -1 * screen.Bounds.X;
                mousePos.Y += -1 * screen.Bounds.Y;

                //guessing taskbar pos. based on cursor pos. on display.
                bool isLeft = mousePos.X < screen.Bounds.Width * .5;
                bool isTop = mousePos.Y < screen.Bounds.Height * .5;

                //menu popup pos. rule.
                if (isLeft && isTop)
                {
                    //not possible?
                    menuStrip.Show(Cursor.Position, ToolStripDropDownDirection.Default);
                }
                if (isLeft && !isTop)
                {
                    menuStrip.Show(Cursor.Position, ToolStripDropDownDirection.AboveRight);
                }
                else if (!isLeft && isTop)
                {
                    menuStrip.Show(Cursor.Position, ToolStripDropDownDirection.BelowLeft);
                }
                else if (!isLeft && !isTop)
                {
                    menuStrip.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
                }
            }
            else
            {
                menuStrip.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
            }
        }

        private void Playback_PlaybackStateChanged(object sender, PlaybackPolicy e)
        {
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new ThreadStart(delegate
            {
                var isPaused = e == PlaybackPolicy.alwaysPaused;
                notifyIcon.Icon = isPaused ? appIconGrey : appIcon;
                trayMenuItems[TrayMenuItem.pauseWallpaper].Checked = isPaused;
            }));
        }

        private void DesktopCore_WallpaperChanged(object sender, EventArgs e)
        {
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new ThreadStart(delegate
            {
                UpdatePauseMenuState();
            }));
        }

        private void UpdatePauseMenuState()
        {
            if (trayMenuItems.TryGetValue(TrayMenuItem.pauseWallpaper, out var pauseMenu))
            {
                bool hasAnimated = desktopCore.Wallpapers.Any(x => x.Category != WallpaperType.picture);
                pauseMenu.Visible = hasAnimated;
                pauseMenu.Enabled = hasAnimated;
            }
        }

        private void SetUpdateMenu(AppUpdateStatus status)
        {
            if (!trayMenuItems.TryGetValue(TrayMenuItem.updateApp, out ToolStripMenuItem updateTrayMenu))
                return;

            switch (status)
            {
                case AppUpdateStatus.uptodate:
                    updateTrayMenu.Enabled = false;
                    updateTrayMenu.Text = i18n.GetString("TextUpdateUptodate");
                    break;
                case AppUpdateStatus.available:
                    updateTrayMenu.Enabled = true;
                    updateTrayMenu.Text = i18n.GetString("TextUpdateAvailable");
                    break;
                case AppUpdateStatus.invalid:
                    updateTrayMenu.Enabled = false;
                    updateTrayMenu.Text = "Fancy~";
                    break;
                case AppUpdateStatus.notchecked:
                    updateTrayMenu.Enabled = false;
                    updateTrayMenu.Text = i18n.GetString("TextUpdateChecking");
                    break;
                case AppUpdateStatus.error:
                    updateTrayMenu.Enabled = true;
                    updateTrayMenu.Text = i18n.GetString("TextupdateCheckFail");
                    break;
            }
        }

        private void I18n_CultureChanged(object sender, string e)
        {
            foreach (TrayMenuItem item in Enum.GetValues(typeof(TrayMenuItem)))
            {
                if (trayMenuItems.TryGetValue(item, out var menuItem))
                    menuItem.Text = GetMenuItemString(item);
            }
            SetUpdateMenu(appUpdater.Status);
        }

        /// <summary>
        /// Sets random library item as wallpaper.
        /// </summary>
        private async Task SetRandomWallpapers()
        {
            switch (userSettings.Settings.WallpaperArrangement)
            {
                case WallpaperArrangement.per:
                    {
                        var screenCount = displayManager.DisplayMonitors.Count;
                        var wallpapersRandom = GetRandomWallpaper().Take(screenCount);
                        var wallpapersCount = wallpapersRandom.Count();
                        if (wallpapersCount > 0)
                        {
                            for (int i = 0; i < screenCount; i++)
                            {
                                await desktopCore.SetWallpaperAsync(wallpapersRandom.ElementAt(i > wallpapersCount - 1 ? 0 : i), displayManager.DisplayMonitors[i]);
                            }
                        }
                    }
                    break;
                case WallpaperArrangement.span:
                case WallpaperArrangement.duplicate:
                    {
                        try
                        {
                            await desktopCore.SetWallpaperAsync(GetRandomWallpaper().First(), displayManager.PrimaryDisplayMonitor);
                        }
                        catch (InvalidOperationException)
                        {
                            //No wallpapers present.
                        }
                    }
                    break;
            }
        }

        private IEnumerable<LibraryModel> GetRandomWallpaper()
        {
            var dir = new List<string>();
            string[] folderPaths = {
                Path.Combine(userSettings.Settings.WallpaperDir, Constants.CommonPartialPaths.WallpaperInstallDir),
                Path.Combine(userSettings.Settings.WallpaperDir, Constants.CommonPartialPaths.WallpaperInstallTempDir)
            };
            for (int i = 0; i < folderPaths.Count(); i++)
            {
                try
                {
                    dir.AddRange(Directory.GetDirectories(folderPaths[i], "*", SearchOption.TopDirectoryOnly));
                }
                catch { /* TODO */ }
            }

            //Fisher-Yates shuffle
            int n = dir.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                var value = dir[k];
                dir[k] = dir[n];
                dir[n] = value;
            }

            for (int i = 0; i < dir.Count; i++)
            {
                LibraryModel libItem = null;
                try
                {
                    libItem = wallpaperLibraryFactory.CreateFromDirectory(dir[i]);
                }
                catch { }

                if (libItem != null)
                {
                    yield return libItem;
                }
            }
        }

        private static ToolStripSeparator CreateToolStripSeparator(Color color)
        {
            ToolStripSeparator separator = new ToolStripSeparator();
            separator.Paint += (s, e) =>
            {
                ToolStripSeparator stripSeparator = s as ToolStripSeparator;
                ContextMenuStrip menuStrip = stripSeparator.Owner as ContextMenuStrip;
                e.Graphics.FillRectangle(new SolidBrush(Color.Transparent), new Rectangle(0, 0, stripSeparator.Width, stripSeparator.Height));
                using var pen = new Pen(color, 1);
                e.Graphics.DrawLine(pen, new Point(23, stripSeparator.Height / 2), new Point(menuStrip.Width, stripSeparator.Height / 2));
            };
            return separator;
        }

        private string GetMenuItemString(TrayMenuItem item)
        {
            return item switch
            {
                TrayMenuItem.openApp => i18n.GetString("TextOpenLively"),
                TrayMenuItem.changeWallpaper => i18n.GetString("TextChangeWallpaper"),
                TrayMenuItem.reportBug => i18n.GetString("ReportBug/Header"),
                TrayMenuItem.exitApp => i18n.GetString("TextExit"),
                TrayMenuItem.pauseWallpaper => i18n.GetString("TextPauseWallpapers"),
                TrayMenuItem.updateApp => "Check for updates",
                _ => string.Empty,
            };
        }

        // Only used here
        private enum TrayMenuItem
        {
            openApp,
            changeWallpaper,
            reportBug,
            exitApp,
            pauseWallpaper,
            updateApp
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    notifyIcon.Visible = false;
                    notifyIcon?.Icon?.Dispose();
                    notifyIcon?.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~Systray()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
