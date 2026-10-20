// Mandarina: the METADATA tool's reader/editor of IPTC-IIM, the news-agency captions (by-line, caption, copyright, city…) that
// Photoshop and Lightroom keep in a JPEG's APP13 segment, inside "Photoshop image resources" (resource 0x0404), and that a
// TIFF keeps raw in its tag 33723. Other resources of the Photoshop block (thumbnails, resolution…) are kept as they are.

using System.Text;

namespace Filee.App.Mandarina.Tools;

internal sealed class MetadataIptc
{
    /// <summary>The identifier that starts the APP13 payload of a JPEG.</summary>
    public static readonly byte[] PhotoshopHeader = "Photoshop 3.0\0"u8.ToArray();

    public const int IptcResource = 0x0404;

    // Record 2 (application) datasets.
    public const int ObjectName = 5;
    public const int DateCreated = 55;
    public const int TimeCreated = 60;
    public const int DigitalCreationDate = 62;
    public const int DigitalCreationTime = 63;
    public const int OriginatingProgram = 65;
    public const int ByLine = 80;
    public const int ByLineTitle = 85;
    public const int City = 90;
    public const int SubLocation = 92;
    public const int ProvinceState = 95;
    public const int CountryCode = 100;
    public const int CountryName = 101;
    public const int Credit = 110;
    public const int Source = 115;
    public const int CopyrightNotice = 116;
    public const int CaptionAbstract = 120;

    private const int CodedCharacterSet = 90; // record 1

    private sealed record Resource(ushort Id, byte[] Name, byte[] Data);

    private sealed record Dataset(int Record, int Number, byte[] Value);

    private readonly List<Resource> _resources = [];
    private readonly List<Dataset> _datasets = [];
    private bool _utf8;

    private MetadataIptc()
    {
    }

    /// <summary>The IPTC in a Photoshop block (the APP13 payload after "Photoshop 3.0\0", or a TIFF's tag 34377). Null when it is damaged.</summary>
    public static MetadataIptc? FromPhotoshop(ReadOnlySpan<byte> data)
    {
        var result = new MetadataIptc();
        var pos = 0;
        while (pos + 12 <= data.Length)
        {
            if (!data.Slice(pos, 4).SequenceEqual("8BIM"u8) && !data.Slice(pos, 4).SequenceEqual("MeSa"u8))
                break;
            var id = (ushort)((data[pos + 4] << 8) | data[pos + 5]);
            var nameLength = data[pos + 6];
            var nameBytes = 1 + nameLength;
            if ((nameBytes & 1) == 1)
                nameBytes++;
            var sizeAt = pos + 6 + nameBytes;
            if (sizeAt + 4 > data.Length)
                return null;
            var size = (data[sizeAt] << 24) | (data[sizeAt + 1] << 16) | (data[sizeAt + 2] << 8) | data[sizeAt + 3];
            var dataAt = sizeAt + 4;
            if (size < 0 || dataAt + (long)size > data.Length)
                return null;
            result._resources.Add(new Resource(id, data.Slice(pos + 7, nameLength).ToArray(), data.Slice(dataAt, size).ToArray()));
            pos = dataAt + size + (size & 1);
        }
        if (result._resources.FirstOrDefault(r => r.Id == IptcResource) is { } iptc && !result.ParseDatasets(iptc.Data))
            return null;
        return result;
    }

    /// <summary>The IPTC stored raw (a TIFF's tag 33723). Null when it is damaged.</summary>
    public static MetadataIptc? FromRaw(ReadOnlySpan<byte> data)
    {
        var result = new MetadataIptc();
        return result.ParseDatasets(data) ? result : null;
    }

    private bool ParseDatasets(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        while (pos + 5 <= data.Length)
        {
            if (data[pos] != 0x1C)
                return false;
            var record = data[pos + 1];
            var number = data[pos + 2];
            long length = (data[pos + 3] << 8) | data[pos + 4];
            var header = 5;
            if ((length & 0x8000) != 0)
            {
                // Extended dataset: the low 15 bits say how many bytes hold the real length.
                var lengthBytes = (int)(length & 0x7FFF);
                if (lengthBytes is < 1 or > 4 || pos + 5 + lengthBytes > data.Length)
                    return false;
                length = 0;
                for (var i = 0; i < lengthBytes; i++)
                    length = (length << 8) | data[pos + 5 + i];
                header += lengthBytes;
            }
            if (pos + header + length > data.Length)
                return false;
            var value = data.Slice(pos + header, (int)length).ToArray();
            if (record == 1 && number == CodedCharacterSet && value.AsSpan().SequenceEqual((byte[])[0x1B, 0x25, 0x47]))
                _utf8 = true;
            _datasets.Add(new Dataset(record, number, value));
            pos += header + (int)length;
        }
        return true;
    }

    // ───────── Reading ─────────

