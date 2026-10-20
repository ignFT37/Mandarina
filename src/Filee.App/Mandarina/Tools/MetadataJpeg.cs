// Mandarina: the JPEG side of the METADATA tool. A JPEG is a list of segments (SOI, APPn, COM, DQT, SOF, SOS + compressed data,
// EOI): the tool reads them, changes only the segments that carry metadata and writes the same compressed data back, byte for
// byte, so the picture is never decoded or re-encoded and its quality cannot change.

using System.Buffers.Binary;

namespace Filee.App.Mandarina.Tools;

/// <summary>One marker segment of a JPEG, or the compressed data that follows an SOS header.</summary>
internal sealed class MetadataJpegSegment(byte marker, byte[] payload, byte[]? entropy = null)
{
    public byte Marker { get; } = marker;

    /// <summary>The bytes after the two length bytes (empty for the markers that have no length).</summary>
    public byte[] Payload { get; set; } = payload;

    /// <summary>For SOS: the entropy-coded data that follows the header, up to the next marker.</summary>
    public byte[]? Entropy { get; } = entropy;

    public bool HasLength => Marker is not (0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7));

    public bool Is(byte marker, ReadOnlySpan<byte> prefix) => Marker == marker && Payload.AsSpan().StartsWith(prefix);

    public bool IsExif => Is(0xE1, "Exif\0\0"u8);

    public bool IsXmp => Is(0xE1, "http://ns.adobe.com/xap/1.0/\0"u8);

    public bool IsExtendedXmp => Is(0xE1, "http://ns.adobe.com/xmp/extension/\0"u8);

    public bool IsIcc => Is(0xE2, "ICC_PROFILE\0"u8);

    public bool IsMpf => Is(0xE2, "MPF\0"u8);

    public bool IsJfif => Is(0xE0, "JFIF\0"u8);

    public bool IsAdobe => Is(0xEE, "Adobe"u8);

    public bool IsPhotoshop => Is(0xED, MetadataIptc.PhotoshopHeader);

    public bool IsComment => Marker == 0xFE;

    public bool IsApp => Marker is >= 0xE0 and <= 0xEF;

    public int Length => 2 + (HasLength ? 2 + Payload.Length : 0) + (Entropy?.Length ?? 0);
}

internal sealed class MetadataJpeg
{
    /// <summary>The identifier XMP carries in its APP1 segment.</summary>
    public static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    private MetadataJpeg(List<MetadataJpegSegment> segments, byte[] trailer, bool hasEoi)
    {
        Segments = segments;
        Trailer = trailer;
        HasEoi = hasEoi;
    }

    /// <summary>SOI … EOI in file order (the compressed data of each scan sits inside its SOS segment).</summary>
    public List<MetadataJpegSegment> Segments { get; }

    /// <summary>What follows the EOI: nothing, or the extra pictures of a motion photo, depth map or gain map.</summary>
    public byte[] Trailer { get; }

    public bool HasEoi { get; }

