// Mandarina: the PDF half of the Compress panel (PDFsharp + Magick.NET). A PDF weighs what its pictures weigh, so compressing
// one means re-encoding them: every image on every page is brought down to the resolution the chosen quality allows and stored
// as a JPEG again. Whatever cannot be treated safely (masks and transparency, palettes, CMYK, CCITT/JBIG2 scans, …) stays
// exactly as it is, and so do the pages, their order and the document information. The new file is built in memory and
// written at the end, so a failure never leaves a half-written PDF.

using System.IO.Compression;
using Filee.App.Mandarina.Panels;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.App.Mandarina.Tools;

public static class PdfCompressor
{
    /// <summary>Pictures with more pixels than this are left alone (decoding them would take gigabytes).</summary>
    private const long MaxPixels = 120_000_000;

    /// <summary>Pictures stored in fewer bytes than this (icons, bullets) are not worth re-encoding.</summary>
    private const int MinImageBytes = 4096;

    /// <summary>A picture is only made smaller when that takes at least 3 % off its width; less is not worth the blur.</summary>
    private const double ResizeBelow = 0.97;

    /// <summary>How many times a size search may rewrite the whole PDF.</summary>
    private const int MaxPasses = 5;

    /// <summary>A size search stops once the result is at least this fraction of the target.</summary>
    private const double CloseEnough = 0.92;

    /// <summary>Quality → the highest resolution (pixels per inch) a picture keeps; in between it is interpolated.</summary>
    private static readonly (int Quality, int Dpi)[] DpiByQuality = [(10, 48), (35, 72), (50, 110), (70, 150), (85, 200), (100, 300)];

    private readonly record struct Level(int Quality, int Dpi);

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="output"/> (a free path) and returns it. Reports 0..1. With
    /// TargetSize it rewrites the PDF up to five times, lowering quality and resolution until it fits (best effort otherwise).
    /// A PDF that is damaged or protected throws <see cref="PanelException"/>; when nothing can be saved the original's bytes are kept.
    /// </summary>
    public static Task<string> CompressAsync(string source, string output, CompressOptions options, IProgress<double>? progress,
        CancellationToken cancel) =>
        Task.Run(() => Compress(source, output, options, progress, cancel), cancel);

    private static string Compress(string source, string output, CompressOptions options, IProgress<double>? progress, CancellationToken cancel)
    {
        var length = new FileInfo(source).Length;
        byte[] best;
        if (options.Mode == CompressMode.TargetSize && options.TargetBytes > 0)
            best = length <= options.TargetBytes ? File.ReadAllBytes(source) : FitTarget(source, options.TargetBytes, progress, cancel);
        else
            best = Rewrite(source, LevelFor(options.Quality), progress, 0, 1, cancel).Bytes;

        // PDFsharp writes plain objects: a file made of many small ones (object streams) can come out bigger. Never hand that back.
        if (best.Length >= length)
            best = File.ReadAllBytes(source);

        ImageCompressor.WriteNew(output, best);
        progress?.Report(1);
        return output;
    }

    // ───────── Settings ─────────

    private static Level LevelFor(int quality) => new(Math.Clamp(quality, 10, 95), DpiFor(quality));

    /// <summary>
    /// The setting a size search tries at <paramref name="harshness"/>: 0 is quality 89, 1 is quality 15. Quality 90 and above would
    /// switch the JPEGs to full chroma detail, a jump in size that would leave sizes between the two out of reach.
    /// </summary>
    private static Level SettingAt(double harshness) => LevelFor((int)Math.Round(89 - 74 * harshness));

    private static int DpiFor(int quality)
    {
        quality = Math.Clamp(quality, DpiByQuality[0].Quality, DpiByQuality[^1].Quality);
        for (var i = 1; i < DpiByQuality.Length; i++)
        {
            var (high, highDpi) = DpiByQuality[i];
            if (quality > high)
                continue;
            var (low, lowDpi) = DpiByQuality[i - 1];
            return (int)Math.Round(lowDpi + (highDpi - lowDpi) * (quality - low) / (double)(high - low));
        }
        return DpiByQuality[^1].Dpi;
    }

    // ───────── Landing on a size ─────────

