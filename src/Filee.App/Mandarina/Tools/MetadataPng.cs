// Mandarina: the PNG side of the METADATA tool. A PNG is a list of chunks (IHDR, IDAT…, text, eXIf, tIME…) each with its own
// CRC: the tool reads and writes the chunks that carry metadata and copies the others (and so the compressed picture, an
// animation's frames and the colour profile) byte for byte.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Filee.App.Mandarina.Tools;

internal sealed class MetadataPngChunk(string type, byte[] data)
{
    public string Type { get; } = type;

    public byte[] Data { get; set; } = data;

    public bool IsCritical => char.IsAsciiLetterUpper(Type[0]);

    public int Length => 12 + Data.Length;
}

internal sealed class MetadataPng
{
    /// <summary>The XMP keyword of an iTXt chunk.</summary>
    public const string XmpKeyword = "XML:com.adobe.xmp";

    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    private MetadataPng(List<MetadataPngChunk> chunks)
    {
        Chunks = chunks;
    }

    public List<MetadataPngChunk> Chunks { get; }

    public static bool LooksLikePng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    /// <summary>Splits <paramref name="data"/> into chunks, checking each CRC. Throws <see cref="InvalidDataException"/> for a damaged file.</summary>
    public static MetadataPng Parse(byte[] data)
    {
        if (!LooksLikePng(data))
            throw new InvalidDataException("This is not a PNG picture.");
        var chunks = new List<MetadataPngChunk>();
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (length > int.MaxValue || pos + 12L + length > data.Length)
                throw new InvalidDataException("The PNG is cut short.");
            var type = Encoding.Latin1.GetString(data, pos + 4, 4);
            var payload = data.AsSpan(pos + 8, (int)length).ToArray();
            var stored = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8 + (int)length));
            if (Crc(data.AsSpan(pos + 4, 4 + (int)length)) != stored)
                throw new InvalidDataException("A chunk of the PNG is damaged.");
            chunks.Add(new MetadataPngChunk(type, payload));
            pos += 12 + (int)length;
            if (type == "IEND")
                break;
        }
        if (chunks.Count == 0 || chunks[0].Type != "IHDR" || chunks[^1].Type != "IEND")
            throw new InvalidDataException("The PNG is cut short.");
        return new MetadataPng(chunks);
    }

    public byte[] ToBytes() => Serialize(Chunks);

    public static byte[] Serialize(IReadOnlyList<MetadataPngChunk> chunks)
    {
        var length = Signature.Length + chunks.Sum(c => c.Length);
        var result = new byte[length];
        Signature.CopyTo(result, 0);
        var pos = 8;
        foreach (var chunk in chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos), (uint)chunk.Data.Length);
            Encoding.Latin1.GetBytes(chunk.Type, result.AsSpan(pos + 4, 4));
            chunk.Data.CopyTo(result, pos + 8);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos + 8 + chunk.Data.Length), Crc(result.AsSpan(pos + 4, 4 + chunk.Data.Length)));
            pos += chunk.Length;
        }
        return result;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in bytes)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    // ───────── Text chunks ─────────

    /// <summary>The keyword and text of a tEXt, zTXt or iTXt chunk; null when the chunk is damaged.</summary>
    public static (string Keyword, string Text)? ReadText(MetadataPngChunk chunk)
    {
        var data = chunk.Data;
        var nul = Array.IndexOf(data, (byte)0);
        if (nul < 1 || nul > 79)
            return null;
        var keyword = Encoding.Latin1.GetString(data, 0, nul);
        try
        {
            switch (chunk.Type)
            {
                case "tEXt":
                    return (keyword, Encoding.Latin1.GetString(data, nul + 1, data.Length - nul - 1));
                case "zTXt":
                    return nul + 2 > data.Length ? null : (keyword, Encoding.Latin1.GetString(Inflate(data.AsSpan(nul + 2))));
                case "iTXt":
                {
                    if (nul + 3 > data.Length)
                        return null;
                    var compressed = data[nul + 1] == 1;
                    var pos = nul + 3;
                    var languageEnd = Array.IndexOf(data, (byte)0, pos);
                    if (languageEnd < 0)
                        return null;
                    var translatedEnd = Array.IndexOf(data, (byte)0, languageEnd + 1);
                    if (translatedEnd < 0)
                        return null;
                    var body = data.AsSpan(translatedEnd + 1);
                    return (keyword, Encoding.UTF8.GetString(compressed ? Inflate(body) : body.ToArray()));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
        {
            return null;
        }
        return null;
    }

    /// <summary>The raw bytes of an iTXt chunk's text (the XMP packet), or null.</summary>
    public static byte[]? ReadTextBytes(MetadataPngChunk chunk)
    {
        if (chunk.Type != "iTXt")
            return null;
        var data = chunk.Data;
        var nul = Array.IndexOf(data, (byte)0);
        if (nul < 1 || nul + 3 > data.Length)
            return null;
        try
        {
            var compressed = data[nul + 1] == 1;
            var languageEnd = Array.IndexOf(data, (byte)0, nul + 3);
            var translatedEnd = languageEnd < 0 ? -1 : Array.IndexOf(data, (byte)0, languageEnd + 1);
            if (translatedEnd < 0)
                return null;
            var body = data.AsSpan(translatedEnd + 1);
            return compressed ? Inflate(body) : body.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Most text a compressed chunk may unpack to: a few bytes of zlib can say gigabytes.</summary>
    private const int MaxUnpacked = 16 << 20;

    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[1 << 16];
        int read;
        while ((read = zlib.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxUnpacked)
                throw new InvalidDataException("A compressed text of the PNG is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>A tEXt chunk when the text is Latin-1 (all that old readers understand), an uncompressed iTXt chunk otherwise.</summary>
    public static MetadataPngChunk TextChunk(string keyword, string text)
    {
        var latin1 = Encoding.Latin1.GetBytes(text);
        if (Encoding.Latin1.GetString(latin1) == text && !text.Contains('\0'))
            return new MetadataPngChunk("tEXt", [.. Encoding.Latin1.GetBytes(keyword), 0, .. latin1]);
        return InternationalText(keyword, Encoding.UTF8.GetBytes(text));
    }

    public static MetadataPngChunk InternationalText(string keyword, byte[] utf8) =>
        new("iTXt", [.. Encoding.Latin1.GetBytes(keyword), 0, 0, 0, 0, 0, .. utf8]);

    /// <summary>An eXIf chunk: the TIFF block of the EXIF with no "Exif\0\0" in front.</summary>
    public static MetadataPngChunk ExifChunk(byte[] tiff) => new("eXIf", tiff);
}
