using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Lively.UI.WinUI.Services
{
    /// <summary>
    /// Puts a picture on the Windows lock screen.
    ///
    /// Windows offers two ways to do this. The personalisation helper is the
    /// polite one: it returns false instead of throwing when a policy forbids
    /// the change. The LockScreen API is the older fallback. Both are tried,
    /// and a failure is reported to the caller rather than crashing the app.
    /// </summary>
    public static class LockScreenService
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public static async Task<bool> SetAsync(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return false;
            }

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask().ConfigureAwait(false);

                if (UserProfilePersonalizationSettings.IsSupported())
                {
                    try
                    {
                        var settings = UserProfilePersonalizationSettings.Current;
                        if (await settings.TrySetLockScreenImageAsync(file))
                        {
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Lock screen (personalisation): {ex.Message}");
                    }
                }

                // Older builds and some managed machines only answer to this one.
                await LockScreen.SetImageFileAsync(file).AsTask().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                // UnauthorizedAccess, group policy and unsupported formats all
                // land here. The caller turns this into a plain explanation.
                Logger.Warn($"Lock screen could not be changed: {ex.Message}");
                return false;
            }
        }
    }
}
