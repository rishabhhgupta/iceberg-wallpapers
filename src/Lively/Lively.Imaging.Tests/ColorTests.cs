using Lively.Discovery.Theming;
using Lively.Imaging.Processing;
using SkiaSharp;
using Xunit;

namespace Lively.Imaging.Tests;

public class DominantColorTests
{
    private static byte[] Solid(byte r, byte g, byte b, int size = 64)
    {
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(r, g, b));
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void A_red_photo_produces_a_red_accent()
    {
        var color = DominantColor.Extract(Solid(200, 40, 40));

        Assert.NotNull(color);
        var (h, s, _) = ColorMath.ToHsl(color!.Value);

        Assert.True(h < 20 || h > 340, $"hue was {h}");
        Assert.True(s > 0.2);
    }

    [Fact]
    public void A_blue_photo_produces_a_blue_accent()
    {
        var color = DominantColor.Extract(Solid(30, 70, 200));

        Assert.NotNull(color);
        var (h, _, _) = ColorMath.ToHsl(color!.Value);

        Assert.InRange(h, 190, 260);
    }

    [Fact]
    public void Garbage_is_rejected_rather_than_returning_a_made_up_colour()
    {
        Assert.Null(DominantColor.Extract(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void A_fully_transparent_image_has_no_colour_to_give()
    {
        using var bitmap = new SKBitmap(32, 32);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        Assert.Null(DominantColor.Extract(data.ToArray()));
    }

    [Fact]
    public void The_extracted_colour_is_always_opaque()
    {
        var color = DominantColor.Extract(Solid(120, 120, 120));

        if (color is { } value)
        {
            Assert.Equal(255, ColorMath.A(value));
        }
    }
}
