using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Lively.Common.Helpers
{
    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);
        [return: MarshalAs(UnmanagedType.U4)]
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, IntPtr rect);
        void SetBackgroundColor(uint color);
        [return: MarshalAs(UnmanagedType.U4)]
        uint GetBackgroundColor();
        void SetPosition(int position);
        [return: MarshalAs(UnmanagedType.I4)]
        int GetPosition();
        void SetSlideshow(IntPtr items);
        [return: MarshalAs(UnmanagedType.Interface)]
        IntPtr GetSlideshow();
        void SetSlideshowOptions(int options, uint slideshowTick);
        void GetSlideshowOptions(out int options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, int direction);
        int GetStatus();
        bool Enable();
    }

    public static class NativeWallpaperUtil
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int SystemParametersInfo(uint uAction, uint uParam, string lpvParam, uint fuWinIni);

        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDWININICHANGE = 0x02;

        /// <summary>
        /// Configures the native Windows desktop wallpaper so that on reboot / sign-in,
        /// Windows Explorer displays this picture immediately at boot (second 0).
        /// </summary>
        public static bool SetDesktopWallpaper(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return false;
            }

            bool success = false;

            // 1. Switch Windows Explorer out of Slideshow or Spotlight mode to Picture mode (0)
            try
            {
                using var wpKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");
                if (wpKey != null)
                {
                    wpKey.SetValue("BackgroundType", 0, RegistryValueKind.DWord);
                }

                using var themeKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes");
                if (themeKey != null)
                {
                    themeKey.SetValue("WallpaperSetFromTheme", 0, RegistryValueKind.DWord);
                }
            }
            catch
            {
                // Ignore registry permission errors if any
            }

            // 2. Clean and synchronize Custom.theme if present (prevent old slideshow/spotlight from being restored at boot)
            try
            {
                string localThemeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Themes");
                string customThemePath = Path.Combine(localThemeDir, "Custom.theme");
                if (File.Exists(customThemePath))
                {
                    string text = File.ReadAllText(customThemePath);
                    text = Regex.Replace(text, @"Wallpaper=.*", "Wallpaper=" + imagePath);
                    text = Regex.Replace(text, @"\[Slideshow\d*\].*?(?=(\[|$))", "", RegexOptions.Singleline);
                    File.WriteAllText(customThemePath, text, Encoding.Unicode);
                }
            }
            catch
            {
                // Ignore theme file access errors
            }

            // 3. Synchronize both TranscodedWallpaper caches (Roaming and Local)
            try
            {
                string roamingThemeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes");
                Directory.CreateDirectory(roamingThemeDir);
                File.Copy(imagePath, Path.Combine(roamingThemeDir, "TranscodedWallpaper"), true);

                string localThemeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Themes");
                if (Directory.Exists(localThemeDir))
                {
                    File.Copy(imagePath, Path.Combine(localThemeDir, "TranscodedWallpaper"), true);
                }
            }
            catch
            {
                // Ignore copy errors
            }

            // 4. Set wallpaper via IDesktopWallpaper COM interface
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD"));
                if (type != null)
                {
                    var dw = (IDesktopWallpaper)Activator.CreateInstance(type);
                    if (dw != null)
                    {
                        dw.SetSlideshow(IntPtr.Zero);
                        dw.SetWallpaper(null, imagePath);
                        dw.SetPosition(4); // DWPOS_FILL
                        success = true;
                    }
                }
            }
            catch
            {
                // Fall back to SystemParametersInfo
            }

            // 5. Broadcast change via Win32 SystemParametersInfo
            try
            {
                int result = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
                if (result != 0)
                {
                    success = true;
                }
            }
            catch
            {
                // Ignore
            }

            return success;
        }
    }
}