    public static bool LooksLikeJpeg(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>Splits <paramref name="data"/> into segments. Throws <see cref="InvalidDataException"/> when it is not a JPEG that can be followed to its end.</summary>
    public static MetadataJpeg Parse(byte[] data)
    {
        if (!LooksLikeJpeg(data))
            throw new InvalidDataException("This is not a JPEG picture.");
        var segments = new List<MetadataJpegSegment> { new(0xD8, []) };
        var pos = 2;
        while (pos < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                // Padding between segments (some writers leave zeros): skip to the next marker.
                pos++;
                continue;
            }
            while (pos < data.Length && data[pos] == 0xFF)
                pos++;
            if (pos >= data.Length)
                break;
            var marker = data[pos++];
            if (marker == 0x00)
                continue;
            if (marker == 0xD9)
            {
                segments.Add(new MetadataJpegSegment(0xD9, []));
                return new MetadataJpeg(segments, data[pos..], hasEoi: true);
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                segments.Add(new MetadataJpegSegment(marker, []));
                continue;
            }
            if (pos + 2 > data.Length)
                throw new InvalidDataException("The JPEG is cut short.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            if (length < 2 || pos + length > data.Length)
                throw new InvalidDataException("The JPEG is cut short.");
            var payload = data.AsSpan(pos + 2, length - 2).ToArray();
            pos += length;
            if (marker != 0xDA)
            {
                segments.Add(new MetadataJpegSegment(marker, payload));
                continue;
            }
            var entropyEnd = pos;
            while (entropyEnd < data.Length)
            {
                if (data[entropyEnd] != 0xFF)
                {
                    entropyEnd++;
                    continue;
                }
                if (entropyEnd + 1 >= data.Length)
                {
                    entropyEnd = data.Length;
                    break;
                }
                var next = data[entropyEnd + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
                {
                    entropyEnd += 2;
                    continue;
                }
                if (next == 0xFF)
                {
                    entropyEnd++;
                    continue;
                }
                break;
            }
            segments.Add(new MetadataJpegSegment(0xDA, payload, data.AsSpan(pos, entropyEnd - pos).ToArray()));
            pos = entropyEnd;
        }
        return new MetadataJpeg(segments, [], hasEoi: false);
    }

    /// <summary>The segments and then <paramref name="trailer"/> as one file.</summary>
    public static byte[] Serialize(IReadOnlyList<MetadataJpegSegment> segments, byte[] trailer)
    {
        var length = trailer.Length;
        foreach (var segment in segments)
            length += segment.Length;
        var result = new byte[length];
        var pos = 0;
        foreach (var segment in segments)
        {
            result[pos++] = 0xFF;
            result[pos++] = segment.Marker;
            if (segment.HasLength)
            {
                if (segment.Payload.Length > 65533)
                    throw new InvalidDataException("A JPEG segment would be larger than 64 KB.");
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), (ushort)(segment.Payload.Length + 2));
                pos += 2;
                segment.Payload.CopyTo(result, pos);
                pos += segment.Payload.Length;
            }
            if (segment.Entropy is { } entropy)
            {
                entropy.CopyTo(result, pos);
                pos += entropy.Length;
            }
        }
        trailer.CopyTo(result, pos);
        return result;
    }

    /// <summary>The EXIF block (a TIFF structure) of the first Exif segment; null when there is none.</summary>
    public byte[]? ExifPayload()
    {
        var segment = Segments.FirstOrDefault(s => s.IsExif);
        return segment is null ? null : segment.Payload.AsSpan(6).ToArray();
    }

    public static MetadataJpegSegment ExifSegment(byte[] tiff) => new(0xE1, [.. "Exif\0\0"u8, .. tiff]);

    /// <summary>The XMP packet of the first XMP segment; null when there is none.</summary>
    public byte[]? XmpPayload()
    {
        var segment = Segments.FirstOrDefault(s => s.IsXmp);
        return segment is null ? null : segment.Payload.AsSpan(XmpHeader.Length).ToArray();
    }

    public static MetadataJpegSegment XmpSegment(byte[] packet) => new(0xE1, [.. XmpHeader, .. packet]);

    /// <summary>
    /// Brings the Multi-Picture Format segment of <paramref name="output"/> (a phone's depth map, gain map or second shot after
    /// the first picture) up to date after the segments in front of it changed in size: the first picture's length and where
    /// the others start. <paramref name="oldHeaderAt"/> is where the MP header sat in the original, <paramref name="newHeaderAt"/>
    /// where it sits now; the first picture ended at <paramref name="oldEnd"/> and ends at <paramref name="newEnd"/>.
    /// </summary>
    public static void FixMultiPicture(byte[] output, int newHeaderAt, int oldHeaderAt, int oldEnd, int newEnd)
    {
        if (newHeaderAt < 0 || newHeaderAt + 8 > output.Length)
            return;
        var big = output[newHeaderAt] == 'M';
        if (!(big || output[newHeaderAt] == 'I'))
            return;
        ushort U16(int at) => big ? BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(at));
        uint U32(int at) => big ? BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at));
        void Write32(int at, uint value)
        {
            if (big)
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(at), value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
        }

        var ifd = newHeaderAt + (long)U32(newHeaderAt + 4);
        if (ifd < newHeaderAt || ifd + 2 > output.Length)
            return;
        var count = U16((int)ifd);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > output.Length)
                return;
            if (U16(entry) != 0xB002)
                continue;
            var size = U32(entry + 4);
            var at = newHeaderAt + (long)U32(entry + 8);
            if (size < 16 || at < newHeaderAt || at + size > output.Length)
                return;
            var shift = newEnd - oldEnd;
            for (var item = 0; item + 16 <= size; item += 16)
            {
                var sizeAt = (int)at + item + 4;
                var offsetAt = sizeAt + 4;
                if (item == 0)
                {
                    Write32(sizeAt, (uint)newEnd);
                    continue;
                }
                var absolute = oldHeaderAt + (long)U32(offsetAt);
                if (absolute >= oldEnd)
                    Write32(offsetAt, (uint)(absolute + shift - newHeaderAt));
            }
            return;
        }
    }
}