    /// <summary>How many datasets of the application record (2) the block holds.</summary>
    public int Count => _datasets.Count(d => d.Record == 2);

    public bool Has(int number) => _datasets.Any(d => d.Record == 2 && d.Number == number);

    public string? GetText(int number)
    {
        var dataset = _datasets.FirstOrDefault(d => d.Record == 2 && d.Number == number);
        if (dataset is null)
            return null;
        var text = Decode(dataset.Value).Trim('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    public IEnumerable<int> Numbers() => _datasets.Where(d => d.Record == 2).Select(d => d.Number);

    private string Decode(byte[] value)
    {
        if (_utf8)
            return Encoding.UTF8.GetString(value);
        // Without a declared character set the text is the old ISO 8859-1 / Windows-1252 kind, but many writers put UTF-8 in it.
        try
        {
            return new UTF8Encoding(false, true).GetString(value);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(value);
        }
    }

    // ───────── Editing ─────────

    /// <summary>Gives a dataset a new text (replacing the existing ones). Returns false when the dataset was not there.</summary>
    public bool SetText(int number, string text)
    {
        var existing = _datasets.FindIndex(d => d.Record == 2 && d.Number == number);
        if (existing < 0)
            return false;
        var value = Encode(text);
        _datasets.RemoveAll(d => d.Record == 2 && d.Number == number);
        _datasets.Insert(Math.Min(existing, _datasets.Count), new Dataset(2, number, value));
        return true;
    }

    /// <summary>Removes a dataset. Returns false when it was not there.</summary>
    public bool Remove(int number) => _datasets.RemoveAll(d => d.Record == 2 && d.Number == number) > 0;

    private byte[] Encode(string text)
    {
        if (_utf8)
            return Encoding.UTF8.GetBytes(text);
        // Declared as Latin-1 (or not declared): keep it that way when the text fits, or switch the whole block to UTF-8.
        var latin1 = Encoding.Latin1.GetBytes(text);
        if (Encoding.Latin1.GetString(latin1) == text)
            return latin1;
        for (var i = 0; i < _datasets.Count; i++)
        {
            var dataset = _datasets[i];
            if (dataset.Record == 2)
                _datasets[i] = dataset with { Value = Encoding.UTF8.GetBytes(Decode(dataset.Value)) };
        }
        _datasets.RemoveAll(d => d.Record == 1 && d.Number == CodedCharacterSet);
        _datasets.Insert(0, new Dataset(1, CodedCharacterSet, [0x1B, 0x25, 0x47]));
        _utf8 = true;
        return Encoding.UTF8.GetBytes(text);
    }

    // ───────── Writing ─────────

    private byte[] SerializeDatasets()
    {
        using var stream = new MemoryStream();
        foreach (var dataset in _datasets.OrderBy(d => d.Record))
        {
            stream.WriteByte(0x1C);
            stream.WriteByte((byte)dataset.Record);
            stream.WriteByte((byte)dataset.Number);
            if (dataset.Value.Length < 0x8000)
            {
                stream.WriteByte((byte)(dataset.Value.Length >> 8));
                stream.WriteByte((byte)dataset.Value.Length);
            }
            else
            {
                stream.Write([0x80, 0x04, (byte)(dataset.Value.Length >> 24), (byte)(dataset.Value.Length >> 16), (byte)(dataset.Value.Length >> 8), (byte)dataset.Value.Length]);
            }
            stream.Write(dataset.Value);
        }
        return stream.ToArray();
    }

    /// <summary>The raw datasets (for a TIFF's tag 33723).</summary>
    public byte[] ToRaw() => SerializeDatasets();

    /// <summary>The Photoshop resources, with the IPTC one rebuilt (without "Photoshop 3.0\0" in front).</summary>
    public byte[] ToPhotoshop()
    {
        using var stream = new MemoryStream();
        var wroteIptc = false;
        void Write(ushort id, byte[] name, byte[] data)
        {
            stream.Write("8BIM"u8);
            stream.WriteByte((byte)(id >> 8));
            stream.WriteByte((byte)id);
            stream.WriteByte((byte)name.Length);
            stream.Write(name);
            if (((1 + name.Length) & 1) == 1)
                stream.WriteByte(0);
            stream.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            stream.Write(data);
            if ((data.Length & 1) == 1)
                stream.WriteByte(0);
        }
        foreach (var resource in _resources)
        {
            if (resource.Id != IptcResource)
            {
                Write(resource.Id, resource.Name, resource.Data);
                continue;
            }
            wroteIptc = true;
            if (_datasets.Count > 0)
                Write(resource.Id, resource.Name, SerializeDatasets());
        }
        if (!wroteIptc && _datasets.Count > 0)
            Write(IptcResource, [], SerializeDatasets());
        return stream.ToArray();
    }

    /// <summary>True when the block would hold nothing at all once written.</summary>
    public bool IsEmpty => _datasets.Count == 0 && _resources.All(r => r.Id == IptcResource);
}
