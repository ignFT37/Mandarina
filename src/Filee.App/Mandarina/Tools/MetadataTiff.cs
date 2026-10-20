// Mandarina: the METADATA tool's reader/editor of TIFF-structured blocks: the EXIF of a JPEG, PNG or WebP, and the IFDs of a
// TIFF file itself. It edits IN PLACE: nothing that already exists is moved (the maker note keeps the offsets it was written
// with, and so do the strips of a TIFF). An IFD that changed is written again where it was when it still fits, or at the end
// when it grew; the old one, with every value that is gone or replaced, is overwritten with zeros, so a removed tag does not
// survive anywhere in the bytes.

using System.Buffers.Binary;
using System.Text;

namespace Filee.App.Mandarina.Tools;

/// <summary>The IFDs that <see cref="MetadataTiff"/> edits.</summary>
internal enum MetadataTiffIfd
{
    Main,
    Exif,
    Gps,
    Interop,
}

/// <summary>One tag of an IFD: its type, count and the raw bytes of the value in the file's byte order.</summary>
internal sealed class MetadataTiffValue(ushort tag, ushort type, uint count, byte[] data, long offset = -1)
{
    public ushort Tag { get; } = tag;

    public ushort Type { get; } = type;

    public uint Count { get; } = count;

    public byte[] Data { get; } = data;

    /// <summary>Where the value lives out of line in the stream (-1 when it is inline or has not been written yet).</summary>
    public long Offset { get; } = offset;
}

internal sealed class MetadataTiff
{
    // The tags the tool knows by name (IFD0, the Exif IFD and the GPS IFD).
    public const ushort ImageDescription = 0x010E;
    public const ushort Make = 0x010F;
    public const ushort Model = 0x0110;
    public const ushort Orientation = 0x0112;
    public const ushort Software = 0x0131;
    public const ushort DateTime = 0x0132;
    public const ushort Artist = 0x013B;
    public const ushort HostComputer = 0x013C;
    public const ushort DocumentName = 0x010D;
    public const ushort PageName = 0x011D;
    public const ushort Copyright = 0x8298;
    public const ushort ExifPointer = 0x8769;
    public const ushort GpsPointer = 0x8825;
    public const ushort InteropPointer = 0xA005;
    public const ushort SubIfds = 0x014A;
    public const ushort DateTimeOriginal = 0x9003;
    public const ushort DateTimeDigitized = 0x9004;
    public const ushort OffsetTime = 0x9010;
    public const ushort OffsetTimeOriginal = 0x9011;
    public const ushort OffsetTimeDigitized = 0x9012;
    public const ushort SubSecTime = 0x9290;
    public const ushort SubSecTimeOriginal = 0x9291;
    public const ushort SubSecTimeDigitized = 0x9292;
    public const ushort MakerNote = 0x927C;
    public const ushort UserComment = 0x9286;
    public const ushort CameraOwnerName = 0xA430;
    public const ushort BodySerialNumber = 0xA431;
    public const ushort LensSpecification = 0xA432;
    public const ushort LensMake = 0xA433;
    public const ushort LensModel = 0xA434;
    public const ushort LensSerialNumber = 0xA435;
    public const ushort XpTitle = 0x9C9B;
    public const ushort XpComment = 0x9C9C;
    public const ushort XpAuthor = 0x9C9D;
    public const ushort XpKeywords = 0x9C9E;
    public const ushort XpSubject = 0x9C9F;
    public const ushort JpegInterchangeFormat = 0x0201;
    public const ushort JpegInterchangeFormatLength = 0x0202;
    public const ushort StripOffsets = 0x0111;
    public const ushort StripByteCounts = 0x0117;
    public const ushort XmpPacket = 0x02BC;
    public const ushort IptcNaa = 0x83BB;
    public const ushort PhotoshopResources = 0x8649;
    public const ushort GpsVersionId = 0x0000;
    public const ushort GpsLatitudeRef = 0x0001;
    public const ushort GpsLatitude = 0x0002;
    public const ushort GpsLongitudeRef = 0x0003;
    public const ushort GpsLongitude = 0x0004;
    public const ushort GpsTimeStamp = 0x0007;
    public const ushort GpsDateStamp = 0x001D;

    public const ushort TypeByte = 1;
    public const ushort TypeAscii = 2;
    public const ushort TypeShort = 3;
    public const ushort TypeLong = 4;
    public const ushort TypeRational = 5;
    public const ushort TypeUndefined = 7;
    private const ushort TypeIfd = 13;
    private const ushort TypeSignedRational = 10;

