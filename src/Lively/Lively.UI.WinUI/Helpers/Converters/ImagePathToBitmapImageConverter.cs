using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;

namespace Lively.UI.WinUI.Helpers.Converters
{
    /// <summary>
    /// Loads a library picture at a small size. Library cards are only a few
    /// hundred pixels wide, so decoding a full-resolution wallpaper wastes
    /// memory and makes scrolling rough.
    /// </summary>
    public class ImagePathToBitmapImageConverter : IValueConverter
    {
        public int DecodePixelWidth { get; set; } = 544;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            try
            {
                return new BitmapImage
                {
                    DecodePixelWidth = DecodePixelWidth,
                    UriSource = new Uri(path, UriKind.Absolute),
                };
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return (value as BitmapImage)?.UriSource?.LocalPath;
        }
    }
}
