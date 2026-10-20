// Mandarina: the WebP side of the METADATA tool. A WebP is a RIFF file: a list of chunks ('VP8 ', 'VP8L', 'ALPH', 'ANMF'…,
// 'ICCP', 'EXIF', 'XMP '), with a 'VP8X' header when it has more than the picture. The tool changes only the 'EXIF' and
// 'XMP ' chunks and the flags that announce them; the compressed picture or animation is copied byte for byte.

using System.Buffers.Binary;
using System.Text;

namespace Filee.App.Mandarina.Tools;

internal sealed class MetadataWebpChunk(string type, byte[] data)
{
    public string Type { get; } = type;

    public byte[] Data { get; set; } = data;

    public int Length => 8 + Data.Length + (Data.Length & 1);
}

internal sealed class MetadataWebp
{
    public const byte IccFlag = 0x20;
    public const byte AlphaFlag = 0x10;
    public const byte ExifFlag = 0x08;
    public const byte XmpFlag = 0x04;
    public const byte AnimationFlag = 0x02;

    private MetadataWebp(List<MetadataWebpChunk> chunks)
    {
        Chunks = chunks;
    }

    public List<MetadataWebpChunk> Chunks { get; }

    public bool HasHeader => Chunks.Count > 0 && Chunks[0].Type == "VP8X";

    public static bool LooksLikeWebp(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>Splits <paramref name="data"/> into chunks. Throws <see cref="InvalidDataException"/> for a file that is not a complete WebP.</summary>
    public static MetadataWebp Parse(byte[] data)
    {
        if (!LooksLikeWebp(data))
            throw new InvalidDataException("This is not a WebP picture.");
        var declared = (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) + 8;
        var end = Math.Min(declared, data.Length);
        var chunks = new List<MetadataWebpChunk>();
        var pos = 12;
        while (pos + 8 <= end)
        {
            var type = Encoding.Latin1.GetString(data, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (pos + 8L + size > data.Length)
                throw new InvalidDataException("The WebP is cut short.");
            chunks.Add(new MetadataWebpChunk(type, data.AsSpan(pos + 8, (int)size).ToArray()));
            pos += 8 + (int)size + (int)(size & 1);
        }
        if (chunks.Count == 0)
            throw new InvalidDataException("The WebP has no picture.");
        return new MetadataWebp(chunks);
    }

    public byte[] ToBytes()
    {
        var body = Chunks.Sum(c => c.Length);
        var result = new byte[12 + body];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(4 + body));
        "WEBP"u8.CopyTo(result.AsSpan(8));
        var pos = 12;
        foreach (var chunk in Chunks)
        {
            Encoding.Latin1.GetBytes(chunk.Type, result.AsSpan(pos, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), (uint)chunk.Data.Length);
            chunk.Data.CopyTo(result, pos + 8);
            pos += chunk.Length;
        }
        return result;
    }

    /// <summary>The width and height of the picture of a simple WebP (a lossy 'VP8 ' or a lossless 'VP8L' chunk); null when unknown.</summary>
    public (int Width, int Height)? PictureSize()
    {
        foreach (var chunk in Chunks)
        {
            var d = chunk.Data;
            if (chunk.Type == "VP8 " && d.Length >= 10 && d[3] == 0x9D && d[4] == 0x01 && d[5] == 0x2A)
                return ((d[6] | d[7] << 8) & 0x3FFF, (d[8] | d[9] << 8) & 0x3FFF);
            if (chunk.Type == "VP8L" && d.Length >= 5 && d[0] == 0x2F)
            {
                var bits = (uint)(d[1] | d[2] << 8 | d[3] << 16 | d[4] << 24);
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
        }
        return null;
    }

    /// <summary>
    /// Makes sure the file has a 'VP8X' header (a simple WebP gets one, with the size of its picture) and returns it. Throws
    /// <see cref="InvalidDataException"/> when the size cannot be found.
    /// </summary>
    public MetadataWebpChunk EnsureHeader()
    {
        if (HasHeader)
            return Chunks[0];
        var (width, height) = PictureSize() ?? throw new InvalidDataException("The WebP picture could not be measured.");
        var flags = Chunks.Any(c => c.Type == "ALPH") || Chunks.Any(c => c.Type == "VP8L" && c.Data.Length >= 5 && (c.Data[4] & 0x10) != 0) ? AlphaFlag : (byte)0;
        var data = new byte[10];
        data[0] = flags;
        data[4] = (byte)(width - 1);
        data[5] = (byte)((width - 1) >> 8);
        data[6] = (byte)((width - 1) >> 16);
        data[7] = (byte)(height - 1);
        data[8] = (byte)((height - 1) >> 8);
        data[9] = (byte)((height - 1) >> 16);
        var header = new MetadataWebpChunk("VP8X", data);
        Chunks.Insert(0, header);
        return header;
    }
}
