// Mandarina: a small HEIF box writer for test files. Windows' HEIF encoder and Filee's own writer cannot make what phones and
// cameras do (irot together with an Exif orientation, a thumbnail item, a depth map or an alpha plane as auxiliary pictures, a
// colour description in nclx, a clean-aperture crop, two top-level pictures); this can. The coded pictures are real HEVC taken
// from a HEIC that Windows' encoder wrote (HeicSamples.ReadCoded), so a decoder has to decode them for real.

using System.Buffers.Binary;
using System.Text;
using Filee.Engines.Heic;

namespace Filee.App.Tests;

internal sealed class HeicBuilder
{
    private sealed class Item
    {
        public int Id;
        public string Type = "hvc1";
        public bool Hidden;
        public string? ContentType;
        public byte[] Data = [];
        public readonly List<(int Index, bool Essential)> Properties = [];
    }

    private readonly List<Item> _items = [];
    private readonly List<byte[]> _properties = [];
    private readonly List<(string Type, int From, int[] To)> _references = [];
    private int _primary = 1;

    private int AddProperty(byte[] box)
    {
        _properties.Add(box);
        return _properties.Count;
    }

    /// <summary>A file with one picture.</summary>
    public static HeicBuilder Of(HeicSamples.Coded picture)
    {
        var builder = new HeicBuilder();
        builder._primary = builder.AddPicture(picture, hidden: false);
        return builder;
    }

    /// <summary>
    /// A file whose picture is a grid of <paramref name="columns"/> × <paramref name="rows"/> copies of <paramref name="tile"/>
    /// cropped to <paramref name="width"/> × <paramref name="height"/>, as an iPhone writes big photos.
    /// </summary>
    public static HeicBuilder GridOf(HeicSamples.Coded tile, int columns, int rows, int width, int height)
    {
        var builder = new HeicBuilder();
        var grid = new Item { Id = 1, Type = "grid", Data = GridDescriptor(columns, rows, width, height) };
        builder._items.Add(grid);
        var shared = builder.PictureProperties(tile);
        var tiles = new List<int>();
        for (var i = 0; i < columns * rows; i++)
        {
            var item = new Item { Id = builder._items.Count + 1, Hidden = true, Data = tile.Sample };
            item.Properties.AddRange(shared);
            builder._items.Add(item);
            tiles.Add(item.Id);
        }
        grid.Properties.Add((builder.AddProperty(Full("ispe", 0, [.. U32(width), .. U32(height)])), false));
        grid.Properties.Add((builder.AddProperty(Full("pixi", 0, [3, (byte)tile.BitDepth, (byte)tile.BitDepth, (byte)tile.BitDepth])), false));
        builder._references.Add(("dimg", 1, [.. tiles]));
        return builder;
    }

    private static byte[] GridDescriptor(int columns, int rows, int width, int height)
    {
        var wide = width > ushort.MaxValue || height > ushort.MaxValue;
        return wide
            ? [0, 1, (byte)(rows - 1), (byte)(columns - 1), .. U32(width), .. U32(height)]
            : [0, 0, (byte)(rows - 1), (byte)(columns - 1), .. U16(width), .. U16(height)];
    }

    private (int Index, bool Essential)[] PictureProperties(HeicSamples.Coded picture) =>
    [
        (AddProperty(Full("ispe", 0, [.. U32(picture.Width), .. U32(picture.Height)])), false),
        (AddProperty(Box("hvcC", picture.HvcC)), true),
        (AddProperty(Full("pixi", 0, [3, (byte)picture.BitDepth, (byte)picture.BitDepth, (byte)picture.BitDepth])), false),
    ];

    private int AddPicture(HeicSamples.Coded picture, bool hidden)
    {
        var item = new Item { Id = _items.Count + 1, Hidden = hidden, Data = picture.Sample };
        item.Properties.AddRange(PictureProperties(picture));
        _items.Add(item);
        return item.Id;
    }

    private Item Primary => _items.First(item => item.Id == _primary);

