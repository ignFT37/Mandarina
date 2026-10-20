// Mandarina: reads and writes the tags of a photo for the METADATA panel, without decoding the picture whenever the format
// allows it. JPEG, PNG, WebP, GIF and TIFF are rewritten at the level of their blocks (segments, chunks, IFDs): the compressed
// picture is copied byte for byte, so nothing is recompressed and the quality cannot change. AVIF and HEIC can only be written
// through Magick.NET: an AVIF is encoded again at its highest quality and a HEIC becomes a JPEG. BMP has no tags.

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal enum PhotoMetadataKind
{
    Jpeg,
    Png,
    WebP,
    Gif,
    Tiff,
    Bmp,
    Heif,
    Unknown,
}

/// <summary>What the panel needs to know about one photo.</summary>
/// <param name="Values">The text of each field the photo has (a field it does not have is missing).</param>
/// <param name="Tags">How many tags the photo carries in all.</param>
/// <param name="OtherTags">How many of them are not in the fields (the panel's "+ N more tags").</param>
/// <param name="CanEdit">False for a format that cannot hold these tags (BMP).</param>
/// <param name="AttachedBytes">Bytes after the picture that "Remove all" drops (the extra pictures a phone adds to a JPEG).</param>
internal sealed record PhotoMetadataInfo(PhotoMetadataKind Kind, IReadOnlyDictionary<PhotoMetadataField, string> Values, int Tags, int OtherTags, bool HasLocation, bool CanEdit, long AttachedBytes);

