// Mandarina: the photo half of the Compress panel (Magick.NET). One photo in, one smaller copy out, in the format of the
// original (HEIC comes out as JPG because the bundled Magick.NET cannot write HEIC; a BMP comes out as PNG). What "smaller"
// means follows CompressOptions: a quality (JPEG/WebP/AVIF quality, PNG palette size, TIFF with JPEG or lossless), or a size
// to land on. The picture is encoded in memory and written at the very end, so a failure never leaves a half-written file.

using Filee.App.Mandarina.Panels;
using ImageMagick;
using ImageMagick.Formats;

namespace Filee.App.Mandarina.Tools;

public static class ImageCompressor
{
    /// <summary>The range of qualities a size search tries.</summary>
    private const int LowestQuality = 10;

    private const int HighestQuality = 95;

    /// <summary>From this quality up the result is lossless where the format allows it (PNG, TIFF) and JPEG keeps full colour detail.</summary>
    private const int LosslessFrom = 90;

    /// <summary>When even the lowest quality is too big, the picture shrinks 15 % per side, up to eight times.</summary>
    private const double ShrinkFactor = 0.85;

    private const int MaxShrinks = 8;

    /// <summary>PNG palettes for size searches, gentlest first. Dithering hides banding but costs bytes, so only the largest palette gets it.</summary>
    private static readonly (int Colors, bool Dither)[] Palettes = [(256, true), (128, false), (64, false), (32, false), (16, false)];

    private enum ImageKind
    {
        Jpeg,
        Png,
        WebP,
        Avif,
        Tiff,
    }

    /// <summary>The extension the compressed photo gets: the source's own, except HEIC/HEIF/HIF (→ ".jpg") and BMP (→ ".png").</summary>
    public static string OutputExtension(string source)
    {
        var extension = Path.GetExtension(source);
        if (PanelImages.IsHeif(extension))
            return ".jpg";
        return extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ? ".png" : extension;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="output"/> (a free path with <see cref="OutputExtension"/>) and
    /// returns it. Reports 0..1. With TargetSize the result is at or just under the target when the picture allows it (best
    /// effort otherwise). Throws <see cref="PanelException"/> for a picture that cannot be read; nothing is left behind on failure.
    /// </summary>
    public static Task<string> CompressAsync(string source, string output, CompressOptions options, IProgress<double>? progress,
        CancellationToken cancel) =>
        Task.Run(() => Compress(source, output, options, progress, cancel), cancel);

    private static string Compress(string source, string output, CompressOptions options, IProgress<double>? progress, CancellationToken cancel)
    {
        var kind = KindOf(output);
        var target = options.Mode == CompressMode.TargetSize ? options.TargetBytes : 0;
        var sourceLength = new FileInfo(source).Length;
        var sameFormat = Path.GetExtension(source).Equals(Path.GetExtension(output), StringComparison.OrdinalIgnoreCase);

        // Already at the wanted size and nothing to resize: the original is the answer.
        if (sameFormat && options.MaxSize == 0 && target > 0 && sourceLength <= target)
        {
            WriteNew(output, File.ReadAllBytes(source));
            progress?.Report(1);
            return output;
        }

        try
        {
            RejectSeveralFrames(source);
            cancel.ThrowIfCancellationRequested();
            using var image = PanelImages.Read(source);
            var (width, height) = (image.Width, image.Height);
            progress?.Report(0.1);

            DropExifThumbnail(image);
            if (kind == ImageKind.Jpeg)
                FlattenAlpha(image);
            Shrink(image, options.MaxSize);
            var resized = (image.Width, image.Height) != (width, height);

            var bytes = new Job(kind, progress, cancel).Encode(image, options);

            // A copy that is not smaller than the original is no use: when nothing else changed, keep the original's bytes.
            if (sameFormat && !resized && bytes.Length >= sourceLength)
                bytes = File.ReadAllBytes(source);

            WriteNew(output, bytes);
            progress?.Report(1);
            return output;
        }
        catch (MagickException ex)
        {
            throw Unreadable(source, ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or PanelException or IOException or UnauthorizedAccessException))
        {
            // Nothing else is expected here; if it happens (out of memory on a huge picture, say) it must be said, not crash the app.
            throw new PanelException($"“{Path.GetFileName(source)}” could not be compressed.", ex)
            {
                Key = "panel.compress.error.failed",
                Args = [Path.GetFileName(source)],
            };
        }
    }

    private static ImageKind KindOf(string output) => Path.GetExtension(output).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageKind.Jpeg,
        ".png" => ImageKind.Png,
        ".webp" => ImageKind.WebP,
        ".avif" => ImageKind.Avif,
        ".tif" or ".tiff" => ImageKind.Tiff,
        var other => throw new PanelException($"Pictures of type “{other}” cannot be compressed.") { Key = "panel.compress.error.unsupported", Args = [other] },
    };