    // ───────── Properties of the primary picture ─────────

    /// <summary>An irot box: the picture is shown turned <paramref name="quarterTurns"/> times 90° counter-clockwise (Exif orientation 6 is 3, 8 is 1, 3 is 2).</summary>
    public HeicBuilder Rotate(int quarterTurns)
    {
        Primary.Properties.Add((AddProperty(Box("irot", [(byte)(quarterTurns & 3)])), true));
        return this;
    }

    /// <summary>An imir box: <paramref name="vertical"/> mirrors top to bottom, otherwise left to right.</summary>
    public HeicBuilder Mirror(bool vertical)
    {
        Primary.Properties.Add((AddProperty(Box("imir", [(byte)(vertical ? 0 : 1)])), true));
        return this;
    }

    /// <summary>A clean-aperture box: the centre <paramref name="width"/> × <paramref name="height"/> pixels of the coded picture are the picture (Windows does it for odd sizes).</summary>
    public HeicBuilder Crop(int width, int height)
    {
        Primary.Properties.Add((AddProperty(Box("clap", [.. U32(width), .. U32(1), .. U32(height), .. U32(1), .. U32(0), .. U32(1), .. U32(0), .. U32(1)])), true));
        return this;
    }

    /// <summary>A colour description in nclx (CICP codes), the other way, besides an ICC profile, a HEIC says what its colours mean.</summary>
    public HeicBuilder Nclx(int primaries, int transfer, int matrix, bool fullRange)
    {
        Primary.Properties.Add((AddProperty(Box("colr", [.. "nclx"u8, .. U16(primaries), .. U16(transfer), .. U16(matrix), (byte)(fullRange ? 0x80 : 0)])), false));
        return this;
    }

    /// <summary>An ICC profile (colr prof), as an iPhone writes Display P3.</summary>
    public HeicBuilder Icc(byte[] profile)
    {
        Primary.Properties.Add((AddProperty(Box("colr", [.. "prof"u8, .. profile])), false));
        return this;
    }

    // ───────── Items that describe or accompany it ─────────

    /// <summary>The Exif item (<paramref name="exif"/> is TIFF data, with or without the "Exif\0\0" prefix).</summary>
    public HeicBuilder Exif(byte[] exif)
    {
        var item = new Item { Id = _items.Count + 1, Type = "Exif", Hidden = true, Data = HeifFile.ExifPayload(exif) ?? throw new InvalidDataException("Not an Exif block.") };
        _items.Add(item);
        _references.Add(("cdsc", item.Id, [_primary]));
        return this;
    }

    /// <summary>An XMP packet in a mime item.</summary>
    public HeicBuilder Xmp(string packet)
    {
        var item = new Item { Id = _items.Count + 1, Type = "mime", ContentType = "application/rdf+xml", Hidden = true, Data = Encoding.UTF8.GetBytes(packet) };
        _items.Add(item);
        _references.Add(("cdsc", item.Id, [_primary]));
        return this;
    }

    /// <summary>A thumbnail: a second, hidden HEVC picture that refers to the primary one (what a phone adds for its gallery).</summary>
    public HeicBuilder Thumbnail(HeicSamples.Coded picture)
    {
        var id = AddPicture(picture, hidden: true);
        _references.Add(("thmb", id, [_primary]));
        return this;
    }

    /// <summary>An auxiliary picture (<paramref name="urn"/> says what it is: depth, alpha…) that refers to the primary one.</summary>
    public HeicBuilder Auxiliary(HeicSamples.Coded picture, string urn)
    {
        var id = AddPicture(picture, hidden: true);
        _items.Single(item => item.Id == id).Properties.Add((AddProperty(Full("auxC", 0, [.. Encoding.ASCII.GetBytes(urn), 0])), false));
        _references.Add(("auxl", id, [_primary]));
        return this;
    }

    public const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";

    public const string AppleDepthUrn = "urn:com:apple:photo:2018:aux:depth";