internal static class PhotoMetadata
{
    /// <summary>Which container a file really is, by its first bytes (a ".jpg" may be a PNG or a HEIC renamed).</summary>
    public static PhotoMetadataKind Sniff(ReadOnlySpan<byte> head)
    {
        if (MetadataJpeg.LooksLikeJpeg(head))
            return PhotoMetadataKind.Jpeg;
        if (MetadataPng.LooksLikePng(head))
            return PhotoMetadataKind.Png;
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
            return PhotoMetadataKind.WebP;
        if (head.StartsWith("GIF8"u8))
            return PhotoMetadataKind.Gif;
        if (head.Length >= 4 && ((head[0] == 'I' && head[1] == 'I' && head[2] == 42 && head[3] == 0) || (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] == 42)))
            return PhotoMetadataKind.Tiff;
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
            return PhotoMetadataKind.Heif;
        if (head.StartsWith("BM"u8))
            return PhotoMetadataKind.Bmp;
        return PhotoMetadataKind.Unknown;
    }

    public static PhotoMetadataKind Sniff(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[16];
        var read = stream.Read(head);
        return Sniff(head[..read]);
    }

    /// <summary>The extension a copy of <paramref name="source"/> is saved with: its own, but ".jpg" for HEIC, HEIF and HIF (they cannot be written).</summary>
    public static string OutputExtension(string source) => PanelImages.OutputExtension(source);

    /// <summary>A file that is none of the photo formats the tool knows: empty, damaged or not a photo (the panel says it in the user's language).</summary>
    private static PanelException NotAPhoto() => new("This picture format is not supported.") { Key = "panel.error.damaged" };

    /// <summary>True when saving <paramref name="source"/> encodes the picture again (AVIF) or changes its format (HEIC).</summary>
    public static bool Recodes(string source) => Sniff(source) == PhotoMetadataKind.Heif;

    // ───────── Reading ─────────

    /// <summary>Reads the tags of <paramref name="path"/>. Synchronous: call it from a background task.</summary>
    public static PhotoMetadataInfo Read(string path)
    {
        var kind = Sniff(path);
        var tags = Load(path, kind);
        var values = new Dictionary<PhotoMetadataField, string>();
        foreach (var field in Enum.GetValues<PhotoMetadataField>())
            if (tags.Get(field) is { Length: > 0 } text)
                values[field] = text;
        var total = tags.Total();
        return new PhotoMetadataInfo(kind, values, total, Math.Max(0, total - tags.Covered()), tags.HasLocation, !tags.CannotStoreAnything, tags.AttachedBytes);
    }

    private static PhotoMetadataTags Load(string path, PhotoMetadataKind kind)
    {
        try
        {
            switch (kind)
            {
                case PhotoMetadataKind.Jpeg:
                    return PhotoMetadataJpeg.Load(MetadataJpeg.Parse(File.ReadAllBytes(path)));
                case PhotoMetadataKind.Png:
                    return PhotoMetadataPng.Load(MetadataPng.Parse(File.ReadAllBytes(path)));
                case PhotoMetadataKind.WebP:
                    return PhotoMetadataWebp.Load(MetadataWebp.Parse(File.ReadAllBytes(path)));
                case PhotoMetadataKind.Gif:
                    return PhotoMetadataGif.Load(MetadataGif.Parse(File.ReadAllBytes(path)));
                case PhotoMetadataKind.Tiff:
                {
                    // Read-only: only the IFDs are read, and nothing the reader changes is ever committed.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var tiff = MetadataTiff.Open(stream, isFile: true) ?? throw new PanelException("This TIFF picture cannot be read.");
                    return PhotoMetadataTiff.Load(tiff);
                }
                case PhotoMetadataKind.Heif:
                    return PhotoMetadataMagick.Load(path);
                case PhotoMetadataKind.Bmp:
                    return new PhotoMetadataTags { CannotStoreAnything = true, CanStoreExif = false };
                default:
                    throw NotAPhoto();
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PanelException(ex.Message, ex);
        }
    }

    // ───────── Writing ─────────

    /// <summary>
    /// Writes a copy of <paramref name="source"/> at <paramref name="output"/> with <paramref name="changes"/> applied: a field
    /// with a value is set, a field with null or empty text is removed. Latitude and longitude change together.
    /// </summary>
    public static void Edit(string source, string output, IReadOnlyDictionary<PhotoMetadataField, string?> changes)
    {
        try
        {
            switch (Sniff(source))
            {
                case PhotoMetadataKind.Jpeg:
                    WriteNew(output, PhotoMetadataJpeg.Edit(File.ReadAllBytes(source), changes));
                    break;
                case PhotoMetadataKind.Png:
                    WriteNew(output, PhotoMetadataPng.Edit(File.ReadAllBytes(source), changes));
                    break;
                case PhotoMetadataKind.WebP:
                    WriteNew(output, PhotoMetadataWebp.Edit(File.ReadAllBytes(source), changes));
                    break;
                case PhotoMetadataKind.Gif:
                    WriteNew(output, PhotoMetadataGif.Edit(File.ReadAllBytes(source), changes));
                    break;
                case PhotoMetadataKind.Tiff:
                    PhotoMetadataTiff.Edit(source, output, changes);
                    break;
                case PhotoMetadataKind.Heif:
                    PhotoMetadataMagick.Edit(source, output, changes);
                    break;
                case PhotoMetadataKind.Bmp:
                    throw new PanelException("BMP pictures cannot hold these tags.");
                default:
                    throw NotAPhoto();
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PanelException(ex.Message, ex);
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to a path that must not exist yet (nothing is ever overwritten).</summary>
    private static void WriteNew(string output, byte[] bytes)
    {
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    /// <summary>Writes a copy of <paramref name="source"/> at <paramref name="output"/> with no tags at all.</summary>
    public static void Strip(string source, string output)
    {
        try
        {
            switch (Sniff(source))
            {
                case PhotoMetadataKind.Jpeg:
                    WriteNew(output, PhotoMetadataJpeg.Strip(File.ReadAllBytes(source)));
                    break;
                case PhotoMetadataKind.Png:
                    WriteNew(output, PhotoMetadataPng.Strip(File.ReadAllBytes(source)));
                    break;
                case PhotoMetadataKind.WebP:
                    WriteNew(output, PhotoMetadataWebp.Strip(File.ReadAllBytes(source)));
                    break;
                case PhotoMetadataKind.Gif:
                    WriteNew(output, PhotoMetadataGif.Strip(File.ReadAllBytes(source)));
                    break;
                case PhotoMetadataKind.Tiff:
                    PhotoMetadataTiff.Strip(source, output);
                    break;
                case PhotoMetadataKind.Heif:
                    PhotoMetadataMagick.Strip(source, output);
                    break;
                case PhotoMetadataKind.Bmp:
                    // A BMP holds no tags: the copy is the file itself.
                    File.Copy(source, output, overwrite: false);
                    break;
                default:
                    throw NotAPhoto();
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PanelException(ex.Message, ex);
        }
    }

    /// <summary>Applies the panel's changes to the tags: the one place that decides how a latitude and a longitude go together.</summary>
    internal static void Apply(PhotoMetadataTags tags, IReadOnlyDictionary<PhotoMetadataField, string?> changes)
    {
        foreach (var (field, value) in changes)
        {
            if (field is PhotoMetadataField.Latitude or PhotoMetadataField.Longitude)
                continue;
            if (field == PhotoMetadataField.Date && !string.IsNullOrEmpty(value) && !MetadataValues.TryParseDate(value, out _))
                throw new PanelException("The date must look like 2024-05-17 10:20:30.");
            tags.Set(field, value);
        }
        var hasLatitude = changes.TryGetValue(PhotoMetadataField.Latitude, out var latitudeText);
        var hasLongitude = changes.TryGetValue(PhotoMetadataField.Longitude, out var longitudeText);
        if (!hasLatitude && !hasLongitude)
            return;
        if (string.IsNullOrEmpty(latitudeText) || string.IsNullOrEmpty(longitudeText))
        {
            tags.RemoveLocation();
            return;
        }
        if (!MetadataValues.TryParseCoordinate(latitudeText, latitude: true, out var latitude)
            || !MetadataValues.TryParseCoordinate(longitudeText, latitude: false, out var longitude))
            throw new PanelException("The coordinates must be decimal degrees such as 40.4168 and -3.7038.");
        tags.SetLocation(latitude, longitude);
    }
}
