using Lively.Discovery.Theming;
using SkiaSharp;

namespace Lively.Imaging.Processing;

/// <summary>
/// Finds the colour a photo is "about", so the app can tint itself to match the
/// current wallpaper.
///
/// Not a full k-means palette: that is slow and the extra accuracy is invisible
/// behind a 20% tint. Instead the image is shrunk to a thumbnail, near-black and
/// near-white pixels are ignored (they carry no colour), and the rest is averaged
/// in proportion to how saturated it is. The result is nudged towards saturation
/// so a muted photo still produces a usable accent.
/// </summary>
public static class DominantColor
{
    private const int SampleSize = 32;

    /// <summary>Returns 0xAARRGGBB, or null when the bytes are not an image.</summary>
    public static uint? Extract(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        try
        {
            using var source = SKBitmap.Decode(imageBytes);
            if (source is null) return null;

            var info = new SKImageInfo(SampleSize, SampleSize, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = source.Resize(info, SKFilterQuality.Low);
            if (bitmap is null) return null;

            double weight = 0, r = 0, g = 0, b = 0;

            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.Alpha < 32) continue;

                    var color = ColorMath.Pack(255, pixel.Red, pixel.Green, pixel.Blue);
                    var (_, saturation, lightness) = ColorMath.ToHsl(color);

                    // Skip near-black and near-white: they are shadows and
                    // highlights, not the colour of the scene.
                    if (lightness < 0.12 || lightness > 0.92) continue;

                    // Saturated pixels say more about the mood than grey ones.
                    var w = 0.15 + (saturation * saturation);

                    weight += w;
                    r += pixel.Red * w;
                    g += pixel.Green * w;
                    b += pixel.Blue * w;
                }
            }

            if (weight <= 0) return null;

            var average = ColorMath.Pack(
                255,
                (byte)Math.Round(r / weight),
                (byte)Math.Round(g / weight),
                (byte)Math.Round(b / weight));

            var (h, s, l) = ColorMath.ToHsl(average);

            // A washed-out average makes a washed-out theme. Push it up, but
            // keep the brightness recognisable so a night photo stays deep.
            s = Math.Clamp(s * 1.35, 0.18, 0.85);
            l = Math.Clamp(l, 0.22, 0.72);

            return ColorMath.FromHsl(h, s, l);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
