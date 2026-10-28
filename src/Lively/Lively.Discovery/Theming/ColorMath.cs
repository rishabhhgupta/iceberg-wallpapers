namespace Lively.Discovery.Theming;

/// <summary>
/// Small colour helpers. Colours are packed as 0xAARRGGBB so the same values can
/// be handed to WinUI, a test, or a JSON file without conversion dance.
/// </summary>
public static class ColorMath
{
    public static byte A(uint c) => (byte)((c >> 24) & 0xFF);
    public static byte R(uint c) => (byte)((c >> 16) & 0xFF);
    public static byte G(uint c) => (byte)((c >> 8) & 0xFF);
    public static byte B(uint c) => (byte)(c & 0xFF);

    public static uint Pack(byte a, byte r, byte g, byte b) =>
        ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    /// <summary>Parses "#RRGGBB", "#AARRGGBB" or the same without the hash.</summary>
    public static uint Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        var value = hex.Trim().TrimStart('#');

        return value.Length switch
        {
            6 => 0xFF000000u | Convert.ToUInt32(value, 16),
            8 => Convert.ToUInt32(value, 16),
            _ => throw new FormatException($"'{hex}' is not a colour."),
        };
    }

    public static string ToHex(uint color) => $"#{color:X8}";

    /// <summary>Linear blend: t = 0 gives a, t = 1 gives b.</summary>
    public static uint Blend(uint a, uint b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Mix(byte x, byte y) => (byte)Math.Round(x + ((y - x) * t));

        return Pack(
            Mix(A(a), A(b)),
            Mix(R(a), R(b)),
            Mix(G(a), G(b)),
            Mix(B(a), B(b)));
    }

    /// <summary>Relative luminance per WCAG, 0 (black) to 1 (white).</summary>
    public static double Luminance(uint color)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(R(color))) + (0.7152 * Channel(G(color))) + (0.0722 * Channel(B(color)));
    }

    /// <summary>WCAG contrast ratio, 1 (identical) to 21 (black on white).</summary>
    public static double ContrastRatio(uint a, uint b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static bool IsDark(uint color) => Luminance(color) < 0.35;

    /// <summary>
    /// Picks black or white text, whichever reads better on the background.
    /// Used everywhere instead of hard-coding white, because a light "morning"
    /// theme with white text is unreadable.
    /// </summary>
    public static uint ReadableText(uint background)
    {
        var white = 0xFFFFFFFFu;
        var nearBlack = 0xFF101418u;
        return ContrastRatio(nearBlack, background) >= ContrastRatio(white, background) ? nearBlack : white;
    }

    /// <summary>
    /// Pushes a colour away from the background until it has at least
    /// <paramref name="minimum"/> contrast, so an accent never disappears.
    /// </summary>
    public static uint EnsureContrast(uint foreground, uint background, double minimum = 3.0)
    {
        if (ContrastRatio(foreground, background) >= minimum) return foreground;

        var target = IsDark(background) ? 0xFFFFFFFFu : 0xFF000000u;
        var result = foreground;

        for (var step = 1; step <= 10; step++)
        {
            result = Blend(foreground, target, step / 10.0);
            if (ContrastRatio(result, background) >= minimum) return result;
        }

        return target;
    }

    /// <summary>0 at grey, 1 at fully saturated.</summary>
    public static double Saturation(uint color)
    {
        var (_, s, _) = ToHsl(color);
        return s;
    }

    public static (double H, double S, double L) ToHsl(uint color)
    {
        var r = R(color) / 255.0;
        var g = G(color) / 255.0;
        var b = B(color) / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;

        if (Math.Abs(max - min) < 1e-9) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (Math.Abs(max - r) < 1e-9) h = ((g - b) / d) + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 1e-9) h = ((b - r) / d) + 2;
        else h = ((r - g) / d) + 4;

        return (h * 60.0, s, l);
    }

    public static uint FromHsl(double h, double s, double l, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s <= 0)
        {
            var v = (byte)Math.Round(l * 255);
            return Pack(alpha, v, v, v);
        }

        var c = (1 - Math.Abs((2 * l) - 1)) * s;
        var x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
        var m = l - (c / 2);

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Pack(
            alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