    /// <summary>The largest block a JPEG can carry as EXIF once "Exif\0\0" has gone in front of it (a segment holds 65533 bytes).</summary>
    public const int MaxJpegPayload = 65533 - 6;

    /// <summary>The tags of IFD0 of a TIFF file that describe the picture's history or its author (the rest says how to read the pixels).</summary>
    private static readonly HashSet<ushort> DescriptiveMainTags =
    [
        ImageDescription, Make, Model, Software, DateTime, Artist, HostComputer, DocumentName, PageName, Copyright, XmpPacket, IptcNaa,
        PhotoshopResources, XpTitle, XpComment, XpAuthor, XpKeywords, XpSubject,
    ];

    private const int MaxEntries = 4096;

    private static readonly MetadataTiffIfd[] ChildrenFirst = [MetadataTiffIfd.Interop, MetadataTiffIfd.Exif, MetadataTiffIfd.Gps, MetadataTiffIfd.Main];

    private readonly Stream _stream;
    private readonly bool _big;
    private readonly bool _isFile;
    private readonly bool _isPage;
    private readonly Dictionary<MetadataTiffIfd, IfdState> _ifds = [];
    private readonly List<(long Offset, long Length)> _wipe = [];
    private long _firstIfd;
    private long _nextIfd;
    private bool _headerChanged;

    private MetadataTiff(Stream stream, bool big, bool isFile, bool isPage)
    {
        _stream = stream;
        _big = big;
        _isFile = isFile;
        _isPage = isPage;
    }

    /// <summary>True when the byte order is big endian ("MM").</summary>
    public bool BigEndian => _big;

    private sealed class IfdState
    {
        public bool Exists = true;
        public bool Dirty;
        public long NextOffset;
        public List<MetadataTiffValue> Entries = [];

        /// <summary>The ranges of the stream this IFD occupies: its table first, then the values that live out of line.</summary>
        public List<(long Offset, long Length)> Owned = [];
    }

    // ───────── Opening ─────────

    /// <summary>
    /// Reads the TIFF structure in <paramref name="stream"/> (seekable; edits are written back to it). Null when it is not a
    /// classic TIFF (BigTIFF is not handled). <paramref name="isFile"/> says the stream is a TIFF file, not an EXIF block: its
    /// IFD0 also holds the tags that describe the picture, which are not counted as metadata. Throws
    /// <see cref="InvalidDataException"/> for a damaged IFD0.
    /// </summary>
    public static MetadataTiff? Open(Stream stream, bool isFile = false)
    {
        if (!ReadHeader(stream, out var big, out var first))
            return null;
        var tiff = new MetadataTiff(stream, big, isFile, isPage: false) { _firstIfd = first };
        tiff.Load();
        return tiff;
    }

    /// <summary>The offsets of the IFDs that follow IFD0 (the pages of a TIFF file), in order.</summary>
    public static List<long> PageOffsets(Stream stream)
    {
        var pages = new List<long>();
        if (!ReadHeader(stream, out var big, out var first))
            return pages;
        var seen = new HashSet<long>();
        for (var at = first; at != 0 && seen.Add(at) && at + 2 <= stream.Length;)
        {
            pages.Add(at);
            var header = new byte[2];
            stream.Position = at;
            stream.ReadExactly(header);
            var count = U16(header, 0, big);
            var nextAt = at + 2 + 12L * count;
            if (nextAt + 4 > stream.Length)
                break;
            var next = new byte[4];
            stream.Position = nextAt;
            stream.ReadExactly(next);
            at = U32(next, 0, big);
        }
        return pages;
    }

    /// <summary>One IFD of a TIFF file (a page after the first) to be edited in place: it can only shrink.</summary>
    public static MetadataTiff? OpenPage(Stream stream, long offset)
    {
        if (!ReadHeader(stream, out var big, out _))
            return null;
        var tiff = new MetadataTiff(stream, big, isFile: true, isPage: true) { _firstIfd = offset };
        tiff.Load();
        return tiff;
    }

    private static bool ReadHeader(Stream stream, out bool big, out long first)
    {
        big = false;
        first = 0;
        if (stream.Length < 8)
            return false;
        var header = new byte[8];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (header[0] == 'M' && header[1] == 'M')
            big = true;
        else if (!(header[0] == 'I' && header[1] == 'I'))
            return false;
        if (U16(header, 2, big) != 42)
            return false;
        first = U32(header, 4, big);
        return true;
    }