    /// <summary>
    /// The gentlest setting whose PDF fits, found with at most five rewrites: the harshest setting first (if that does not fit
    /// nothing will), then the gentlest (if that fits it is the answer), then straight-line guesses, on a log-size scale,
    /// between a setting that fits and one that does not.
    /// </summary>
    private static byte[] FitTarget(string source, long target, IProgress<double>? progress, CancellationToken cancel)
    {
        var pass = 0;
        (byte[] Bytes, int Changed) Pass(double harshness)
        {
            var result = Rewrite(source, SettingAt(harshness), progress, pass / (double)MaxPasses, 1.0 / MaxPasses, cancel);
            pass++;
            return result;
        }

        var (best, changed) = Pass(1);
        if (best.Length > target || changed == 0)
            return best;
        var (gentlest, _) = Pass(0);
        if (gentlest.Length <= target)
            return gentlest;

        double fit = 1, over = 0;
        double fitSize = best.Length, overSize = gentlest.Length;
        while (pass < MaxPasses && best.Length < target * CloseEnough && fit - over > 0.02)
        {
            // Aim a little under the target (a guess that lands over it is wasted), and stay clear of both ends so the bracket shrinks.
            var slope = Math.Log(fitSize / overSize);
            var guess = slope < -1e-9 ? over + Math.Log(target * 0.96 / overSize) / slope * (fit - over) : (over + fit) / 2;
            var next = Math.Clamp(guess, over + 0.05 * (fit - over), fit - 0.05 * (fit - over));
            var (bytes, _) = Pass(next);
            if (bytes.Length <= target)
                (best, fit, fitSize) = (bytes, next, bytes.Length);
            else
                (over, overSize) = (next, bytes.Length);
        }
        return best;
    }

    // ───────── One rewrite ─────────

