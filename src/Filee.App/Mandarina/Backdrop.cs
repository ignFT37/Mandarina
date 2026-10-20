// Mandarina: the frosted glass behind the wheel. Windows can't blur what lies behind a transparent window that
// never takes the focus, so the screen under the wheel is captured just before it appears, blurred and slightly
// saturated here (like CSS backdrop-filter: blur(16px) saturate(1.45)), and drawn under the slices.

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Filee.Core.Platform;

namespace Filee.App.Mandarina;

internal static class Backdrop
{
    /// <summary>Work at a quarter of the pixels: the blur hides the detail anyway.</summary>
    private const int Downscale = 2;

    /// <summary>
    /// The blurred screen in <paramref name="area"/> (physical pixels), or null when the platform can't capture it.
    /// <paramref name="blurPixels"/> is the blur radius in physical pixels.
    /// </summary>
    public static Bitmap? Capture(IPlatformServices platform, PixelRect area, double blurPixels)
    {
        if (area.Width <= 0 || area.Height <= 0 || platform.CaptureScreen(area.X, area.Y, area.Width, area.Height) is not { } pixels)
            return null;

        var w = Math.Max(1, area.Width / Downscale);
        var h = Math.Max(1, area.Height / Downscale);
        var small = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // Average the 2×2 block.
                for (var c = 0; c < 4; c++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < Downscale; dy++)
                    for (var dx = 0; dx < Downscale; dx++)
                    {
                        var sy = Math.Min(area.Height - 1, y * Downscale + dy);
                        var sx = Math.Min(area.Width - 1, x * Downscale + dx);
                        sum += pixels[(sy * area.Width + sx) * 4 + c];
                    }
                    small[(y * w + x) * 4 + c] = (byte)(sum / (Downscale * Downscale));
                }
            }
        }

        // Three box blurs approximate a Gaussian.
        var radius = Math.Max(1, (int)Math.Round(blurPixels / Downscale / 2));
        var buffer = new byte[small.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxBlur(small, buffer, w, h, radius, horizontal: true);
            BoxBlur(buffer, small, w, h, radius, horizontal: false);
        }
        Saturate(small, 1.45);

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(small, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(), new PixelSize(w, h),
                new Vector(96, 96), w * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void BoxBlur(byte[] source, byte[] target, int w, int h, int radius, bool horizontal)
    {
        var lines = horizontal ? h : w;
        var length = horizontal ? w : h;
        var window = radius * 2 + 1;
        Parallel.For(0, lines, line =>
        {
            int Index(int i) => horizontal ? (line * w + i) * 4 : (i * w + line) * 4;
            Span<int> sum = stackalloc int[3];
            for (var c = 0; c < 3; c++)
            {
                sum[c] = 0;
                for (var k = -radius; k <= radius; k++)
                    sum[c] += source[Index(Math.Clamp(k, 0, length - 1)) + c];
            }
            for (var i = 0; i < length; i++)
            {
                var at = Index(i);
                for (var c = 0; c < 3; c++)
                    target[at + c] = (byte)(sum[c] / window);
                target[at + 3] = 255;
                var leaving = Index(Math.Clamp(i - radius, 0, length - 1));
                var entering = Index(Math.Clamp(i + radius + 1, 0, length - 1));
                for (var c = 0; c < 3; c++)
                    sum[c] += source[entering + c] - source[leaving + c];
            }
        });
    }

    private static void Saturate(byte[] bgra, double amount)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            var gray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            bgra[i] = (byte)Math.Clamp(gray + (b - gray) * amount, 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(gray + (g - gray) * amount, 0, 255);
            bgra[i + 2] = (byte)Math.Clamp(gray + (r - gray) * amount, 0, 255);
        }
    }
}
