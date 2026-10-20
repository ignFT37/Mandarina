// Mandarina: the JPEG driver of the METADATA tool. It reads the metadata segments (EXIF and XMP in APP1, IPTC in APP13, the
// comments) into a PhotoMetadataTags, and writes the file again with only those segments changed. The compressed picture is
// copied as it is. "Strip" keeps what the picture needs to look the same (JFIF, the colour profile, Adobe's colour transform and
// the orientation) and drops everything else, including the extra pictures a phone appends after the first one.

using System.Text;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal static class PhotoMetadataJpeg
{
    /// <summary>An XMP packet does not fit in one segment beyond this (the segment holds 65533 bytes, minus the identifier).</summary>
    private const int MaxXmp = 65533 - 29;

    private const int MaxIptc = 65533 - 14;

    internal static PhotoMetadataTags Load(MetadataJpeg jpeg)
    {
        var tags = new PhotoMetadataTags { AttachedBytes = jpeg.Trailer.Length };
        if (jpeg.ExifPayload() is { } exif)
            tags.Exif = OpenExif(exif);
        if (jpeg.XmpPayload() is { } xmp)
        {
            tags.Xmp = MetadataXmp.TryParse(xmp);
            tags.XmpUnreadable = tags.Xmp is null;
        }
        if (jpeg.Segments.FirstOrDefault(s => s.IsPhotoshop) is { } photoshop)
        {
            var iptc = MetadataIptc.FromPhotoshop(photoshop.Payload.AsSpan(MetadataIptc.PhotoshopHeader.Length));
            tags.Iptc = iptc;
            tags.IptcUnreadable = iptc is null;
        }
        foreach (var comment in jpeg.Segments.Where(s => s.IsComment))
            tags.Texts.Add(new PhotoMetadataText(null, Encoding.UTF8.GetString(comment.Payload).TrimEnd('\0'), comment));
        return tags;
    }

    /// <summary>The EXIF block as an editable TIFF; null when the block is damaged (the file is then treated as having none).</summary>
    private static MetadataTiff? OpenExif(byte[] payload)
    {
        var stream = new MemoryStream();
        stream.Write(payload);
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
        var jpeg = MetadataJpeg.Parse(original);
        var hadExif = jpeg.ExifPayload() is not null;
        var tags = Load(jpeg);
        if (hadExif && tags.Exif is null)
            throw new PanelException("The EXIF of this photo is damaged: use \"Remove all\" instead.");
        PhotoMetadata.Apply(tags, changes);

        byte[]? exifBytes = null;
        if (tags.Exif is { } exif)
        {
            exif.Commit();
            exifBytes = exif.ToArray();
            if (exifBytes.Length > MetadataTiff.MaxJpegPayload)
            {
                // Too big for one segment (a big thumbnail plus what was added): the thumbnail and the maker note go.
                exifBytes = exif.Compacted().ToArray();
                if (exifBytes.Length > MetadataTiff.MaxJpegPayload)
                    throw new PanelException("The tags of this photo do not fit in its EXIF block.");
            }
        }

        var segments = new List<MetadataJpegSegment>();
        var placedExif = false;
        var placedIptc = false;
        foreach (var segment in jpeg.Segments)
        {
            if (segment.IsExif)
            {
                // The first Exif segment is replaced by the new block, any other is dropped.
                if (!placedExif && exifBytes is not null)
                    segments.Add(MetadataJpeg.ExifSegment(exifBytes));
                placedExif = true;
                continue;
            }
            if (segment.IsXmp)
            {
                if (tags.XmpChanged && tags.Xmp is null)
                    continue;
                if (tags.XmpChanged && tags.Xmp is { } xmp)
                {
                    var packet = xmp.ToPacket();
                    if (packet.Length > MaxXmp)
                        throw new PanelException("The XMP of this photo is too large to be written back.");
                    segments.Add(MetadataJpeg.XmpSegment(packet));
                    continue;
                }
            }
            else if (segment.IsPhotoshop && !placedIptc)
            {
                placedIptc = true;
                if (tags.IptcChanged && tags.Iptc is null)
                    continue;
                if (tags.IptcChanged && tags.Iptc is { } iptc)
                {
                    var block = iptc.ToPhotoshop();
                    if (block.Length > MaxIptc)
                        throw new PanelException("The IPTC of this photo is too large to be written back.");
                    if (block.Length > 0)
                        segments.Add(new MetadataJpegSegment(0xED, [.. MetadataIptc.PhotoshopHeader, .. block]));
                    continue;
                }
            }
            else if (segment.IsComment)
            {
                var text = tags.Texts.First(t => ReferenceEquals(t.Source, segment));
                if (text.Removed)
                    continue;
                if (text.Changed)
                {
                    segments.Add(new MetadataJpegSegment(0xFE, Encoding.UTF8.GetBytes(text.Value)));
                    continue;
                }
            }
            segments.Add(segment);
        }
        if (!placedExif && exifBytes is not null)
            segments.Insert(InsertionPoint(segments), MetadataJpeg.ExifSegment(exifBytes));

        var result = Assemble(original, jpeg, segments, jpeg.Trailer);
        EnsureSamePicture(jpeg, MetadataJpeg.Parse(result));
        return result;
    }

    /// <summary>Right after the SOI and the JFIF header (a photo with a JFIF segment keeps it first).</summary>
    private static int InsertionPoint(List<MetadataJpegSegment> segments) => segments.Count > 1 && segments[1].IsJfif ? 2 : 1;

    /// <summary>Writes the segments and the trailer, and brings the multi-picture index of a phone photo up to date.</summary>
    private static byte[] Assemble(byte[] original, MetadataJpeg parsed, List<MetadataJpegSegment> segments, byte[] trailer)
    {
        var result = MetadataJpeg.Serialize(segments, trailer);
        var oldMpf = parsed.Segments.FirstOrDefault(s => s.IsMpf);
        var newMpf = segments.FirstOrDefault(s => s.IsMpf);
        if (oldMpf is not null && newMpf is not null && trailer.Length > 0)
        {
            var oldHeader = OffsetOf(parsed.Segments, oldMpf) + 8;
            var newHeader = OffsetOf(segments, newMpf) + 8;
            var oldEnd = original.Length - parsed.Trailer.Length;
            var newEnd = result.Length - trailer.Length;
            if (oldHeader != newHeader || oldEnd != newEnd)
                MetadataJpeg.FixMultiPicture(result, newHeader, oldHeader, oldEnd, newEnd);
        }
        return result;
    }

    private static int OffsetOf(IReadOnlyList<MetadataJpegSegment> segments, MetadataJpegSegment target)
    {
        var offset = 0;
        foreach (var segment in segments)
        {
            if (ReferenceEquals(segment, target))
                return offset;
            offset += segment.Length;
        }
        return -1;
    }

    // ───────── Strip ─────────

    internal static byte[] Strip(byte[] original)
    {
        var jpeg = MetadataJpeg.Parse(original);
        var tags = Load(jpeg);
        var orientation = tags.Exif?.GetInt(MetadataTiffIfd.Main, MetadataTiff.Orientation) ?? 1;

        var segments = new List<MetadataJpegSegment>();
        foreach (var segment in jpeg.Segments)
        {
            if (segment.IsJfif)
            {
                // The JFIF header is kept for the pixel density; the small picture some of them carry is a copy of the photo.
                var header = segment.Payload.Length >= 14 ? segment.Payload.AsSpan(0, 14).ToArray() : segment.Payload;
                if (header.Length >= 14)
                {
                    header[12] = 0;
                    header[13] = 0;
                }
                segments.Add(new MetadataJpegSegment(0xE0, header));
            }
            else if (segment.IsIcc || segment.IsAdobe || !(segment.IsApp || segment.IsComment))
            {
                segments.Add(segment);
            }
        }

        // The photo is stored in the sensor's orientation and EXIF says how to turn it; without that line a portrait photo would
        // show lying on its side, and turning the pixels would mean recompressing them. It is a display instruction, not data.
        if (orientation is > 1 and <= 8)
        {
            var exif = MetadataTiff.CreateEmpty(tags.Exif?.BigEndian ?? true);
            exif.SetShort(MetadataTiffIfd.Main, MetadataTiff.Orientation, (ushort)orientation);
            exif.Commit();
            segments.Insert(InsertionPoint(segments), MetadataJpeg.ExifSegment(exif.ToArray()));
        }
        // Everything after the picture (a motion photo's video, a depth map, the unedited original) goes with the metadata.
        var result = MetadataJpeg.Serialize(segments, []);
        EnsureSamePicture(jpeg, MetadataJpeg.Parse(result));
        return result;
    }

    /// <summary>A last check on what is written: the picture's own segments and compressed data must be the ones of the original.</summary>
    internal static void EnsureSamePicture(MetadataJpeg original, MetadataJpeg written)
    {
        static List<MetadataJpegSegment> Picture(MetadataJpeg jpeg) => jpeg.Segments.Where(s => !s.IsApp && !s.IsComment).ToList();
        var a = Picture(original);
        var b = Picture(written);
        var same = a.Count == b.Count;
        for (var i = 0; same && i < a.Count; i++)
            same = a[i].Marker == b[i].Marker && a[i].Payload.AsSpan().SequenceEqual(b[i].Payload) && (a[i].Entropy ?? []).AsSpan().SequenceEqual(b[i].Entropy ?? []);
        if (!same)
            throw new PanelException("The picture changed while its tags were rewritten; nothing was saved.");
    }
}
