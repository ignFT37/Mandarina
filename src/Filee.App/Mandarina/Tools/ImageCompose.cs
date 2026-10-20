// Mandarina: the picture work shared by the "Background" (frameImage) and "Collage" (createCollage) panels: hex colours,
// flat / gradient / picture backgrounds, "cover" and "contain" resizing, rounded corners, soft shadows and the canvas
// geometry of a framed photo. Plain Magick.NET, no Avalonia: a panel's preview and its final render run the very same
// code, only at different sizes, and the tests can call it directly.

using ImageMagick;

namespace Filee.App.Mandarina.Tools;

/// <summary>How <see cref="ImageCompose.Resize"/> fits a picture into a box.</summary>
public enum ComposeFit
{
    /// <summary>Fills the whole box and crops what sticks out, around the centre.</summary>
    Cover,

    /// <summary>Shows the whole picture inside the box; one side may stay shorter than the box.</summary>
    Contain,
}

/// <summary>Where a photo sits on its framed canvas, in pixels.</summary>
public readonly record struct FrameLayout(uint CanvasWidth, uint CanvasHeight, int PhotoX, int PhotoY, uint PhotoWidth, uint PhotoHeight);

public static class ImageCompose
{
    /// <summary>The finished canvas is never smaller than this on its longer side (a small photo is enlarged to fit).</summary>
    public const int MinCanvasSide = 1200;

    /// <summary>... and never larger than this (a huge photo is shrunk).</summary>
    public const int MaxCanvasSide = 6000;

    /// <summary>Blur sigma a shadow is drawn at: bigger blurs are drawn on a smaller layer and scaled back up.</summary>
    private const double ShadowWorkSigma = 4;

