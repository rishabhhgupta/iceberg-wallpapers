using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System.UserProfile;
using Lively.Common.Helpers;

namespace Lively.UI.WinUI.Services
{
    /// <summary>
    /// Sets the Windows native desktop wallpaper so that on computer restart / log-in,
    /// Windows Explorer displays this wallpaper immediately at boot (second 0)
    /// without waiting for the app or background players to start.
    /// </summary>
    public static class SystemWallpaperService
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public static async Task<bool> SetAsync(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return false;
            }

            bool success = false;

            // 1. Comprehensive native Windows configuration:
            //    - Sets BackgroundType = 0 (Picture mode) in Explorer
            //    - Sets WallpaperSetFromTheme = 0 in Themes
            //    - Cleans Custom.theme to prevent old slideshow/Spotlight at boot
            //    - Synchronizes TranscodedWallpaper in both Roaming and Local
            //    - Sets wallpaper via IDesktopWallpaper COM API
            //    - Calls SystemParametersInfo (Win32)
            try
            {
                success = NativeWallpaperUtil.SetDesktopWallpaper(imagePath);
                if (success)
                {
                    Logger.Info($"Native wallpaper set successfully: {imagePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"NativeWallpaperUtil failed: {ex.Message}");
            }

            // 2. Also try Windows.System.UserProfile.UserProfilePersonalizationSettings (Packaged API)
            //    when running interactively in foreground
            try
            {
                if (UserProfilePersonalizationSettings.IsSupported())
                {
                    var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask().ConfigureAwait(false);
                    var settings = UserProfilePersonalizationSettings.Current;
                    if (settings != null && await settings.TrySetWallpaperImageAsync(file))
                    {
                        Logger.Info($"System wallpaper set via UserProfilePersonalizationSettings: {imagePath}");
                        success = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Personalization API failed to set wallpaper: {ex.Message}");
            }

            return success;
        }
    }
}
