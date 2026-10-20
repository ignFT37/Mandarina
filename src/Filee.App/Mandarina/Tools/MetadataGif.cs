// Mandarina: the GIF side of the METADATA tool. A GIF is a header, a colour table and a list of blocks: extensions (graphic
// control, comment, application…) and images. The tool reads comments and the XMP application extension and writes only those
// blocks again; the images (every frame of an animation, with its timing and palette) are copied byte for byte.

using System.Text;

namespace Filee.App.Mandarina.Tools;

internal sealed class MetadataGifBlock(byte kind, byte label, byte[] raw)
{
    /// <summary>0x21 extension, 0x2C image, 0x3B trailer; 0 for the header and the screen descriptor.</summary>
    public byte Kind { get; } = kind;

    /// <summary>For an extension: its label (0xF9 graphic control, 0xFE comment, 0xFF application, 0x01 plain text).</summary>
    public byte Label { get; } = label;

    public byte[] Raw { get; } = raw;

    public bool IsComment => Kind == 0x21 && Label == 0xFE;

    public bool IsApplication => Kind == 0x21 && Label == 0xFF;

    /// <summary>The 8-byte identifier and 3-byte code of an application extension ("NETSCAPE2.0", "XMP DataXMP"…).</summary>
    public string ApplicationId => IsApplication && Raw.Length >= 14 && Raw[2] == 11 ? Encoding.Latin1.GetString(Raw, 3, 11) : "";
}

internal sealed class MetadataGif
{
    public const string XmpApplication = "XMP DataXMP";

    internal MetadataGif(List<MetadataGifBlock> blocks)
    {
        Blocks = blocks;
    }

    public List<MetadataGifBlock> Blocks { get; }

    public static bool LooksLikeGif(ReadOnlySpan<byte> data) => data.Length >= 13 && (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8));

    /// <summary>Splits <paramref name="data"/> into blocks. Throws <see cref="InvalidDataException"/> when the file cannot be followed to its trailer.</summary>
    public static MetadataGif Parse(byte[] data)
    {
        if (!LooksLikeGif(data))
            throw new InvalidDataException("This is not a GIF picture.");
        var blocks = new List<MetadataGifBlock>();
        var packed = data[10];
        var pos = 13 + ((packed & 0x80) != 0 ? 3 * (1 << ((packed & 7) + 1)) : 0);
        if (pos > data.Length)
            throw new InvalidDataException("The GIF is cut short.");
        blocks.Add(new MetadataGifBlock(0, 0, data[..pos]));
        while (pos < data.Length)
        {
            var kind = data[pos];
            if (kind == 0x3B)
            {
                blocks.Add(new MetadataGifBlock(0x3B, 0, [0x3B]));
                return new MetadataGif(blocks);
            }
            var start = pos;
            byte label = 0;
            if (kind == 0x21)
            {
                if (pos + 2 > data.Length)
                    throw new InvalidDataException("The GIF is cut short.");
                label = data[pos + 1];
                pos = SkipSubBlocks(data, pos + 2);
            }
            else if (kind == 0x2C)
            {
                if (pos + 11 > data.Length)
                    throw new InvalidDataException("The GIF is cut short.");
                var local = data[pos + 9];
                pos += 10 + ((local & 0x80) != 0 ? 3 * (1 << ((local & 7) + 1)) : 0) + 1;
                pos = SkipSubBlocks(data, pos);
            }
            else
            {
                throw new InvalidDataException("The GIF has a block that cannot be read.");
            }
            blocks.Add(new MetadataGifBlock(kind, label, data[start..pos]));
        }
        throw new InvalidDataException("The GIF is cut short.");
    }

    private static int SkipSubBlocks(byte[] data, int pos)
    {
        while (true)
        {
            if (pos >= data.Length)
                throw new InvalidDataException("The GIF is cut short.");
            var size = data[pos];
            pos += 1 + size;
            if (size == 0)
                return pos;
        }
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream(Blocks.Sum(b => b.Raw.Length));
        foreach (var block in Blocks)
            stream.Write(block.Raw);
        return stream.ToArray();
    }

    /// <summary>The text of a comment extension (its sub-blocks joined).</summary>
    public static string CommentText(MetadataGifBlock block)
    {
        var bytes = new List<byte>();
        for (var pos = 2; pos < block.Raw.Length && block.Raw[pos] != 0; pos += 1 + block.Raw[pos])
            bytes.AddRange(block.Raw.AsSpan(pos + 1, Math.Min((int)block.Raw[pos], block.Raw.Length - pos - 1)).ToArray());
        try
        {
            return new UTF8Encoding(false, true).GetString([.. bytes]);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString([.. bytes]);
        }
    }

    public static MetadataGifBlock Comment(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var raw = new List<byte> { 0x21, 0xFE };
        for (var i = 0; i < bytes.Length; i += 255)
        {
            var size = Math.Min(255, bytes.Length - i);
            raw.Add((byte)size);
            raw.AddRange(bytes.AsSpan(i, size).ToArray());
        }
        raw.Add(0);
        return new MetadataGifBlock(0x21, 0xFE, [.. raw]);
    }

    /// <summary>The XMP packet of an XMP application extension: what lies between its header and the 258-byte trailer.</summary>
    public static byte[]? XmpPacket(MetadataGifBlock block)
    {
        if (block.ApplicationId != XmpApplication || block.Raw.Length < 14 + 258)
            return null;
        return block.Raw[14..^258];
    }

    public static MetadataGifBlock Xmp(byte[] packet)
    {
        var raw = new List<byte> { 0x21, 0xFF, 11 };
        raw.AddRange(Encoding.Latin1.GetBytes(XmpApplication));
        raw.AddRange(packet);
        // The magic trailer: 01, FF FE … 01, 00 00. Read as sub-blocks, it carries a decoder that does not know XMP safely to the end.
        raw.Add(0x01);
        for (var b = 0xFF; b >= 0x01; b--)
            raw.Add((byte)b);
        raw.Add(0);
        raw.Add(0);
        return new MetadataGifBlock(0x21, 0xFF, [.. raw]);
    }
}
