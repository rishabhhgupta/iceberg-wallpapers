using Lively.Discovery.Theming;
using Microsoft.UI.Xaml.Media;

namespace Lively.UI.WinUI.Services
{
    /// <summary>
    /// Turns an atmosphere palette into brushes for the window. Shared by the
    /// pages so the whole app drifts with the time of day instead of only Home.
    /// </summary>
    public static class PaletteBrushes
    {
        public static LinearGradientBrush Background(Palette palette) => new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = ToColor(palette.BackgroundTop), Offset = 0 },
                new GradientStop { Color = ToColor(palette.BackgroundBottom), Offset = 1 },
            },
        };

        public static SolidColorBrush Text(Palette palette) =>
            new(ToColor(palette.TextPrimary));

        public static Windows.UI.Color ToColor(uint argb) =>
            Windows.UI.Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
    }
}