    /// <summary>A second picture shown on its own (not hidden), as a burst or a Live Photo stores them.</summary>
    public HeicBuilder AlsoShown(HeicSamples.Coded picture)
    {
        AddPicture(picture, hidden: false);
        return this;
    }

    // ───────── The file ─────────

    public byte[] ToBytes()
    {
        var ftyp = Box("ftyp", [.. "heic"u8, .. U32(0), .. "mif1"u8, .. "heic"u8]);
        // The item locations hold absolute offsets, so the header is built twice: its size does not depend on them.
        var header = Meta(new Dictionary<int, long>());
        var offsets = new Dictionary<int, long>();
        long at = ftyp.Length + header.Length + 8; // + the mdat box header
        foreach (var item in _items)
        {
            offsets[item.Id] = at;
            at += item.Data.Length;
        }
        header = Meta(offsets);

        using var file = new MemoryStream();
        file.Write(ftyp);
        file.Write(header);
        file.Write(U32(8 + _items.Sum(item => (long)item.Data.Length)));
        file.Write("mdat"u8);
        foreach (var item in _items)
            file.Write(item.Data);
        return file.ToArray();
    }

    private byte[] Meta(Dictionary<int, long> offsets)
    {
        var iloc = new List<byte> { 0x44, 0x00 };
        iloc.AddRange(U16(_items.Count));
        foreach (var item in _items)
        {
            iloc.AddRange(U16(item.Id));
            iloc.AddRange(U16(0));                        // data reference: this file
            iloc.AddRange(U16(1));                        // one extent
            iloc.AddRange(U32(offsets.GetValueOrDefault(item.Id)));
            iloc.AddRange(U32(item.Data.Length));
        }

        var iinf = new List<byte>(U16(_items.Count));
        foreach (var item in _items)
        {
            var entry = new List<byte>();
            entry.AddRange(U16(item.Id));
            entry.AddRange(U16(0));                       // not protected
            entry.AddRange(Encoding.ASCII.GetBytes(item.Type));
            entry.Add(0);                                 // empty name
            if (item.ContentType is { } contentType)
            {
                entry.AddRange(Encoding.ASCII.GetBytes(contentType));
                entry.Add(0);
            }
            iinf.AddRange(Full("infe", 2, [.. entry], item.Hidden ? 1 : 0));
        }

        var ipma = new List<byte>(U32(_items.Count(item => item.Properties.Count > 0)));
        foreach (var item in _items.Where(item => item.Properties.Count > 0))
        {
            ipma.AddRange(U16(item.Id));
            ipma.Add((byte)item.Properties.Count);
            foreach (var (index, essential) in item.Properties)
                ipma.Add((byte)((essential ? 0x80 : 0) | index));
        }

        var parts = new List<byte[]>
        {
            Full("hdlr", 0, [.. U32(0), .. "pict"u8, .. new byte[12], 0]),
            Box("dinf", Full("dref", 0, [.. U32(1), .. Full("url ", 0, [], 1)])),
            Full("pitm", 0, U16(_primary)),
            Full("iloc", 0, [.. iloc]),
            Full("iinf", 0, [.. iinf]),
        };
        if (_references.Count > 0)
            parts.Add(Full("iref", 0, [.. _references.SelectMany(r => Box(r.Type, [.. U16(r.From), .. U16(r.To.Length), .. r.To.SelectMany(U16)]))]));
        parts.Add(Box("iprp", [.. Box("ipco", [.. _properties.SelectMany(p => p)]), .. Full("ipma", 0, [.. ipma])]));
        return Full("meta", 0, [.. parts.SelectMany(p => p)]);
    }

    // ───────── Boxes ─────────

    private static byte[] Box(string type, byte[] payload) => [.. U32(8 + payload.Length), .. Encoding.ASCII.GetBytes(type), .. payload];

    private static byte[] Full(string type, int version, byte[] payload, int flags = 0) =>
        Box(type, [(byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags, .. payload]);

    private static byte[] U16(int value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        return bytes;
    }

    private static byte[] U32(long value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)value));
        return bytes;
    }
}