    /// <summary>Only the first frame is ever read, so a multi-page TIFF or an animated WebP would silently lose the rest: say no instead.</summary>
    private static void RejectSeveralFrames(string source)
    {
        if (Path.GetExtension(source).ToLowerInvariant() is not (".tif" or ".tiff" or ".webp"))
            return;
        if (MagickImageInfo.ReadCollection(source).Take(2).Count() > 1)
            throw Unreadable(source, null);
    }

    /// <summary>
    /// What a compressor says about a file it cannot use (damaged, protected, several pages…). The message is short English like
    /// the other engines' and the key says it in the user's language.
    /// </summary>
    internal static PanelException Unreadable(string source, Exception? inner) =>
        new($"“{Path.GetFileName(source)}” is damaged, protected or not a file that can be compressed.", inner)
        {
            Key = "panel.compress.unreadable",
            Args = [Path.GetFileName(source)],
            Detail = inner?.Message,
        };

    /// <summary>Removes the small preview some cameras embed in the EXIF block; the rest of the EXIF stays.</summary>
    private static void DropExifThumbnail(IMagickImage<byte> image)
    {
        if (image.GetExifProfile() is { ThumbnailLength: > 0 } exif)
        {
            exif.RemoveThumbnail();
            image.SetProfile(exif);
        }
    }