    /// <summary>Re-encodes the pictures of <paramref name="source"/> at <paramref name="level"/> and returns the new PDF and how many pictures changed.</summary>
    private static (byte[] Bytes, int Changed) Rewrite(string source, Level level, IProgress<double>? progress, double start, double span,
        CancellationToken cancel)
    {
        using var document = Open(source);
        try
        {
            var pages = document.PageCount;      // before saving: a saved document refuses to be asked anything
            var images = ImagesOf(document);
            var changed = 0;
            for (var i = 0; i < images.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    if (Recompress(images[i].Image, images[i].PageWidth, level))
                        changed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // This picture could not be decoded or re-encoded (a broken stream, an odd structure): it stays as it is.
                }
                progress?.Report(start + span * (i + 1) / (images.Count + 1));
            }

            Pack(document);
            document.Options.NoCompression = false;
            document.Options.CompressContentStreams = true;
            document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            using var stream = new MemoryStream();
            document.Save(stream, false);
            var bytes = stream.ToArray();
            // What was written must read back with the same number of pages, or it is not handed on.
            if (!ReadsBack(bytes, pages))
                throw new PanelException($"“{Path.GetFileName(source)}” could not be rewritten safely.") { Key = "panel.compress.error.unsafe", Args = [Path.GetFileName(source)] };
            progress?.Report(start + span);
            return (bytes, changed);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or PanelException or IOException or UnauthorizedAccessException))
        {
            // PDFsharp has exceptions of its own that nothing above knows about: whatever it is, say it in words.
            throw new PanelException($"“{Path.GetFileName(source)}” could not be compressed.", ex) { Key = "panel.compress.error.failed", Args = [Path.GetFileName(source)] };
        }
    }

    private static bool ReadsBack(byte[] pdf, int pages)
    {
        try
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var check = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            return check.PageCount == pages;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static PdfDocument Open(string path)
    {
        PdfDocument? document = null;
        try
        {
            document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            if (document.SecuritySettings.IsEncrypted)
                throw new InvalidOperationException("The document is encrypted.");
            return document;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            // PDFsharp reports a damaged file as InvalidOperationException, PdfReaderException or a plain Exception, and a
            // protected one as PdfReaderException: to the user they are the same.
            document?.Dispose();
            throw ImageCompressor.Unreadable(path, ex);
        }
    }

    /// <summary>Streams without a filter (page contents, forms, raw pictures) are stored compressed: a loaded document does not do that by itself.</summary>
    private static void Pack(PdfDocument document)
    {
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is not PdfDictionary { Stream: { } stream } dictionary)
                continue;
            var elements = dictionary.Elements;
            // XMP metadata must stay readable as it is (PDF/A); /F points to an external file; /DecodeParms belongs to a filter that is not there.
            if (stream.IsFiltered() || stream.Length < 64 || elements.ContainsKey("/F") || elements.ContainsKey("/DecodeParms")
                || elements.GetName("/Type") is "/Metadata" or "/XRef" or "/ObjStm")
                continue;
            stream.Zip();
        }
    }

    // ───────── Finding the pictures ─────────

    /// <summary>Every picture of every page, once each, with the width (in points) of the first page that uses it. Forms are searched too.</summary>
    private static List<(PdfDictionary Image, double PageWidth)> ImagesOf(PdfDocument document)
    {
        var found = new List<(PdfDictionary, double)>();
        var seen = new HashSet<int>();

        void Search(PdfDictionary? resources, double pageWidth, int depth)
        {
            if (resources is null || ItemOf(resources, "/XObject") is not PdfDictionary xobjects)
                return;
            foreach (var entry in xobjects.Elements)
            {
                if (entry.Value is not PdfReference { Value: PdfDictionary xobject } reference || !seen.Add(reference.ObjectNumber))
                    continue;
                switch (xobject.Elements.GetName("/Subtype"))
                {
                    case "/Image":
                        found.Add((xobject, pageWidth));
                        break;
                    case "/Form" when depth < 4:
                        Search(ItemOf(xobject, "/Resources") as PdfDictionary, pageWidth, depth + 1);
                        break;
                }
            }
        }

        foreach (var page in document.Pages)
        {
            var width = page.Width.Point;
            Search(ResourcesOf(page), double.IsFinite(width) && width > 0 ? Math.Clamp(width, 72, 14400) : 612, 0);
        }
        return found;
    }

    /// <summary>
    /// The page's resources, which may be inherited from the page tree. Read without PdfPage.Resources: that creates an empty
    /// dictionary when the page has none of its own, and an empty one would hide the inherited fonts and pictures.
    /// </summary>
    private static PdfDictionary? ResourcesOf(PdfDictionary page)
    {
        PdfDictionary? node = page;
        for (var depth = 0; node is not null && depth < 32; depth++)
        {
            if (ItemOf(node, "/Resources") is PdfDictionary resources)
                return resources;
            node = ItemOf(node, "/Parent") as PdfDictionary;
        }
        return null;
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static PdfItem? ItemOf(PdfDictionary dictionary, string key) =>
        dictionary.Elements.ContainsKey(key) ? Resolve(dictionary.Elements[key]) : null;

    // ───────── One picture ─────────

    /// <summary>
    /// Re-encodes one image XObject as a JPEG at <paramref name="level"/>, scaled down to the resolution the level allows.
    /// Returns false (the image untouched) when it is not safe to treat or when the new data would not be smaller.
    /// </summary>
    private static bool Recompress(PdfDictionary image, double pageWidth, Level level)
    {
        var elements = image.Elements;
        if (image.Stream is not { } stream)
            return false;
        if (elements.ContainsKey("/SMask") || elements.ContainsKey("/Mask") || elements.ContainsKey("/Matte") || elements.ContainsKey("/SMaskInData")
            || elements.GetBoolean("/ImageMask"))
            return false;

        int width = elements.GetInteger("/Width"), height = elements.GetInteger("/Height");
        if (width < 1 || height < 1 || (long)width * height > MaxPixels || elements.GetInteger("/BitsPerComponent") != 8 || !HasDefaultDecode(image))
            return false;
        var components = ComponentsOf(image);
        var filter = FilterOf(image, out var parms);
        var data = stream.Value;
        if (components == 0 || filter is null || data is null || data.Length < MinImageBytes)
            return false;

        using var picture = filter == "/DCTDecode" ? ReadJpeg(data, width, height, components) : ReadSamples(data, filter, width, height, components, parms);
        if (picture is null)
            return false;

        // The estimate is the picture spread over the page's width: a lower bound of its true resolution, so it is never over-reduced.
        var scale = Math.Min(1.0, level.Dpi * pageWidth / 72.0 / width);
        if (scale < ResizeBelow)
        {
            picture.FilterType = FilterType.Lanczos;
            picture.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)))
            {
                IgnoreAspectRatio = true,
            });
        }
        picture.Strip();
        picture.Orientation = OrientationType.Undefined;
        var jpeg = ImageCompressor.EncodeJpeg(picture, level.Quality);

        // Written by Magick, but the PDF will describe it with its own numbers: they must agree, or the page renders wrong.
        if (jpeg.Length >= data.Length || !TryReadJpegHeader(jpeg, out var newWidth, out var newHeight, out var newComponents)
            || newComponents != components || newWidth != picture.Width || newHeight != picture.Height)
            return false;

        stream.Value = jpeg;
        elements.SetName("/Filter", "/DCTDecode");
        elements.Remove("/DecodeParms");
        elements.SetInteger("/Width", newWidth);
        elements.SetInteger("/Height", newHeight);
        elements.SetInteger("/BitsPerComponent", 8);
        return true;
    }

    /// <summary>1 for grey, 3 for RGB (device, calibrated or ICC-based), 0 for anything else: palettes, CMYK, Lab, spot colours, …</summary>
    private static int ComponentsOf(PdfDictionary image)
    {
        switch (ItemOf(image, "/ColorSpace"))
        {
            case PdfName name:
                return ComponentsOfFamily(name.Value);
            case PdfArray { Elements.Count: > 0 } space:
                var family = (Resolve(space.Elements[0]) as PdfName)?.Value;
                if (family == "/ICCBased")
                {
                    var n = space.Elements.Count > 1 && Resolve(space.Elements[1]) is PdfDictionary profile ? profile.Elements.GetInteger("/N") : 0;
                    return n is 1 or 3 ? n : 0;
                }
                return ComponentsOfFamily(family);
            default:
                return 0;
        }
    }

    private static int ComponentsOfFamily(string? family) => family switch
    {
        "/DeviceRGB" or "/CalRGB" => 3,
        "/DeviceGray" or "/CalGray" => 1,
        _ => 0,
    };

    /// <summary>"" (none), "/FlateDecode" or "/DCTDecode" when that is the image's only filter; null for any other (left alone).</summary>
    private static string? FilterOf(PdfDictionary image, out PdfDictionary? parms)
    {
        parms = null;
        string name;
        switch (ItemOf(image, "/Filter"))
        {
            case null:
            case PdfArray { Elements.Count: 0 }:
                name = "";
                break;
            case PdfName single:
                name = single.Value;
                break;
            case PdfArray { Elements.Count: 1 } list:
                name = (Resolve(list.Elements[0]) as PdfName)?.Value ?? "?";
                break;
            default:
                return null;
        }
        if (name is not ("" or "/FlateDecode" or "/DCTDecode"))
            return null;
        parms = ItemOf(image, "/DecodeParms") switch
        {
            PdfDictionary dictionary => dictionary,
            PdfArray { Elements.Count: 1 } list => Resolve(list.Elements[0]) as PdfDictionary,
            _ => null,
        };
        return name;
    }

    /// <summary>True without a /Decode array or with the identity one ([0 1] per component); an inverted image is left alone.</summary>
    private static bool HasDefaultDecode(PdfDictionary image)
    {
        if (!image.Elements.ContainsKey("/Decode"))
            return true;
        if (ItemOf(image, "/Decode") is not PdfArray decode)
            return false;
        for (var i = 0; i + 1 < decode.Elements.Count; i += 2)
        {
            if (decode.Elements.GetReal(i) != 0 || decode.Elements.GetReal(i + 1) != 1)
                return false;
        }
        return true;
    }

    // ───────── Reading the pixels ─────────

    /// <summary>Decodes a DCT stream whose own header agrees with the dictionary; null otherwise (including CMYK).</summary>
    private static MagickImage? ReadJpeg(byte[] data, int width, int height, int components)
    {
        if (!TryReadJpegHeader(data, out var jpegWidth, out var jpegHeight, out var jpegComponents)
            || jpegWidth != width || jpegHeight != height || jpegComponents != components)
            return null;
        return new MagickImage(data);
    }

    /// <summary>The raw 8-bit samples of a Flate (with or without predictor) or unfiltered stream as a picture; null when they do not add up.</summary>
    private static MagickImage? ReadSamples(byte[] data, string filter, int width, int height, int components, PdfDictionary? parms)
    {
        var rowBytes = width * components;
        byte[]? samples;
        if (filter == "")
        {
            samples = data.Length >= (long)rowBytes * height ? data : null;
        }
        else
        {
            samples = Inflate(data, width, height, components, parms);
        }
        if (samples is null)
            return null;
        var mapping = components == 3 ? "RGB" : "I";
        return new MagickImage(samples, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, mapping));
    }

    /// <summary>Inflates a FlateDecode stream into exactly width × height × components bytes, undoing the PNG or TIFF predictor if it has one.</summary>
    private static byte[]? Inflate(byte[] data, int width, int height, int components, PdfDictionary? parms)
    {
        int Setting(string key, int fallback) => parms is not null && parms.Elements.ContainsKey(key) ? parms.Elements.GetInteger(key) : fallback;

        var predictor = Setting("/Predictor", 1);
        if (predictor is not (1 or 2) && predictor < 10)
            return null;
        // A predictor works on rows of the image's own shape; anything else is not an image we can read back.
        if (predictor > 1 && (Setting("/Colors", 1) != components || Setting("/BitsPerComponent", 8) != 8 || Setting("/Columns", 1) != width))
            return null;

        var rowBytes = width * components;
        var stride = predictor >= 10 ? rowBytes + 1 : rowBytes;
        var buffer = new byte[(long)stride * height];
        using (var zlib = new ZLibStream(new MemoryStream(data, writable: false), CompressionMode.Decompress))
        {
            if (!ReadFully(zlib, buffer))
                return null;
        }
        switch (predictor)
        {
            case 1:
                return buffer;
            case 2:
                for (var row = 0; row < height; row++)
                {
                    for (var x = components; x < rowBytes; x++)
                        buffer[row * rowBytes + x] += buffer[row * rowBytes + x - components];
                }
                return buffer;
            default:
                return UndoPngFilters(buffer, rowBytes, components, height);
        }
    }

    private static bool ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
                return false;
            total += read;
        }
        return true;
    }

    /// <summary>The rows of <paramref name="filtered"/> each start with a PNG filter type (0..4); returns the plain rows, null for an unknown type.</summary>
    internal static byte[]? UndoPngFilters(byte[] filtered, int rowBytes, int bytesPerPixel, int height)
    {
        var plain = new byte[(long)rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var type = filtered[y * (rowBytes + 1)];
            var first = y * (rowBytes + 1) + 1;
            var to = y * rowBytes;
            var above = to - rowBytes;
            for (var x = 0; x < rowBytes; x++)
            {
                int raw = filtered[first + x];
                int left = x >= bytesPerPixel ? plain[to + x - bytesPerPixel] : 0;
                int up = y > 0 ? plain[above + x] : 0;
                int upLeft = x >= bytesPerPixel && y > 0 ? plain[above + x - bytesPerPixel] : 0;
                int value;
                switch (type)
                {
                    case 0:
                        value = raw;
                        break;
                    case 1:
                        value = raw + left;
                        break;
                    case 2:
                        value = raw + up;
                        break;
                    case 3:
                        value = raw + ((left + up) >> 1);
                        break;
                    case 4:
                        value = raw + Paeth(left, up, upLeft);
                        break;
                    default:
                        return null;
                }
                plain[to + x] = (byte)value;
            }
        }
        return plain;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }

    /// <summary>Width, height and number of components from a JPEG's frame header (SOF); false when there is none.</summary>
    internal static bool TryReadJpegHeader(ReadOnlySpan<byte> jpeg, out int width, out int height, out int components)
    {
        (width, height, components) = (0, 0, 0);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return false;
        var i = 2;
        while (i + 3 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }
            var marker = jpeg[i + 1];
            if (marker is 0xFF)
            {
                i++;                                   // fill byte
                continue;
            }
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8))
            {
                i += 2;                                // stuffing, TEM, RSTn, SOI: no length
                continue;
            }
            if (marker == 0xD9)
                return false;                          // EOI before any frame header
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (i + 9 >= jpeg.Length)
                    return false;
                height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                components = jpeg[i + 9];
                return width > 0 && height > 0 && components > 0;
            }
            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }
        return false;
    }
}