    /// <summary>4×4 Bayer thresholds (0..15): gradients are dithered with them so 8-bit steps don't band.</summary>
    private static readonly byte[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    // ───────── Colours ─────────

    /// <summary>Reads "#RRGGBB" or "#RGB" (the # is optional, case doesn't matter).</summary>
    public static bool TryParseColor(string? text, out MagickColor color)
    {
        color = MagickColors.White;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 3 or 6 } || !hex.All(Uri.IsHexDigit))
            return false;
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(digit => new string(digit, 2)));
        color = new MagickColor(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
        return true;
    }

    /// <summary>The colour of <paramref name="text"/>, or <paramref name="fallback"/>'s when it isn't a hex colour.</summary>
    public static MagickColor ParseColor(string? text, string fallback = "#FFFFFF") =>
        TryParseColor(text, out var color) ? color : TryParseColor(fallback, out var other) ? other : MagickColors.White;

    /// <summary>"#RRGGBB", upper case.</summary>
    public static string ToHex(IMagickColor<byte> color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>"#RRGGBB" for a readable <paramref name="text"/>, otherwise <paramref name="fallback"/> untouched.</summary>
    public static string NormalizeHex(string? text, string fallback) => TryParseColor(text, out var color) ? ToHex(color) : fallback;

    // ───────── Backgrounds ─────────

    /// <summary>An opaque canvas of one colour.</summary>
    public static MagickImage Solid(uint width, uint height, IMagickColor<byte> color)
    {
        var image = new MagickImage(color, Math.Max(1, width), Math.Max(1, height));
        image.Alpha(AlphaOption.Off);
        return image;
    }

    /// <summary>
    /// An opaque linear gradient from <paramref name="from"/> to <paramref name="to"/>, the angle in degrees as in CSS: the
    /// direction the gradient runs towards, clockwise from "up" (0 bottom to top, 90 left to right, 180 top to bottom, 135 top
    /// left to bottom right). Whatever the angle, the two colours sit exactly on the two corners the gradient line runs between.
    /// </summary>
    public static MagickImage Gradient(uint width, uint height, IMagickColor<byte> from, IMagickColor<byte> to, double angle)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var radians = angle * Math.PI / 180;
        double dx = Math.Sin(radians), dy = -Math.Cos(radians);
        // Half the length of the canvas projected on the gradient line: t runs 0..1 between the two extreme corners.
        var half = Math.Max(1e-6, (Math.Abs(width * dx) + Math.Abs(height * dy)) / 2);
        var data = new byte[(long)width * height * 3];
        int w = (int)width, h = (int)height;
        Parallel.For(0, h, y =>
        {
            var offset = y * w * 3;
            var along = (y + 0.5 - h / 2.0) * dy;
            for (var x = 0; x < w; x++)
            {
                var t = Math.Clamp(0.5 + ((x + 0.5 - w / 2.0) * dx + along) / (2 * half), 0, 1);
                var threshold = (Bayer[(y & 3) * 4 + (x & 3)] + 0.5) / 16;
                data[offset++] = Mix(from.R, to.R, t, threshold);
                data[offset++] = Mix(from.G, to.G, t, threshold);
                data[offset++] = Mix(from.B, to.B, t, threshold);
            }
        });
        return new MagickImage(data, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGB));

        static byte Mix(byte a, byte b, double t, double threshold) => (byte)Math.Clamp(Math.Floor(a + (b - a) * t + threshold), 0, 255);
    }

    /// <summary>A new canvas made of <paramref name="source"/> scaled to cover <paramref name="width"/> × <paramref name="height"/>; the source is left alone.</summary>
    public static MagickImage Cover(MagickImage source, uint width, uint height)
    {
        var image = new MagickImage(source);
        Resize(image, width, height, ComposeFit.Cover);
        image.Alpha(AlphaOption.Remove);
        return image;
    }

    // ───────── Size and shape ─────────

    /// <summary>
    /// Resizes <paramref name="image"/> in place to fit <paramref name="width"/> × <paramref name="height"/>. Cover gives
    /// exactly that size (the surplus is cut off evenly all round); Contain keeps the whole picture, so its size is at most
    /// that and its shape is the original's.
    /// </summary>
    public static void Resize(MagickImage image, uint width, uint height, ComposeFit mode)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        double widthScale = (double)width / image.Width, heightScale = (double)height / image.Height;
        var scale = mode == ComposeFit.Cover ? Math.Max(widthScale, heightScale) : Math.Min(widthScale, heightScale);
        var scaledWidth = Math.Max(1, (uint)Math.Round(image.Width * scale));
        var scaledHeight = Math.Max(1, (uint)Math.Round(image.Height * scale));
        if (mode == ComposeFit.Cover)
        {
            // Rounding must never leave a gap at the edge of the box.
            scaledWidth = Math.Max(scaledWidth, width);
            scaledHeight = Math.Max(scaledHeight, height);
        }
        else
        {
            scaledWidth = Math.Min(scaledWidth, width);
            scaledHeight = Math.Min(scaledHeight, height);
        }
        if (scaledWidth != image.Width || scaledHeight != image.Height)
            image.Resize(new MagickGeometry(scaledWidth, scaledHeight) { IgnoreAspectRatio = true });
        if (mode == ComposeFit.Cover && (image.Width != width || image.Height != height))
        {
            image.Crop(width, height, Gravity.Center);
            image.ResetPage();
        }
    }

    /// <summary>
    /// Makes the corners of <paramref name="image"/> round by fading its alpha out in the four corner squares (the radius is
    /// in pixels, at most half the shorter side). The image gains an alpha channel. Only those squares are touched, so a
    /// 6000 px photo costs hardly more than a small one.
    /// </summary>
    public static void RoundCorners(MagickImage image, double radius)
    {
        var reach = (int)Math.Round(Math.Min(radius, Math.Min(image.Width, image.Height) / 2.0));
        if (reach < 1)
            return;
        var size = (uint)reach;
        image.Alpha(AlphaOption.Set);
        using var topLeft = CornerMask(reach);
        int right = (int)image.Width - reach, bottom = (int)image.Height - reach;
        foreach (var (x, y, flop, flip) in new[] { (0, 0, false, false), (right, 0, true, false), (0, bottom, false, true), (right, bottom, true, true) })
        {
            using var corner = image.CloneArea(x, y, size, size);
            using var mask = new MagickImage(topLeft);
            corner.ResetPage();
            if (flop)
                mask.Flop();
            if (flip)
                mask.Flip();
            corner.Composite(mask, 0, 0, CompositeOperator.DstIn);
            image.Composite(corner, x, y, CompositeOperator.Copy);
        }
    }

    /// <summary>The top left corner of a rounded box: white, opaque inside a circle of <paramref name="reach"/> pixels and clear outside it.</summary>
    private static MagickImage CornerMask(int reach)
    {
        var data = new byte[reach * reach * 4];
        Parallel.For(0, reach, row =>
        {
            var offset = row * reach * 4;
            var dy = reach - row - 0.5;
            for (var column = 0; column < reach; column++)
            {
                var dx = reach - column - 0.5;
                // Distance of the pixel centre from the circle, as coverage: one pixel of edge softness.
                var coverage = Math.Clamp(reach - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
                data[offset++] = 255;
                data[offset++] = 255;
                data[offset++] = 255;
                data[offset++] = (byte)Math.Round(coverage * 255);
            }
        });
        return new MagickImage(data, new PixelReadSettings((uint)reach, (uint)reach, StorageType.Char, PixelMapping.RGBA));
    }

    /// <summary>
    /// A canvas-sized layer (black with alpha) holding the soft shadow of <paramref name="shape"/> placed at
    /// <paramref name="x"/>, <paramref name="y"/>: the shadow follows the shape's alpha (rounded corners, a cut-out), or is its
    /// plain rectangle when it has none. <paramref name="sigma"/> is the blur in canvas pixels, <paramref name="opacity"/> goes
    /// 0..1. A big blur is drawn on a shrunken layer and scaled back up (a blur is smooth, nothing is lost), which keeps a
    /// 6000 px canvas fast.
    /// </summary>
    public static MagickImage ShadowLayer(uint canvasWidth, uint canvasHeight, MagickImage shape, double x, double y, double sigma, double opacity)
    {
        var scale = sigma > ShadowWorkSigma ? ShadowWorkSigma / sigma : 1.0;
        var layerWidth = Math.Max(1, (uint)Math.Ceiling(canvasWidth * scale));
        var layerHeight = Math.Max(1, (uint)Math.Ceiling(canvasHeight * scale));
        var width = Math.Max(1, (uint)Math.Round(shape.Width * scale));
        var height = Math.Max(1, (uint)Math.Round(shape.Height * scale));
        var layer = new MagickImage(MagickColors.Transparent, layerWidth, layerHeight);
        using (var shadow = new MagickImage(MagickColors.Black, width, height))
        {
            shadow.Alpha(AlphaOption.Set);
            if (shape.HasAlpha)
            {
                // Black everywhere, with the shape's own alpha (shrunk along with the layer).
                using var silhouette = width == shape.Width && height == shape.Height ? null : new MagickImage(shape);
                silhouette?.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
                shadow.Composite(silhouette ?? shape, CompositeOperator.CopyAlpha);
            }
            shadow.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, Math.Clamp(opacity, 0, 1));
            layer.Composite(shadow, (int)Math.Round(x * scale), (int)Math.Round(y * scale), CompositeOperator.Over);
        }
        if (sigma * scale > 0.3)
            layer.GaussianBlur(sigma * scale);
        if (layerWidth != canvasWidth || layerHeight != canvasHeight)
            layer.Resize(new MagickGeometry(canvasWidth, canvasHeight) { IgnoreAspectRatio = true });
        return layer;
    }

    /// <summary>
    /// Converts a picture that carries its own colour profile (Display P3 phone shots, Adobe RGB) or is CMYK (print JPEGs, which
    /// would come out in the wrong colours when laid on an RGB canvas) to sRGB, so its pixels and the hex colours picked for
    /// the background are read the same way. A picture without a profile is already taken as sRGB; a CMYK one without a
    /// profile is read as ordinary US press inks.
    /// </summary>
    public static void ToSrgb(MagickImage image)
    {
        try
        {
            if (image.ColorSpace == ColorSpace.CMYK)
                image.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
            else if (image.GetColorProfile() is { } profile && profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) != true)
                image.TransformColorSpace(ColorProfiles.SRGB);
        }
        catch (MagickException)
        {
            // A damaged profile: keep the pixels as they are.
        }
    }

    /// <summary>True when some pixel of <paramref name="image"/> is not fully opaque.</summary>
    public static bool HasTransparency(MagickImage image) =>
        image.HasAlpha && image.Statistics().GetChannel(PixelChannel.Alpha) is { } alpha && alpha.Minimum < Quantum.Max;

    // ───────── The framed photo's canvas ─────────

    /// <summary>
    /// The canvas, before scaling, for a photo of <paramref name="photoWidth"/> × <paramref name="photoHeight"/>: the photo plus
    /// <paramref name="paddingPercent"/> of its longer side on every side; with a <paramref name="ratio"/> (width / height) the
    /// smallest canvas of that shape that still holds it.
    /// </summary>
    private static (double Width, double Height) NaturalCanvas(uint photoWidth, uint photoHeight, double paddingPercent, double? ratio)
    {
        var padding = Math.Max(photoWidth, photoHeight) * Math.Clamp(paddingPercent, 0, 100) / 100;
        double width = photoWidth + 2 * padding, height = photoHeight + 2 * padding;
        if (ratio is not { } shape || shape <= 0)
            return (width, height);
        return width / height < shape ? (height * shape, height) : (width, width / shape);
    }

    /// <summary>
    /// The longer side of the finished canvas: that of the photo plus its padding, but at least <see cref="MinCanvasSide"/>
    /// (a small photo is enlarged) and at most <see cref="MaxCanvasSide"/>.
    /// </summary>
    public static int FinalSide(uint photoWidth, uint photoHeight, double paddingPercent, double? ratio)
    {
        var (width, height) = NaturalCanvas(photoWidth, photoHeight, paddingPercent, ratio);
        return (int)Math.Clamp(Math.Round(Math.Max(width, height)), MinCanvasSide, MaxCanvasSide);
    }

    /// <summary>
    /// Where a photo goes on its canvas when the canvas' longer side is <paramref name="longSide"/> pixels: centred, with
    /// at least the padding all round. The preview and the final picture use different sides and the same proportions.
    /// </summary>
    public static FrameLayout FrameFor(uint photoWidth, uint photoHeight, double paddingPercent, double? ratio, int longSide)
    {
        var (width, height) = NaturalCanvas(photoWidth, photoHeight, paddingPercent, ratio);
        var scale = Math.Max(1, longSide) / Math.Max(width, height);
        var canvasWidth = Math.Max(1, (uint)Math.Round(width * scale));
        var canvasHeight = Math.Max(1, (uint)Math.Round(height * scale));
        var shownWidth = Math.Clamp((uint)Math.Round(photoWidth * scale), 1, canvasWidth);
        var shownHeight = Math.Clamp((uint)Math.Round(photoHeight * scale), 1, canvasHeight);
        return new FrameLayout(canvasWidth, canvasHeight, (int)((canvasWidth - shownWidth) / 2), (int)((canvasHeight - shownHeight) / 2), shownWidth, shownHeight);
    }
}
