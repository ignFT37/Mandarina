// Mandarina: the PNG driver of the METADATA tool. EXIF lives in an eXIf chunk, XMP in an iTXt chunk, the rest in text chunks
// (Author, Description, Copyright, Software, Creation Time…) and a tIME chunk. Only those chunks are rewritten; the picture
// chunks, the colour profile, the palette and the frames of an animation are copied unchanged.

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal static class PhotoMetadataPng
{
    /// <summary>The chunks that say how to show the picture and nothing about its history: they stay when everything else goes.</summary>
    private static readonly HashSet<string> Display =
    [
        "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "hIST", "pHYs", "sPLT", "cICP", "mDCv", "cLLi", "acTL", "fcTL", "fdAT", "sTER", "pCAL", "oFFs", "sCAL",
    ];

    /// <summary>The chunks that make up the picture itself.</summary>
    private static readonly HashSet<string> Picture = ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "acTL", "fcTL", "fdAT", "CgBI"];

    internal static PhotoMetadataTags Load(MetadataPng png)
    {
        var tags = new PhotoMetadataTags();
        foreach (var chunk in png.Chunks)
        {
            switch (chunk.Type)
            {
                case "eXIf" when tags.Exif is null:
                    tags.Exif = OpenExif(chunk.Data);
                    break;
                case "tEXt" or "zTXt":
                    if (MetadataPng.ReadText(chunk) is { } plain)
                        tags.Texts.Add(new PhotoMetadataText(plain.Keyword, plain.Text, chunk));
                    break;
                case "iTXt":
                    if (IsXmp(chunk))
                    {
                        if (tags.Xmp is null && !tags.XmpUnreadable)
                        {
                            tags.Xmp = MetadataPng.ReadTextBytes(chunk) is { } packet ? MetadataXmp.TryParse(packet) : null;
                            tags.XmpUnreadable = tags.Xmp is null;
                        }
                    }
                    else if (MetadataPng.ReadText(chunk) is { } international)
                    {
                        tags.Texts.Add(new PhotoMetadataText(international.Keyword, international.Text, chunk));
                    }
                    break;
            }
        }
        return tags;
    }

    private static bool IsXmp(MetadataPngChunk chunk) =>
        chunk.Type == "iTXt" && chunk.Data.Length > MetadataPng.XmpKeyword.Length && System.Text.Encoding.Latin1.GetString(chunk.Data, 0, MetadataPng.XmpKeyword.Length) == MetadataPng.XmpKeyword
        && chunk.Data[MetadataPng.XmpKeyword.Length] == 0;

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

    // ───────── Edit ─────────

    internal static byte[] Edit(byte[] original, IReadOnlyDictionary<PhotoMetadataField, string?> changes)
    {
        var png = MetadataPng.Parse(original);
        var tags = Load(png);
        var hadExif = png.Chunks.Any(c => c.Type == "eXIf");
        if (hadExif && tags.Exif is null)
            throw new PanelException("The EXIF of this picture is damaged: use \"Remove all\" instead.");
        PhotoMetadata.Apply(tags, changes);

        byte[]? exifBytes = null;
        if (tags.Exif is { } exif)
        {
            exif.Commit();
            exifBytes = exif.ToArray();
        }

        var chunks = new List<MetadataPngChunk>();
        var placedExif = false;
        foreach (var chunk in png.Chunks)
        {
            if (chunk.Type == "eXIf")
            {
                if (!placedExif && exifBytes is not null)
                    chunks.Add(MetadataPng.ExifChunk(exifBytes));
                placedExif = true;
                continue;
            }
            if (IsXmp(chunk))
            {
                if (!tags.XmpChanged)
                {
                    chunks.Add(chunk);
                    continue;
                }
                if (tags.Xmp is { } xmp)
                    chunks.Add(MetadataPng.InternationalText(MetadataPng.XmpKeyword, xmp.ToPacket()));
                continue;
            }
            if (chunk.Type == "tIME" && tags.DateTouched)
                continue;
            if (chunk.Type is "tEXt" or "zTXt" or "iTXt" && tags.Texts.FirstOrDefault(t => ReferenceEquals(t.Source, chunk)) is { } text)
            {
                if (text.Removed)
                    continue;
                if (text.Changed)
                {
                    chunks.Add(MetadataPng.TextChunk(text.Key!, text.Value));
                    continue;
                }
            }
            chunks.Add(chunk);
        }
        if (!placedExif && exifBytes is not null)
            chunks.Insert(chunks.FindIndex(c => c.Type == "IDAT"), MetadataPng.ExifChunk(exifBytes));

        var result = MetadataPng.Serialize(chunks);
        EnsureSamePicture(png, MetadataPng.Parse(result));
        return result;
    }

    // ───────── Strip ─────────

    internal static byte[] Strip(byte[] original)
    {
        var png = MetadataPng.Parse(original);
        var tags = Load(png);
        var orientation = tags.Exif?.GetInt(MetadataTiffIfd.Main, MetadataTiff.Orientation) ?? 1;

        var chunks = png.Chunks.Where(c => c.IsCritical || Display.Contains(c.Type)).ToList();
        // The same reason as in a JPEG: the orientation tells how to show the pixels, and turning them would recompress them.
        if (orientation is > 1 and <= 8)
        {
            var exif = MetadataTiff.CreateEmpty(tags.Exif?.BigEndian ?? true);
            exif.SetShort(MetadataTiffIfd.Main, MetadataTiff.Orientation, (ushort)orientation);
            exif.Commit();
            chunks.Insert(chunks.FindIndex(c => c.Type == "IDAT"), MetadataPng.ExifChunk(exif.ToArray()));
        }
        var result = MetadataPng.Serialize(chunks);
        EnsureSamePicture(png, MetadataPng.Parse(result));
        return result;
    }

    /// <summary>A last check on what is written: the chunks that make up the picture must be the original's, in the same order.</summary>
    private static void EnsureSamePicture(MetadataPng original, MetadataPng written)
    {
        var a = original.Chunks.Where(c => Picture.Contains(c.Type)).ToList();
        var b = written.Chunks.Where(c => Picture.Contains(c.Type)).ToList();
        var same = a.Count == b.Count;
        for (var i = 0; same && i < a.Count; i++)
            same = a[i].Type == b[i].Type && a[i].Data.AsSpan().SequenceEqual(b[i].Data);
        if (!same)
            throw new PanelException("The picture changed while its tags were rewritten; nothing was saved.");
    }
}
