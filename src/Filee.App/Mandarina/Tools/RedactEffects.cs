// Mandarina: how the REDACT tools cover an area, for both the photo (Magick.NET, here) and the video (the same numbers
// feed FFmpeg's filters). What is covered must not be recoverable, so blur and pixelation are measured against the area,
// not in pixels (a 2 cm face and a 20 cm face must be equally unrecognisable whatever the size of the photo), and even
// the weakest setting is past what a person can read: tight around a line of text, a blur of 35 % of the line's height
// or blocks of 35 % of it are already illegible, and the weakest setting here is 40 %.

using Avalonia;
using Avalonia.Media;
using ImageMagick;

namespace Filee.App.Mandarina.Tools;

/// <summary>What replaces the covered area.</summary>
public enum RedactEffect
{
    /// <summary>A flat colour.</summary>
    Fill,

    /// <summary>Gaussian blur.</summary>
    Blur,

    /// <summary>Big square blocks, each the average colour of what it covers.</summary>
    Pixelate,
}

internal static class RedactEffects
{
    public const double MinStrength = 1;
    public const double MaxStrength = 100;
    public const double MinBlock = 4;
    public const double MaxBlock = 64;

    /// <summary>The weakest blur offered: this share of the area's shorter side (text is unreadable from about 0.35).</summary>
    private const double BlurFloor = 0.40;

    /// <summary>How much further the strongest blur goes (a smooth smear).</summary>
    private const double BlurSpan = 0.40;

    /// <summary>The smallest pixelation block offered: this share of the area's shorter side (text is unreadable from about 0.35).</summary>
    private const double BlockFloor = 0.40;

    /// <summary>How much further the largest block goes (one block across the shorter side).</summary>
    private const double BlockSpan = 0.60;

    /// <summary>The colours offered for a flat cover: black, white, Mandarina's orange and a mid grey.</summary>
    public static IReadOnlyList<Color> Swatches { get; } =
    [
        Color.FromRgb(0x00, 0x00, 0x00),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0xFF, 0x52, 0x00),
        Color.FromRgb(0x80, 0x80, 0x80),
    ];

    /// <summary>
    /// The standard deviation of the blur, in pixels, for an area of <paramref name="width"/> × <paramref name="height"/>:
    /// <paramref name="strength"/> (1..100) goes from 40 % to 80 % of the area's shorter side. Never under 1.5 px.
    /// </summary>
    public static double BlurSigma(double strength, double width, double height)
    {
        var step = (Math.Clamp(strength, MinStrength, MaxStrength) - MinStrength) / (MaxStrength - MinStrength);
        return Math.Max(1.5, Math.Min(width, height) * (BlurFloor + BlurSpan * step));
    }

    /// <summary>
    /// The side of a pixelation block, in pixels: <paramref name="block"/> (4..64) goes from 40 % of the area's shorter
    /// side to all of it (one block across). Never under 2 px.
    /// </summary>
    public static int BlockSize(double block, double width, double height)
    {
        var step = (Math.Clamp(block, MinBlock, MaxBlock) - MinBlock) / (MaxBlock - MinBlock);
        return (int)Math.Max(2, Math.Round(Math.Min(width, height) * (BlockFloor + BlockSpan * step)));
    }

    /// <summary>
    /// The whole pixels of <paramref name="area"/> inside a picture of <paramref name="width"/> × <paramref name="height"/>
    /// (rounded outwards so nothing at the edge of the area stays visible); empty when it lies outside.
    /// </summary>
    public static (int X, int Y, int Width, int Height) Snap(Rect area, double width, double height)
    {
        var left = (int)Math.Clamp(Math.Floor(area.X), 0, width);
        var top = (int)Math.Clamp(Math.Floor(area.Y), 0, height);
        var right = (int)Math.Clamp(Math.Ceiling(area.Right), 0, width);
        var bottom = (int)Math.Clamp(Math.Ceiling(area.Bottom), 0, height);
        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Covers <paramref name="area"/> of <paramref name="image"/> (full size, upright) in place.</summary>
    public static void Cover(MagickImage image, Rect area, RedactEffect effect, Color fill, double strength, double block)
    {
        var (x, y, width, height) = Snap(area, image.Width, image.Height);
        if (width <= 0 || height <= 0)
            return;

        if (effect == RedactEffect.Fill)
        {
            using var flat = new MagickImage(new MagickColor(fill.R, fill.G, fill.B), (uint)width, (uint)height);
            image.Composite(flat, x, y, CompositeOperator.Copy);
            return;
        }

        // Work on a copy of the area and put it back, so the blur and the blocks never reach beyond it.
        using var piece = image.CloneArea(x, y, (uint)width, (uint)height);
        piece.ResetPage();
        var whole = new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true };
        if (effect == RedactEffect.Blur)
        {
            var sigma = RedactEffects.BlurSigma(strength, width, height);
            // A wide kernel is slow; blurring a smaller copy and scaling it back looks the same.
            var shrink = Math.Max(1, (int)(sigma / 6));
            if (shrink > 1)
                piece.Resize(new MagickGeometry((uint)Math.Max(1, width / shrink), (uint)Math.Max(1, height / shrink)) { IgnoreAspectRatio = true });
            piece.GaussianBlur(0, sigma / shrink);
            if (shrink > 1)
                piece.Resize(whole);
        }
        else
        {
            var size = BlockSize(block, width, height);
            // Averaging down, then enlarging without smoothing: every block is one flat colour.
            piece.FilterType = FilterType.Box;
            piece.Resize(new MagickGeometry((uint)Math.Max(1, (int)Math.Ceiling((double)width / size)), (uint)Math.Max(1, (int)Math.Ceiling((double)height / size))) { IgnoreAspectRatio = true });
            piece.FilterType = FilterType.Point;
            piece.Resize(whole);
        }
        image.Composite(piece, x, y, CompositeOperator.Copy);
    }
}
