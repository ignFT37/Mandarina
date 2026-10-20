// Mandarina: the WebP driver of the METADATA tool: the 'EXIF' and 'XMP ' chunks are read into a PhotoMetadataTags and written
// back, with the flags of the 'VP8X' header that announce them. The picture (lossy, lossless or animated) is not touched.

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal static class PhotoMetadataWebp
{
    private static readonly HashSet<string> Picture = ["VP8X", "ICCP", "ANIM", "ANMF", "ALPH", "VP8 ", "VP8L"];

    internal static PhotoMetadataTags Load(MetadataWebp webp)
    {
        var tags = new PhotoMetadataTags();
        if (webp.Chunks.FirstOrDefault(c => c.Type == "EXIF") is { } exif)
            tags.Exif = OpenExif(exif.Data);
        if (webp.Chunks.FirstOrDefault(c => c.Type == "XMP ") is { } xmp)
        {
            tags.Xmp = MetadataXmp.TryParse(xmp.Data);
            tags.XmpUnreadable = tags.Xmp is null;
        }
        return tags;
    }

    private static MetadataTiff? OpenExif(byte[] payload)
    {
        var stream = new MemoryStream();
        // Some writers put the "Exif\0\0" of a JPEG in front of the block.
        stream.Write(payload.AsSpan().StartsWith("Exif\0\0"u8) ? payload.AsSpan(6) : payload);
        try
        {
            return MetadataTiff.Open(stream);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    internal static byte[] Edit(byte[] original, IReadOnlyDictionary<PhotoMetadataField, string?> changes)
    {
        var webp = MetadataWebp.Parse(original);
        var tags = Load(webp);
        if (webp.Chunks.Any(c => c.Type == "EXIF") && tags.Exif is null)
            throw new PanelException("The EXIF of this picture is damaged: use \"Remove all\" instead.");
        PhotoMetadata.Apply(tags, changes);

        byte[]? exifBytes = null;
        if (tags.Exif is { } exif)
        {
            exif.Commit();
            exifBytes = exif.ToArray();
        }
        Write(webp, exifBytes, tags.XmpChanged ? tags.Xmp?.ToPacket() : null, replaceXmp: tags.XmpChanged);
        var result = webp.ToBytes();
        EnsureSamePicture(MetadataWebp.Parse(original), MetadataWebp.Parse(result));
        return result;
    }

    internal static byte[] Strip(byte[] original)
    {
        var webp = MetadataWebp.Parse(original);
        var tags = Load(webp);
        var orientation = tags.Exif?.GetInt(MetadataTiffIfd.Main, MetadataTiff.Orientation) ?? 1;
        byte[]? exifBytes = null;
        if (orientation is > 1 and <= 8)
        {
            var exif = MetadataTiff.CreateEmpty(tags.Exif?.BigEndian ?? true);
            exif.SetShort(MetadataTiffIfd.Main, MetadataTiff.Orientation, (ushort)orientation);
            exif.Commit();
            exifBytes = exif.ToArray();
        }
        webp.Chunks.RemoveAll(c => !Picture.Contains(c.Type));
        Write(webp, exifBytes, null, replaceXmp: true);
        var result = webp.ToBytes();
        EnsureSamePicture(MetadataWebp.Parse(original), MetadataWebp.Parse(result));
        return result;
    }

    /// <summary>
    /// Puts <paramref name="exif"/> (null: none) and the XMP packet (when <paramref name="replaceXmp"/>; null: none) in the
    /// chunk list, after the picture where the format wants them, and sets the header flags to match.
    /// </summary>
    private static void Write(MetadataWebp webp, byte[]? exif, byte[]? xmp, bool replaceXmp)
    {
        // EXIF
        webp.Chunks.RemoveAll(c => c.Type == "EXIF");
        if (exif is not null)
        {
            if (!webp.HasHeader)
                webp.EnsureHeader();
            webp.Chunks.Add(new MetadataWebpChunk("EXIF", exif));
        }

        // XMP
        if (replaceXmp)
        {
            webp.Chunks.RemoveAll(c => c.Type == "XMP ");
            if (xmp is not null)
            {
                if (!webp.HasHeader)
                    webp.EnsureHeader();
                webp.Chunks.Add(new MetadataWebpChunk("XMP ", xmp));
            }
        }
        else
        {
            // Keep the XMP where it was, but after the EXIF the format wants it to come last.
            var kept = webp.Chunks.FirstOrDefault(c => c.Type == "XMP ");
            if (kept is not null)
            {
                webp.Chunks.Remove(kept);
                webp.Chunks.Add(kept);
            }
        }

        if (webp.HasHeader)
        {
            var header = webp.Chunks[0];
            var flags = header.Data[0];
            flags = (byte)(webp.Chunks.Any(c => c.Type == "EXIF") ? flags | MetadataWebp.ExifFlag : flags & ~MetadataWebp.ExifFlag);
            flags = (byte)(webp.Chunks.Any(c => c.Type == "XMP ") ? flags | MetadataWebp.XmpFlag : flags & ~MetadataWebp.XmpFlag);
            header.Data[0] = flags;
        }
    }

    private static void EnsureSamePicture(MetadataWebp original, MetadataWebp written)
    {
        var a = original.Chunks.Where(c => Picture.Contains(c.Type) && c.Type != "VP8X").ToList();
        var b = written.Chunks.Where(c => Picture.Contains(c.Type) && c.Type != "VP8X").ToList();
        var same = a.Count == b.Count;
        for (var i = 0; same && i < a.Count; i++)
            same = a[i].Type == b[i].Type && a[i].Data.AsSpan().SequenceEqual(b[i].Data);
        if (!same)
            throw new PanelException("The picture changed while its tags were rewritten; nothing was saved.");
    }
}