    /// <summary>A TIFF block with no tags yet (a fresh EXIF).</summary>
    public static MetadataTiff CreateEmpty(bool bigEndian = true)
    {
        var stream = new MemoryStream();
        stream.Write(bigEndian ? "MM\0*"u8 : "II*\0"u8);
        stream.Write([0, 0, 0, 0]);
        var tiff = new MetadataTiff(stream, bigEndian, isFile: false, isPage: false);
        tiff._ifds[MetadataTiffIfd.Main] = new IfdState { Exists = false, Dirty = true };
        tiff._headerChanged = true;
        return tiff;
    }

    private void Load()
    {
        if (_firstIfd == 0)
        {
            _ifds[MetadataTiffIfd.Main] = new IfdState { Exists = false };
            return;
        }
        var main = ReadIfd(_firstIfd);
        _ifds[MetadataTiffIfd.Main] = main;
        _nextIfd = main.NextOffset;
        if (PointerIn(main, ExifPointer) is { } exifAt && TryReadIfd(exifAt) is { } exif)
        {
            _ifds[MetadataTiffIfd.Exif] = exif;
            if (PointerIn(exif, InteropPointer) is { } interopAt && TryReadIfd(interopAt) is { } interop)
                _ifds[MetadataTiffIfd.Interop] = interop;
        }
        if (PointerIn(main, GpsPointer) is { } gpsAt && TryReadIfd(gpsAt) is { } gps)
            _ifds[MetadataTiffIfd.Gps] = gps;
    }

    private long? PointerIn(IfdState state, ushort tag)
    {
        var value = state.Entries.FirstOrDefault(v => v.Tag == tag);
        if (value is null || value.Data.Length < 4 || value.Type is not (TypeLong or TypeIfd))
            return null;
        return U32(value.Data, 0, _big);
    }

    /// <summary>A pointer that leads nowhere (a damaged file) counts as "no such IFD": the pointer entry stays as it is.</summary>
    private IfdState? TryReadIfd(long offset)
    {
        try
        {
            return ReadIfd(offset);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private IfdState ReadIfd(long offset)
    {
        if (offset < 8 || offset + 2 > _stream.Length)
            throw new InvalidDataException("An IFD lies outside the file.");
        var count = ReadU16(offset);
        // Some writers leave out the four bytes that say where the next IFD is when there is none.
        var available = (_stream.Length - offset - 2) / 12;
        if (count > MaxEntries || count > available)
            throw new InvalidDataException("An IFD does not fit in the file.");
        var hasNext = offset + 2 + 12L * count + 4 <= _stream.Length;
        var table = new byte[12 * count + 4];
        _stream.Position = offset + 2;
        _stream.ReadExactly(table.AsSpan(0, 12 * count + (hasNext ? 4 : 0)));
        var state = new IfdState { NextOffset = hasNext ? U32(table, 12 * count, _big) : 0 };
        state.Owned.Add((offset, 2 + 12L * count + (hasNext ? 4 : 0)));
        for (var i = 0; i < count; i++)
        {
            var at = i * 12;
            var tag = U16(table, at, _big);
            var type = U16(table, at + 2, _big);
            var n = U32(table, at + 4, _big);
            var size = UnitSize(type) * (long)n;
            // A tag whose value cannot be found is left out: the rest of the IFD is still good, and a bad entry is not worth losing it.
            if (size > _stream.Length)
                continue;
            if (size <= 4)
            {
                state.Entries.Add(new MetadataTiffValue(tag, type, n, table.AsSpan(at + 8, (int)size).ToArray()));
                continue;
            }
            var valueAt = U32(table, at + 8, _big);
            if (valueAt < 8 || valueAt + size > _stream.Length)
                continue;
            var data = new byte[size];
            _stream.Position = valueAt;
            _stream.ReadExactly(data);
            state.Owned.Add((valueAt, size));
            state.Entries.Add(new MetadataTiffValue(tag, type, n, data, valueAt));
        }
        return state;
    }

    private ushort ReadU16(long offset)
    {
        Span<byte> bytes = stackalloc byte[2];
        _stream.Position = offset;
        _stream.ReadExactly(bytes);
        return U16(bytes, 0, _big);
    }

    internal static int UnitSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or TypeIfd => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 1,
    };

    private static ushort U16(ReadOnlySpan<byte> bytes, int at, bool big) =>
        big ? BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);