    /// <summary>JPEG has no transparency: flatten onto white (what <see cref="PanelImages.Save"/> does).</summary>
    private static void FlattenAlpha(IMagickImage<byte> image)
    {
        if (!image.HasAlpha)
            return;
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaOption.Remove);
    }

    /// <summary>Brings the longer side down to <paramref name="longestSide"/> pixels (Lanczos); never enlarges, 0 keeps the size.</summary>
    private static void Shrink(IMagickImage<byte> image, int longestSide)
    {
        if (longestSide <= 0 || Math.Max(image.Width, image.Height) <= longestSide)
            return;
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry((uint)longestSide, (uint)longestSide));
    }

    // ───────── Encoders (shared with PdfCompressor) ─────────

    internal static byte[] Write(IMagickImage<byte> image, MagickFormat format)
    {
        using var stream = new MemoryStream();
        image.Write(stream, format);
        return stream.ToArray();
    }

    /// <summary>
    /// A baseline JPEG with optimized Huffman tables; 4:2:0 chroma below quality 90, full colour detail above. A grey picture
    /// is written as a one-component JPEG and a colour one always as three components, even when it happens to be grey
    /// (Magick would otherwise quietly turn it into a one-component file).
    /// </summary>
    internal static byte[] EncodeJpeg(IMagickImage<byte> image, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        image.Quality = (uint)quality;
        if (image.ColorSpace != ColorSpace.CMYK)
            image.Settings.ColorType = image.ColorSpace == ColorSpace.Gray ? ColorType.Grayscale : ColorType.TrueColor;
        image.Settings.SetDefines(new JpegWriteDefines
        {
            OptimizeCoding = true,
            SamplingFactor = quality < LosslessFrom ? JpegSamplingFactor.Ratio420 : JpegSamplingFactor.Ratio444,
        });
        return Write(image, MagickFormat.Jpeg);
    }

    private static byte[] EncodeWith(IMagickImage<byte> image, MagickFormat format, int quality)
    {
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        return Write(image, format);
    }

    /// <summary>TIFF: JPEG-compressed at <paramref name="jpegQuality"/>, or lossless (Zip, strongest) when it is null.</summary>
    private static byte[] EncodeTiff(IMagickImage<byte> image, int? jpegQuality)
    {
        image.Settings.Compression = jpegQuality is null ? CompressionMethod.Zip : CompressionMethod.JPEG;
        image.Quality = (uint)(jpegQuality ?? 95);
        return Write(image, MagickFormat.Tiff);
    }

    /// <summary>PNG: lossless with the strongest compression when <paramref name="colors"/> is 0, else cut down to a palette.</summary>
    private static byte[] EncodePng(IMagickImage<byte> image, int colors, bool dither)
    {
        if (colors == 0)
        {
            image.Quality = 95;
            return Write(image, MagickFormat.Png);
        }
        using var palette = image.Clone();
        palette.Quantize(new QuantizeSettings { Colors = (uint)colors, DitherMethod = dither ? DitherMethod.FloydSteinberg : DitherMethod.No });
        palette.Quality = 95;
        return Write(palette, MagickFormat.Png8);
    }

    private static (int Colors, bool Dither) PaletteFor(int quality) => quality >= 70 ? Palettes[0] : quality >= 50 ? Palettes[1] : Palettes[2];

    private static IMagickImage<byte> Scaled(IMagickImage<byte> image, double factor)
    {
        var copy = image.Clone();
        copy.FilterType = FilterType.Lanczos;
        copy.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(image.Width * factor)), (uint)Math.Max(1, Math.Round(image.Height * factor)))
        {
            IgnoreAspectRatio = true,
        });
        return copy;
    }

    /// <summary>Creates <paramref name="path"/> and writes <paramref name="bytes"/>; never overwrites, and removes what it started on failure.</summary>
    internal static void WriteNew(string path, byte[] bytes)
    {
        var created = false;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            file.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            if (created)
                MediaTools.TryDelete(path);
            throw;
        }
    }

    // ───────── One picture's encoding, from a quality or towards a size ─────────

    private sealed class Job(ImageKind kind, IProgress<double>? progress, CancellationToken cancel)
    {
        private int _encodes;

        public byte[] Encode(IMagickImage<byte> image, CompressOptions options) =>
            options.Mode == CompressMode.TargetSize && options.TargetBytes > 0
                ? FitTarget(image, options.TargetBytes)
                : EncodeAt(image, Math.Clamp(options.Quality, 1, 100));

        private byte[] EncodeAt(IMagickImage<byte> image, int quality)
        {
            var lossless = quality >= LosslessFrom;
            var (colors, dither) = lossless ? (0, false) : PaletteFor(quality);
            return kind switch
            {
                ImageKind.Png => Palette(image, colors, dither),
                ImageKind.Tiff when lossless || image.HasAlpha => LosslessTiff(image),
                _ => Lossy(image, quality),
            };
        }

        /// <summary>Counts and reports an encode, and checks for cancellation before it.</summary>
        private byte[] Run(Func<byte[]> encode)
        {
            cancel.ThrowIfCancellationRequested();
            var bytes = encode();
            _encodes++;
            progress?.Report(Math.Min(0.95, 0.1 + 0.1 * _encodes));
            return bytes;
        }

        private byte[] Lossy(IMagickImage<byte> image, int quality) => Run(() => kind switch
        {
            ImageKind.Jpeg => EncodeJpeg(image, quality),
            ImageKind.WebP => EncodeWith(image, MagickFormat.WebP, quality),
            ImageKind.Avif => EncodeWith(image, MagickFormat.Avif, quality),
            _ => EncodeTiff(image, quality),
        });

        private byte[] LosslessTiff(IMagickImage<byte> image) => Run(() => EncodeTiff(image, null));

        private byte[] Palette(IMagickImage<byte> image, int colors, bool dither) => Run(() => EncodePng(image, colors, dither));

        // ───────── Landing on a size ─────────

        private byte[] FitTarget(IMagickImage<byte> image, long target)
        {
            // The gentlest result that fits at this size, or the harshest one when none does.
            byte[] AtSize(IMagickImage<byte> sized) => kind switch
            {
                ImageKind.Png => FitPalette(sized, target),
                ImageKind.Tiff when sized.HasAlpha => LosslessTiff(sized),
                ImageKind.Tiff => FitTiff(sized, target),
                _ => Bisect(sized, target),
            };
            byte[] Harshest(IMagickImage<byte> sized) => kind switch
            {
                ImageKind.Png => Palette(sized, Palettes[^1].Colors, false),
                ImageKind.Tiff when sized.HasAlpha => LosslessTiff(sized),
                _ => Lossy(sized, LowestQuality),
            };

            var best = AtSize(image);
            if (best.Length <= target)
                return best;

            // Even the harshest setting is too big: make the picture smaller until the harshest fits, then spend the room on quality.
            for (var shrink = 1; shrink <= MaxShrinks; shrink++)
            {
                using var smaller = Scaled(image, Math.Pow(ShrinkFactor, shrink));
                best = Harshest(smaller);
                if (best.Length <= target)
                    return AtSize(smaller);
            }
            return best;
        }

        /// <summary>The highest quality (10..95) whose result fits, found by halving; the lowest quality's result when none does.</summary>
        private byte[] Bisect(IMagickImage<byte> image, long target)
        {
            // An AVIF encode takes seconds: settling within four quality points is plenty.
            var resolution = kind == ImageKind.Avif ? 4 : 1;
            byte[]? best = null, smallest = null;
            int low = LowestQuality, high = HighestQuality;
            while (low <= high && high - low + 1 >= resolution)
            {
                var middle = (low + high) / 2;
                var bytes = Lossy(image, middle);
                if (bytes.Length <= target)
                {
                    best = bytes;
                    low = middle + 1;
                }
                else
                {
                    smallest = bytes;
                    high = middle - 1;
                }
            }
            if (best is not null)
                return best;
            // None fit. With a resolution of 1 the last try was the lowest quality; otherwise it may not have been reached yet.
            return resolution == 1 && smallest is not null ? smallest : Lossy(image, LowestQuality);
        }

        /// <summary>PNG: lossless first, then smaller and smaller palettes.</summary>
        private byte[] FitPalette(IMagickImage<byte> image, long target)
        {
            var bytes = Palette(image, 0, false);
            if (bytes.Length <= target)
                return bytes;
            foreach (var (colors, dither) in Palettes)
            {
                bytes = Palette(image, colors, dither);
                if (bytes.Length <= target)
                    return bytes;
            }
            return bytes;
        }

        /// <summary>TIFF: lossless first, then JPEG compression at the highest quality that fits.</summary>
        private byte[] FitTiff(IMagickImage<byte> image, long target)
        {
            var lossless = LosslessTiff(image);
            return lossless.Length <= target ? lossless : Bisect(image, target);
        }
    }
}
