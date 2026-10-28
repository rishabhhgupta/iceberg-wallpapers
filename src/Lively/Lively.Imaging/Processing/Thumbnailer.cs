using SkiaSharp;

namespace Lively.Imaging.Processing;

/// <summary>
/// Decodes and downscales images with SkiaSharp.
///
/// Why not the built-in decoder: a 6000 x 4000 photo decoded at full size is
/// roughly 96 MB of memory. Scrolling a grid of those is how wallpaper apps run
/// out of memory. Decoding straight to the tile width costs a fraction of that.
/// </summary>
public static class Thumbnailer
{
    public sealed record DecodedImage(byte[] Bytes, int Width, int Height);

    /// <summary>Reads dimensions without decoding the whole image.</summary>
    public static (int Width, int Height)? Dimensions(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(source));
            if (codec is null) return null;
            return (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Downscales to <paramref name="targetWidth"/>. Images already smaller are
    /// returned untouched: upscaling a thumbnail only makes it blurry and larger.
    /// Returns null when the bytes are not a decodable image.
    /// </summary>
    public static byte[]? Resize(byte[] source, int targetWidth, int quality = 82)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));

        try
        {
            using var bitmap = SKBitmap.Decode(source);
            if (bitmap is null) return null;

            if (bitmap.Width <= targetWidth) return source;

            var scale = (double)targetWidth / bitmap.Width;
            var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

            var info = new SKImageInfo(targetWidth, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var resized = bitmap.Resize(info, SKFilterQuality.Medium);
            if (resized is null) return null;

            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
            return encoded?.ToArray();
        }
        catch (Exception)
        {
            // Corrupt or unsupported file: caller shows a placeholder.
            return null;
        }
    }

    /// <summary>Resizes and reports the resulting dimensions in one decode.</summary>
    public static DecodedImage? Create(byte[] source, int targetWidth, int quality = 82)
    {
        var bytes = Resize(source, targetWidth, quality);
        if (bytes is null) return null;

        var dimensions = Dimensions(bytes);
        if (dimensions is null) return null;

        return new DecodedImage(bytes, dimensions.Value.Width, dimensions.Value.Height);
    }
}