    private static uint U32(ReadOnlySpan<byte> bytes, int at, bool big) =>
        big ? BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

    // ───────── Reading ─────────

    /// <summary>The tags of <paramref name="ifd"/> (none when it does not exist).</summary>
    public IReadOnlyList<MetadataTiffValue> Entries(MetadataTiffIfd ifd) => _ifds.TryGetValue(ifd, out var state) ? state.Entries : [];

    public MetadataTiffValue? Find(MetadataTiffIfd ifd, ushort tag) => Entries(ifd).FirstOrDefault(v => v.Tag == tag);

    public bool Has(MetadataTiffIfd ifd, ushort tag) => Find(ifd, tag) is not null;

    /// <summary>An ASCII tag as text (UTF-8, or Latin-1 when the bytes are not UTF-8); null when absent or blank.</summary>
    public string? GetString(MetadataTiffIfd ifd, ushort tag)
    {
        if (Find(ifd, tag) is not { Type: TypeAscii or TypeByte or TypeUndefined } value)
            return null;
        var text = DecodeText(value.Data);
        return text.Length == 0 ? null : text;
    }

    /// <summary>A Windows XP tag (UTF-16 little endian) as text; null when absent or blank.</summary>
    public string? GetXpString(MetadataTiffIfd ifd, ushort tag)
    {
        if (Find(ifd, tag) is not { } value)
            return null;
        var text = Encoding.Unicode.GetString(value.Data, 0, value.Data.Length & ~1).TrimEnd('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The user comment of the Exif IFD: eight bytes that name the character set ("ASCII", "UNICODE", "JIS" or none), then the text.
    /// Null when absent, blank or in a set that is not read.
    /// </summary>
    public string? GetUserComment()
    {
        if (Find(MetadataTiffIfd.Exif, UserComment) is not { } value || value.Data.Length <= 8)
            return null;
        var set = Encoding.ASCII.GetString(value.Data, 0, 8).TrimEnd('\0', ' ');
        var text = value.Data.AsSpan(8);
        string? result;
        switch (set)
        {
            case "UNICODE":
                result = (_big ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(text.ToArray(), 0, text.Length & ~1);
                break;
            case "JIS":
                return null;
            default:
                result = DecodeText(text.ToArray());
                break;
        }
        result = result.TrimEnd('\0', ' ');
        return result.Length == 0 ? null : result;
    }

    /// <summary>Writes the user comment: as ASCII when it is, else as UTF-16 in the block's byte order.</summary>
    public void SetUserComment(string text)
    {
        var ascii = text.All(c => c < 128);
        byte[] prefix = ascii ? "ASCII\0\0\0"u8.ToArray() : "UNICODE\0"u8.ToArray();
        var body = ascii ? Encoding.ASCII.GetBytes(text) : (_big ? Encoding.BigEndianUnicode : Encoding.Unicode).GetBytes(text);
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data, prefix.Length);
        PutRaw(MetadataTiffIfd.Exif, new MetadataTiffValue(UserComment, TypeUndefined, (uint)data.Length, data));
    }

    public int? GetInt(MetadataTiffIfd ifd, ushort tag)
    {
        if (Find(ifd, tag) is not { Count: >= 1 } value)
            return null;
        return value.Type switch
        {
            TypeShort => U16(value.Data, 0, _big),
            TypeLong => (int)U32(value.Data, 0, _big),
            TypeByte => value.Data[0],
            _ => null,
        };
    }

    /// <summary>The numbers of a RATIONAL tag as doubles (null when absent, of another type or with a zero denominator).</summary>
    public double[]? GetRationals(MetadataTiffIfd ifd, ushort tag)
    {
        if (Find(ifd, tag) is not { Type: TypeRational or TypeSignedRational, Count: >= 1 } value)
            return null;
        var result = new double[value.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var numerator = U32(value.Data, i * 8, _big);
            var denominator = U32(value.Data, i * 8 + 4, _big);
            if (denominator == 0)
                return null;
            result[i] = numerator / (double)denominator;
        }
        return result;
    }

    /// <summary>Decimal degrees from the GPS IFD; null when it does not hold a complete position.</summary>
    public (double Latitude, double Longitude)? GetGps()
    {
        if (GetString(MetadataTiffIfd.Gps, GpsLatitudeRef) is not { } latitudeRef || GetString(MetadataTiffIfd.Gps, GpsLongitudeRef) is not { } longitudeRef
            || GetRationals(MetadataTiffIfd.Gps, GpsLatitude) is not { Length: 3 } lat || GetRationals(MetadataTiffIfd.Gps, GpsLongitude) is not { Length: 3 } lon)
            return null;
        var latitude = lat[0] + lat[1] / 60 + lat[2] / 3600;
        var longitude = lon[0] + lon[1] / 60 + lon[2] / 3600;
        if (latitudeRef.StartsWith('S') || latitudeRef.StartsWith('s'))
            latitude = -latitude;
        if (longitudeRef.StartsWith('W') || longitudeRef.StartsWith('w'))
            longitude = -longitude;
        return (latitude, longitude);
    }

    /// <summary>True when a GPS IFD exists, whatever it holds (all its tags are a position, or the time or speed at it).</summary>
    public bool HasGpsIfd => _ifds.ContainsKey(MetadataTiffIfd.Gps);

    /// <summary>True when IFD0 leads on to another IFD: the thumbnail of a photo, or the next page of a TIFF file.</summary>
    public bool HasNextIfd => _nextIfd != 0;

    /// <summary>
    /// How many tags the block holds in its IFDs, not counting the pointers that only lead to another IFD (and, in a TIFF
    /// file, the tags of IFD0 that say how to read the pixels).
    /// </summary>
    public int TagCount => _ifds.Sum(pair => pair.Value.Entries.Count(v =>
        v.Tag is not (ExifPointer or GpsPointer or InteropPointer or SubIfds) && (!_isFile || pair.Key != MetadataTiffIfd.Main || DescriptiveMainTags.Contains(v.Tag))));

    /// <summary>Decodes an EXIF text: UTF-8 when it is valid, Latin-1 otherwise; the end mark and the blank padding dropped.</summary>
    internal static string DecodeText(byte[] data)
    {
        var span = data.AsSpan();
        var nul = span.IndexOf((byte)0);
        if (nul >= 0)
            span = span[..nul];
        try
        {
            return new UTF8Encoding(false, true).GetString(span).TrimEnd();
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(span).TrimEnd();
        }
    }

    // ───────── Editing ─────────

    private IfdState Ensure(MetadataTiffIfd ifd)
    {
        if (_ifds.TryGetValue(ifd, out var existing))
        {
            if (!existing.Exists)
            {
                existing.Exists = true;
                existing.Dirty = true;
            }
            return existing;
        }
        var state = new IfdState { Dirty = true };
        _ifds[ifd] = state;
        // The IFD hangs from a pointer tag of its parent; the pointer's value is filled in when the IFD is written.
        switch (ifd)
        {
            case MetadataTiffIfd.Exif:
                PutRaw(MetadataTiffIfd.Main, new MetadataTiffValue(ExifPointer, TypeLong, 1, new byte[4]));
                break;
            case MetadataTiffIfd.Gps:
                PutRaw(MetadataTiffIfd.Main, new MetadataTiffValue(GpsPointer, TypeLong, 1, new byte[4]));
                break;
            case MetadataTiffIfd.Interop:
                PutRaw(MetadataTiffIfd.Exif, new MetadataTiffValue(InteropPointer, TypeLong, 1, new byte[4]));
                break;
        }
        return state;
    }

    private void PutRaw(MetadataTiffIfd ifd, MetadataTiffValue value)
    {
        var state = Ensure(ifd);
        state.Entries.RemoveAll(v => v.Tag == value.Tag);
        state.Entries.Add(value);
        state.Dirty = true;
    }

    public void SetAscii(MetadataTiffIfd ifd, ushort tag, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var data = new byte[bytes.Length + 1];
        bytes.CopyTo(data, 0);
        PutRaw(ifd, new MetadataTiffValue(tag, TypeAscii, (uint)data.Length, data));
    }

    public void SetShort(MetadataTiffIfd ifd, ushort tag, ushort value)
    {
        var data = new byte[2];
        WriteU16(data, 0, value);
        PutRaw(ifd, new MetadataTiffValue(tag, TypeShort, 1, data));
    }

    /// <summary>A Windows XP tag: the text in UTF-16 little endian with its end mark, as BYTE values.</summary>
    public void SetXpString(MetadataTiffIfd ifd, ushort tag, string text)
    {
        var data = Encoding.Unicode.GetBytes(text + "\0");
        PutRaw(ifd, new MetadataTiffValue(tag, TypeByte, (uint)data.Length, data));
    }

    /// <summary>A tag of raw bytes (an XMP packet, IPTC datasets), as BYTE or UNDEFINED.</summary>
    public void SetBytes(MetadataTiffIfd ifd, ushort tag, ushort type, byte[] data) =>
        PutRaw(ifd, new MetadataTiffValue(tag, type, (uint)data.Length, data));

    /// <summary>Removes a tag; true when it was there.</summary>
    public bool Remove(MetadataTiffIfd ifd, ushort tag)
    {
        if (!_ifds.TryGetValue(ifd, out var state) || state.Entries.RemoveAll(v => v.Tag == tag) == 0)
            return false;
        state.Dirty = true;
        return true;
    }

    /// <summary>Removes the GPS IFD with everything in it: position, altitude, speed, direction and the date and time of the fix.</summary>
    public void RemoveGps()
    {
        // The pointer goes even when the IFD it led to could not be read: a position must not stay reachable.
        if (_ifds.Remove(MetadataTiffIfd.Gps, out var gps))
            _wipe.AddRange(gps.Owned);
        Remove(MetadataTiffIfd.Main, GpsPointer);
    }

    /// <summary>Removes the Exif IFD (and the interoperability IFD under it) with everything in it.</summary>
    public void RemoveExifIfd()
    {
        if (_ifds.Remove(MetadataTiffIfd.Interop, out var interop))
            _wipe.AddRange(interop.Owned);
        if (_ifds.Remove(MetadataTiffIfd.Exif, out var exif))
            _wipe.AddRange(exif.Owned);
        Remove(MetadataTiffIfd.Main, ExifPointer);
    }

    /// <summary>Replaces the GPS IFD by one that holds only this position (an altitude or a time of the old place must not stay).</summary>
    public void SetGps(double latitude, double longitude)
    {
        RemoveGps();
        PutRaw(MetadataTiffIfd.Gps, new MetadataTiffValue(GpsVersionId, TypeByte, 4, [2, 3, 0, 0]));
        SetAscii(MetadataTiffIfd.Gps, GpsLatitudeRef, latitude < 0 ? "S" : "N");
        PutRaw(MetadataTiffIfd.Gps, new MetadataTiffValue(GpsLatitude, TypeRational, 3, DegreesMinutesSeconds(latitude)));
        SetAscii(MetadataTiffIfd.Gps, GpsLongitudeRef, longitude < 0 ? "W" : "E");
        PutRaw(MetadataTiffIfd.Gps, new MetadataTiffValue(GpsLongitude, TypeRational, 3, DegreesMinutesSeconds(longitude)));
    }

    private byte[] DegreesMinutesSeconds(double degrees)
    {
        // Whole units of 1/10000 of a second, so the seconds can round up to 60 without carrying by hand.
        var units = (long)Math.Round(Math.Abs(degrees) * 3600 * 10000, MidpointRounding.AwayFromZero);
        var data = new byte[24];
        void Rational(int index, long numerator, uint denominator)
        {
            WriteU32(data, index * 8, (uint)numerator);
            WriteU32(data, index * 8 + 4, denominator);
        }
        Rational(0, units / (3600L * 10000), 1);
        Rational(1, units / (60L * 10000) % 60, 1);
        Rational(2, units % (60L * 10000), 10000);
        return data;
    }

    /// <summary>
    /// Cuts IFD0 off from the IFD that follows it and wipes that IFD, its values and the picture it points to: the thumbnail
    /// of a photo. (In a TIFF file the next IFD is the next page: do not call it there.)
    /// </summary>
    public void RemoveThumbnail()
    {
        if (_nextIfd == 0)
            return;
        var seen = new HashSet<long>();
        for (var next = _nextIfd; next != 0 && seen.Add(next);)
        {
            IfdState state;
            try
            {
                state = ReadIfd(next);
            }
            catch (InvalidDataException)
            {
                break;
            }
            _wipe.AddRange(state.Owned);
            var from = state.Entries.FirstOrDefault(v => v.Tag == JpegInterchangeFormat);
            var length = state.Entries.FirstOrDefault(v => v.Tag == JpegInterchangeFormatLength);
            if (from is not null && length is not null)
                AddWipe(Unsigned(from, 0), Unsigned(length, 0));
            var strips = state.Entries.FirstOrDefault(v => v.Tag == StripOffsets);
            var sizes = state.Entries.FirstOrDefault(v => v.Tag == StripByteCounts);
            if (strips is not null && sizes is not null && strips.Count == sizes.Count)
                for (var i = 0; i < strips.Count; i++)
                    AddWipe(Unsigned(strips, i), Unsigned(sizes, i));
            next = state.NextOffset;
        }
        _nextIfd = 0;
        if (_ifds.TryGetValue(MetadataTiffIfd.Main, out var main))
            main.Dirty = true;
    }

    private long Unsigned(MetadataTiffValue value, int index) => value.Type switch
    {
        TypeShort when value.Data.Length >= index * 2 + 2 => U16(value.Data, index * 2, _big),
        TypeLong when value.Data.Length >= index * 4 + 4 => U32(value.Data, index * 4, _big),
        _ => 0,
    };

    private void AddWipe(long offset, long length)
    {
        if (offset >= 8 && length > 0 && offset + length <= _stream.Length)
            _wipe.Add((offset, length));
    }

    // ───────── Saving ─────────

    /// <summary>
    /// Writes the changes into the stream: an IFD that changed is written again where it was when it still fits, or at the end;
    /// everything it left behind is zeroed. Returns the stream's length. The object still describes the stream afterwards.
    /// </summary>
    public long Commit()
    {
        var cursor = _stream.Length;
        foreach (var id in ChildrenFirst)
        {
            if (!_ifds.TryGetValue(id, out var state) || !state.Dirty)
                continue;
            var pointer = PointerTag(id);
            if (state.Entries.Count == 0 && id != MetadataTiffIfd.Main)
            {
                // An IFD with nothing left goes away with the pointer to it.
                _wipe.AddRange(state.Owned);
                _ifds.Remove(id);
                if (_ifds.TryGetValue(ParentOf(id), out var holder))
                {
                    holder.Entries.RemoveAll(v => v.Tag == pointer);
                    holder.Dirty = true;
                }
                continue;
            }

            var entries = state.Entries.OrderBy(v => v.Tag).ToList();
            var tableSize = 2 + 12L * entries.Count + 4;
            var oldTable = state.Owned.Count > 0 ? state.Owned[0] : (Offset: -1L, Length: 0L);
            var inPlace = oldTable.Offset >= 8 && tableSize <= oldTable.Length;
            if (_isPage && id == MetadataTiffIfd.Main && !inPlace)
                throw new InvalidOperationException("A page of a TIFF file cannot grow.");

            _wipe.AddRange(state.Owned);
            long tableAt;
            if (inPlace)
            {
                tableAt = oldTable.Offset;
            }
            else
            {
                cursor = Align(cursor);
                tableAt = cursor;
                cursor += tableSize;
            }
            var placed = new Dictionary<MetadataTiffValue, long>();
            foreach (var value in entries.Where(v => v.Data.Length > 4 && v.Offset < 0))
            {
                cursor = Align(cursor);
                placed[value] = cursor;
                cursor += value.Data.Length;
            }
            if (cursor > uint.MaxValue)
                throw new InvalidDataException("The TIFF block would pass 4 GB.");

            var table = new byte[tableSize];
            WriteU16(table, 0, (ushort)entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var value = entries[i];
                var at = 2 + i * 12;
                WriteU16(table, at, value.Tag);
                WriteU16(table, at + 2, value.Type);
                WriteU32(table, at + 4, value.Count);
                if (value.Data.Length <= 4)
                    value.Data.CopyTo(table, at + 8);
                else
                    WriteU32(table, at + 8, (uint)(placed.TryGetValue(value, out var where) ? where : value.Offset));
            }
            WriteU32(table, table.Length - 4, (uint)(id == MetadataTiffIfd.Main ? _nextIfd : state.NextOffset));
            _stream.Position = tableAt;
            _stream.Write(table);
            foreach (var (value, where) in placed)
            {
                _stream.Position = where;
                _stream.Write(value.Data);
            }

            // Describe the IFD as it is now, so the next commit knows what it owns.
            var written = new IfdState { NextOffset = id == MetadataTiffIfd.Main ? _nextIfd : state.NextOffset };
            written.Owned.Add((tableAt, tableSize));
            foreach (var value in entries)
            {
                if (value.Data.Length <= 4)
                {
                    written.Entries.Add(value);
                    continue;
                }
                var where = placed.TryGetValue(value, out var p) ? p : value.Offset;
                written.Owned.Add((where, value.Data.Length));
                written.Entries.Add(placed.ContainsKey(value) ? new MetadataTiffValue(value.Tag, value.Type, value.Count, value.Data, where) : value);
            }
            _ifds[id] = written;

            if (inPlace)
                continue;
            // It moved: the place that points to it has to say where it is now.
            if (id == MetadataTiffIfd.Main)
            {
                _firstIfd = tableAt;
                _headerChanged = true;
            }
            else if (_ifds.TryGetValue(ParentOf(id), out var holder))
            {
                var data = new byte[4];
                WriteU32(data, 0, (uint)tableAt);
                holder.Entries.RemoveAll(v => v.Tag == pointer);
                holder.Entries.Add(new MetadataTiffValue(pointer, TypeLong, 1, data));
                holder.Dirty = true;
            }
        }

        if (_headerChanged && !_isPage)
        {
            var data = new byte[4];
            WriteU32(data, 0, (uint)_firstIfd);
            _stream.Position = 4;
            _stream.Write(data);
        }
        _headerChanged = false;

        // Zero what the new layout no longer uses, except what some IFD still points to.
        var live = _ifds.Values.SelectMany(state => state.Owned).ToList();
        foreach (var range in _wipe)
        {
            foreach (var (offset, length) in Subtract(range, live))
            {
                if (offset < 8 || offset + length > _stream.Length)
                    continue;
                _stream.Position = offset;
                _stream.Write(new byte[length]);
            }
        }
        _wipe.Clear();
        foreach (var state in _ifds.Values)
            state.Dirty = false;
        _stream.Flush();
        return _stream.Length;
    }

    /// <summary>The bytes of the block, when it lives in memory (the EXIF of a photo).</summary>
    public byte[] ToArray() => _stream is MemoryStream memory ? memory.ToArray() : throw new InvalidOperationException("The block is not in memory.");

    /// <summary>
    /// A new block that holds the same tags written compactly, without the thumbnail and the maker note (whose private offsets
    /// cannot be moved). For an EXIF that no longer fits in a JPEG segment.
    /// </summary>
    public MetadataTiff Compacted()
    {
        var result = CreateEmpty(_big);
        foreach (var id in new[] { MetadataTiffIfd.Main, MetadataTiffIfd.Exif, MetadataTiffIfd.Gps, MetadataTiffIfd.Interop })
        {
            foreach (var value in Entries(id))
            {
                if (value.Tag is ExifPointer or GpsPointer or InteropPointer or MakerNote or SubIfds)
                    continue;
                result.PutRaw(id, new MetadataTiffValue(value.Tag, value.Type, value.Count, value.Data));
            }
        }
        result.Commit();
        return result;
    }

    private static MetadataTiffIfd ParentOf(MetadataTiffIfd ifd) => ifd == MetadataTiffIfd.Interop ? MetadataTiffIfd.Exif : MetadataTiffIfd.Main;

    private static ushort PointerTag(MetadataTiffIfd ifd) => ifd switch
    {
        MetadataTiffIfd.Exif => ExifPointer,
        MetadataTiffIfd.Gps => GpsPointer,
        MetadataTiffIfd.Interop => InteropPointer,
        _ => 0,
    };

    /// <summary>The parts of <paramref name="range"/> that none of the ranges in <paramref name="keep"/> covers.</summary>
    private static List<(long Offset, long Length)> Subtract((long Offset, long Length) range, List<(long Offset, long Length)> keep)
    {
        var pieces = new List<(long Offset, long Length)>();
        if (range.Length > 0)
            pieces.Add(range);
        foreach (var (keptAt, keptLength) in keep)
        {
            var next = new List<(long Offset, long Length)>();
            foreach (var (from, length) in pieces)
            {
                var end = from + length;
                var keptEnd = keptAt + keptLength;
                if (keptEnd <= from || keptAt >= end)
                {
                    next.Add((from, length));
                    continue;
                }
                if (keptAt > from)
                    next.Add((from, keptAt - from));
                if (keptEnd < end)
                    next.Add((keptEnd, end - keptEnd));
            }
            pieces = next;
        }
        return pieces;
    }

    private static long Align(long position) => (position + 1) & ~1L;

    private void WriteU16(byte[] buffer, int at, ushort value)
    {
        if (_big)
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at), value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at), value);
    }

    private void WriteU32(byte[] buffer, int at, uint value)
    {
        if (_big)
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(at), value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at), value);
    }
}
