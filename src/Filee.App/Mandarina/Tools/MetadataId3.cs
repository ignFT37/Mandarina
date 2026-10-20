// Mandarina: the MP3 side of the METADATA tool. An MP3 is a stream of audio frames with tags around it: an ID3v2 tag in front
// (maybe more than one), and at the end an APEv2 tag and/or the 128 bytes of ID3v1. The tool changes the tags and copies the audio
// frames byte for byte, so nothing is decoded or re-encoded (and the Xing/LAME header that gives the exact length and the gapless
// delay stays as the encoder wrote it). The frames it does not edit are kept as they are: ratings, lyrics, cover art, chapters.

using System.Buffers.Binary;
using System.Text;

namespace Filee.App.Mandarina.Tools;

/// <summary>One ID3v2 frame: its four-letter id (v2.3/v2.4 names), its flags and its body as stored.</summary>
internal sealed class MetadataId3Frame(string id, ushort flags, byte[] body, bool plain = true)
{
    public string Id { get; } = id;

    public ushort Flags { get; } = flags;

    public byte[] Body { get; } = body;

    /// <summary>True for a frame whose body can be read as it is (not compressed, encrypted or unsynchronised).</summary>
    public bool IsPlain { get; } = plain;
}

internal sealed class MetadataId3
{
    private static readonly string[] Genres =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop", "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B", "Rap",
        "Reggae", "Rock", "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal",
        "Jazz+Funk", "Fusion", "Trance", "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise", "Alternative Rock", "Bass",
        "Soul", "Punk", "Space", "Meditative", "Instrumental Pop", "Instrumental Rock", "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic",
        "Pop-Folk", "Eurodance", "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta", "Top 40", "Christian Rap", "Pop/Funk", "Jungle", "Native American",
        "Cabaret", "New Wave", "Psychedelic", "Rave", "Showtunes", "Trailer", "Lo-Fi", "Tribal", "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical",
        "Rock & Roll", "Hard Rock",
    ];

    private MetadataId3()
    {
    }

    /// <summary>2, 3 or 4; 0 when the file has no ID3v2 tag.</summary>
    public int Version { get; private set; }

    public List<MetadataId3Frame> Frames { get; } = [];

    /// <summary>Where the audio starts (after the ID3v2 tags and their padding) and ends (before the APE and ID3v1 tags).</summary>
    public long AudioStart { get; private set; }

    public long AudioEnd { get; private set; }

    public long FileLength { get; private set; }

    /// <summary>The ID3v2 tag's total size in the file (header, frames and padding), 0 when there is none.</summary>
    public long TagSize { get; private set; }

    public MetadataId3V1? V1 { get; private set; }

    public MetadataApeTag? Ape { get; private set; }

    /// <summary>True for a v2.2 frame set the tool had to leave out when converting (unknown frames).</summary>
    public bool Converted { get; private set; }

    // ───────── Reading ─────────

    public static MetadataId3 Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = new MetadataId3 { FileLength = stream.Length };
        long position = 0;
        var header = new byte[10];
        while (position + 10 <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(header);
            if (!(header[0] == 'I' && header[1] == 'D' && header[2] == '3' && header[3] is 2 or 3 or 4 && header[4] != 0xFF && SyncSafe(header, 6) is var size and >= 0))
                break;
            var footer = header[3] == 4 && (header[5] & 0x10) != 0 ? 10 : 0;
            if (position + 10 + size + footer > stream.Length)
                break;
            var body = new byte[size];
            stream.ReadExactly(body);
            if (result.Version == 0)
            {
                result.Version = header[3];
                result.ParseFrames(header[3], header[5], body);
            }
            position += 10 + size + footer;
            result.TagSize = position;
        }
        result.AudioStart = position;
        result.AudioEnd = stream.Length;
        if (stream.Length - position >= 128)
        {
            var tag = new byte[128];
            stream.Position = stream.Length - 128;
            stream.ReadExactly(tag);
            if (tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G')
            {
                result.V1 = MetadataId3V1.Parse(tag);
                result.AudioEnd -= 128;
            }
        }
        if (result.AudioEnd - position >= 32)
        {
            var footer = new byte[32];
            stream.Position = result.AudioEnd - 32;
            stream.ReadExactly(footer);
            if (Encoding.ASCII.GetString(footer, 0, 8) == "APETAGEX")
            {
                var ape = MetadataApeTag.Read(stream, result.AudioEnd, footer);
                if (ape is not null)
                {
                    result.Ape = ape;
                    result.AudioEnd = ape.Start;
                }
            }
        }
        return result;
    }

    private static int SyncSafe(byte[] bytes, int at) =>
        (bytes[at] | bytes[at + 1] | bytes[at + 2] | bytes[at + 3]) >= 0x80 ? -1 : (bytes[at] << 21) | (bytes[at + 1] << 14) | (bytes[at + 2] << 7) | bytes[at + 3];

    private void ParseFrames(int version, byte flags, byte[] body)
    {
        // Version 2.3 unsynchronises the whole tag when the header says so; 2.4 does it frame by frame (flag on the frame).
        if (version == 3 && (flags & 0x80) != 0)
            body = MetadataId3Unsynchronise.Decode(body);
        var pos = 0;
        if ((flags & 0x40) != 0 && version >= 3 && body.Length >= 6)
        {
            // The extended header: skip it (it is not written back, its CRC would be wrong).
            var extended = version == 4 ? SyncSafe(body, 0) : BinaryPrimitives.ReadInt32BigEndian(body) + 4;
            if (extended < 0 || extended > body.Length)
                return;
            pos = extended;
        }
        var idLength = version == 2 ? 3 : 4;
        var headerLength = version == 2 ? 6 : 10;
        while (pos + headerLength <= body.Length && body[pos] != 0)
        {
            var id = Encoding.Latin1.GetString(body, pos, idLength);
            int size;
            ushort frameFlags = 0;
            if (version == 2)
            {
                size = (body[pos + 3] << 16) | (body[pos + 4] << 8) | body[pos + 5];
            }
            else
            {
                size = version == 4 ? SyncSafe(body, pos + 4) : BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(pos + 4));
                // Some v2.4 writers (old iTunes) store plain sizes: a size that is not synchsafe, or leads out of the tag, is read that way.
                if (version == 4 && (size < 0 || pos + headerLength + size > body.Length) && BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(pos + 4)) is var plain and >= 0
                    && pos + headerLength + plain <= body.Length)
                    size = plain;
                frameFlags = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(pos + 8));
            }
            if (size < 0 || pos + headerLength + size > body.Length)
                break;
            var frameBody = body.AsSpan(pos + headerLength, size).ToArray();
            if (version == 2)
            {
                if (MetadataId3V22.Upgrade(id, frameBody) is { } upgraded)
                    Frames.Add(upgraded);
                else
                    Converted = true;
            }
            else
            {
                // v2.3: compression, encryption and grouping change the body; v2.4: also unsynchronisation and a data-length prefix.
                var readable = version == 3 ? (frameFlags & 0x00E0) == 0 : (frameFlags & 0x004F) == 0;
                Frames.Add(new MetadataId3Frame(id, frameFlags, frameBody, readable));
            }
            pos += headerLength + size;
        }
    }

    // ───────── Fields ─────────

    public MetadataId3Frame? Find(string id) => Frames.FirstOrDefault(f => f.Id == id && f.IsPlain);

    /// <summary>The text of a text frame (several values joined with " / "); null when absent or blank.</summary>
    public string? Text(string id)
    {
        var frame = Find(id);
        if (frame is null || frame.Body.Length < 2)
            return null;
        var values = DecodeStrings(frame.Body[0], frame.Body.AsSpan(1));
        var text = string.Join(" / ", values.Where(v => v.Length > 0));
        return text.Length == 0 ? null : text;
    }

    /// <summary>The genre as a name ("(17)" and "17" are Rock).</summary>
    public string? Genre()
    {
        var text = Text("TCON");
        if (text is null)
            return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith('('))
        {
            var close = trimmed.IndexOf(')');
            if (close > 1 && int.TryParse(trimmed.AsSpan(1, close - 1), out var code))
            {
                var rest = trimmed[(close + 1)..].Trim();
                return rest.Length > 0 ? rest : NameOf(code) ?? trimmed;
            }
        }
        return int.TryParse(trimmed, out var number) ? NameOf(number) ?? trimmed : trimmed;
    }

    public static string? NameOf(int code) => code >= 0 && code < Genres.Length ? Genres[code] : null;

    public static int CodeOf(string name)
    {
        var index = Array.FindIndex(Genres, g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 255 : index;
    }

    /// <summary>The recording date as "yyyy", "yyyy-MM-dd" or "yyyy-MM-ddTHH:mm" from TDRC (v2.4) or TYER, TDAT and TIME (v2.3).</summary>
    public string? Date()
    {
        if (Text("TDRC") is { } recording)
            return recording;
        var year = Text("TYER");
        if (year is null)
            return null;
        var date = Text("TDAT");
        if (date is { Length: 4 } && date.All(char.IsAsciiDigit))
        {
            var result = $"{year}-{date[2..4]}-{date[..2]}";
            if (Text("TIME") is { Length: 4 } time && time.All(char.IsAsciiDigit))
                result += $"T{time[..2]}:{time[2..]}";
            return result;
        }
        return year;
    }

    /// <summary>
    /// The user's comment: the COMM frame with no description, or else the first that is not iTunes' technical one; failing that
    /// a "comment" in a TXXX frame, which is where FFmpeg writes it.
    /// </summary>
    public string? Comment()
    {
        foreach (var frame in Frames.Where(f => f.Id == "COMM" && f.IsPlain))
        {
            if (ReadComment(frame) is { } comment && (comment.Description.Length == 0 || !IsTechnical(comment.Description)) && comment.Text.Length > 0)
                return comment.Text;
        }
        foreach (var frame in Frames.Where(f => f.Id == "TXXX" && f.IsPlain))
        {
            if (ReadUserText(frame) is { } user && user.Description.Equals("comment", StringComparison.OrdinalIgnoreCase) && user.Value.Length > 0)
                return user.Value;
        }
        return null;
    }

    /// <summary>The description and value of a TXXX frame (user-defined text).</summary>
    internal static (string Description, string Value)? ReadUserText(MetadataId3Frame frame)
    {
        if (frame.Body.Length < 2)
            return null;
        var strings = DecodeStrings(frame.Body[0], frame.Body.AsSpan(1), maximum: 2);
        return strings.Count switch { 0 => null, 1 => (strings[0], ""), _ => (strings[0], strings[1]) };
    }

    private static bool IsTechnical(string description) => description.StartsWith("iTun", StringComparison.OrdinalIgnoreCase);

    internal static (string Language, string Description, string Text)? ReadComment(MetadataId3Frame frame)
    {
        if (frame.Body.Length < 5)
            return null;
        var encoding = frame.Body[0];
        var language = Encoding.Latin1.GetString(frame.Body, 1, 3);
        var strings = DecodeStrings(encoding, frame.Body.AsSpan(4), maximum: 2);
        return strings.Count switch
        {
            0 => null,
            1 => (language, "", strings[0]),
            _ => (language, strings[0], strings[1]),
        };
    }

    public bool HasPicture => Frames.Any(f => f.Id == "APIC");

    // ───────── Text encodings ─────────

    internal static List<string> DecodeStrings(byte encoding, ReadOnlySpan<byte> data, int maximum = int.MaxValue)
    {
        var result = new List<string>();
        var pos = 0;
        var wide = encoding is 1 or 2;
        while (pos <= data.Length && result.Count < maximum)
        {
            int end;
            var step = wide ? 2 : 1;
            end = pos;
            while (end + step <= data.Length && !(wide ? data[end] == 0 && data[end + 1] == 0 : data[end] == 0))
                end += step;
            result.Add(DecodeText(encoding, data[pos..Math.Min(end, data.Length)]));
            pos = end + step;
            if (end >= data.Length)
                break;
        }
        return result;
    }

    internal static string DecodeText(byte encoding, ReadOnlySpan<byte> data)
    {
        switch (encoding)
        {
            case 1:
                if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                    return Encoding.Unicode.GetString(data[2..]);
                if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                    return Encoding.BigEndianUnicode.GetString(data[2..]);
                return Encoding.Unicode.GetString(data);
            case 2:
                return Encoding.BigEndianUnicode.GetString(data);
            case 3:
                return Encoding.UTF8.GetString(data);
            default:
                // "Latin-1", but a lot of software writes UTF-8 into it: what is valid UTF-8 is taken as such.
                try
                {
                    return new UTF8Encoding(false, true).GetString(data);
                }
                catch (ArgumentException)
                {
                    return Encoding.Latin1.GetString(data);
                }
        }
    }

    // ───────── Building frames ─────────

    /// <summary>A text frame: Latin-1 when the text fits, UTF-16 with a byte-order mark (v2.3) or UTF-8 (v2.4) when it does not.</summary>
    public static MetadataId3Frame TextFrame(string id, string text, int version)
    {
        var latin1 = Encoding.Latin1.GetBytes(text);
        if (Encoding.Latin1.GetString(latin1) == text)
            return new MetadataId3Frame(id, 0, [0, .. latin1]);
        return version == 4
            ? new MetadataId3Frame(id, 0, [3, .. Encoding.UTF8.GetBytes(text)])
            : new MetadataId3Frame(id, 0, [1, 0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]);
    }

    public static MetadataId3Frame CommentFrame(string language, string text, int version)
    {
        var encoded = TextFrame("COMM", text, version).Body;
        // encoding byte, language, empty short description (terminated in the frame's encoding), the text.
        var wide = encoded[0] == 1;
        return new MetadataId3Frame("COMM", 0, [encoded[0], .. Encoding.ASCII.GetBytes(language), .. (wide ? new byte[] { 0xFF, 0xFE, 0, 0 } : [0]), .. encoded.AsSpan(1)]);
    }

    /// <summary>The picture of an APIC frame, with the rest of the frame (kind, description) as a tuple; null when it cannot be read.</summary>
    internal static (string Mime, byte Type, byte[] Data)? ReadPicture(MetadataId3Frame frame)
    {
        var body = frame.Body;
        if (body.Length < 4 || !frame.IsPlain)
            return null;
        var encoding = body[0];
        var mimeEnd = Array.IndexOf(body, (byte)0, 1);
        if (mimeEnd < 0 || mimeEnd + 2 > body.Length)
            return null;
        var mime = Encoding.Latin1.GetString(body, 1, mimeEnd - 1);
        var type = body[mimeEnd + 1];
        var pos = mimeEnd + 2;
        var wide = encoding is 1 or 2;
        while (pos + (wide ? 2 : 1) <= body.Length && !(wide ? body[pos] == 0 && body[pos + 1] == 0 : body[pos] == 0))
            pos += wide ? 2 : 1;
        pos += wide ? 2 : 1;
        return pos > body.Length ? null : (mime, type, body[pos..]);
    }

    public static MetadataId3Frame PictureFrame(string mime, byte type, byte[] data) =>
        new("APIC", 0, [0, .. Encoding.Latin1.GetBytes(mime), 0, type, 0, .. data]);

    // ───────── Writing ─────────

    /// <summary>
    /// An ID3v2 tag of <paramref name="version"/> (3 or 4) holding <paramref name="frames"/>, padded with zeros up to
    /// <paramref name="minimumSize"/> bytes when that is bigger. Returns an empty array without frames.
    /// </summary>
    public static byte[] Build(int version, IReadOnlyList<MetadataId3Frame> frames, long minimumSize = 0)
    {
        if (frames.Count == 0)
            return [];
        using var body = new MemoryStream();
        foreach (var frame in frames)
        {
            body.Write(Encoding.Latin1.GetBytes(frame.Id));
            var size = new byte[4];
            if (version == 4)
            {
                size[0] = (byte)((frame.Body.Length >> 21) & 0x7F);
                size[1] = (byte)((frame.Body.Length >> 14) & 0x7F);
                size[2] = (byte)((frame.Body.Length >> 7) & 0x7F);
                size[3] = (byte)(frame.Body.Length & 0x7F);
            }
            else
            {
                BinaryPrimitives.WriteInt32BigEndian(size, frame.Body.Length);
            }
            body.Write(size);
            var flags = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(flags, frame.Flags);
            body.Write(flags);
            body.Write(frame.Body);
        }
        var padding = Math.Max(0, minimumSize - 10 - body.Length);
        var total = body.Length + padding;
        var tag = new byte[10 + total];
        "ID3"u8.CopyTo(tag);
        tag[3] = (byte)version;
        tag[6] = (byte)((total >> 21) & 0x7F);
        tag[7] = (byte)((total >> 14) & 0x7F);
        tag[8] = (byte)((total >> 7) & 0x7F);
        tag[9] = (byte)(total & 0x7F);
        body.ToArray().CopyTo(tag, 10);
        return tag;
    }
}

/// <summary>Reverses ID3's unsynchronisation (every 0xFF 0x00 is 0xFF).</summary>
internal static class MetadataId3Unsynchronise
{
    public static byte[] Decode(byte[] data)
    {
        var result = new List<byte>(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0)
                i++;
        }
        return [.. result];
    }
}

/// <summary>The ID3v1 tag at the end of a file: fixed-width Latin-1 fields.</summary>
internal sealed class MetadataId3V1
{
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string Year = "";
    public string Comment = "";
    public int Track;
    public int Genre = 255;

    public static MetadataId3V1 Parse(byte[] tag)
    {
        string Field(int at, int length) => Encoding.Latin1.GetString(tag, at, length).TrimEnd('\0', ' ');
        var result = new MetadataId3V1 { Title = Field(3, 30), Artist = Field(33, 30), Album = Field(63, 30), Year = Field(93, 4), Genre = tag[127] };
        if (tag[125] == 0 && tag[126] != 0)
        {
            result.Comment = Field(97, 28);
            result.Track = tag[126];
        }
        else
        {
            result.Comment = Field(97, 30);
        }
        return result;
    }

    public byte[] ToBytes()
    {
        var tag = new byte[128];
        "TAG"u8.CopyTo(tag);
        void Put(string value, int at, int length) => Encoding.Latin1.GetBytes(value.Length > length ? value[..length] : value).CopyTo(tag, at);
        Put(Title, 3, 30);
        Put(Artist, 33, 30);
        Put(Album, 63, 30);
        Put(Year, 93, 4);
        Put(Comment, 97, Track > 0 ? 28 : 30);
        if (Track > 0)
            tag[126] = (byte)Track;
        tag[127] = (byte)Genre;
        return tag;
    }

    public bool IsEmpty => Title.Length + Artist.Length + Album.Length + Year.Length + Comment.Length == 0 && Track == 0 && Genre == 255;
}

/// <summary>An APEv2 tag at the end of a file (foobar2000, MP3Gain, Musepack): key/value items.</summary>
internal sealed class MetadataApeTag
{
    public sealed record Item(string Key, uint Flags, byte[] Value);

    public List<Item> Items { get; } = [];

    /// <summary>Where the tag starts in the file (its header when it has one).</summary>
    public long Start { get; private set; }

    public bool HasHeader { get; private set; }

    public uint Version { get; private set; }

    public static MetadataApeTag? Read(Stream stream, long end, byte[] footer)
    {
        var version = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(8));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(20));
        if (size < 32 || size > end || count > 100000)
            return null;
        var hasHeader = (flags & 0x80000000) != 0;
        var itemsStart = end - size;
        var tag = new MetadataApeTag { Version = version, HasHeader = hasHeader, Start = hasHeader ? itemsStart - 32 : itemsStart };
        if (tag.Start < 0)
            return null;
        var data = new byte[size - 32];
        stream.Position = itemsStart;
        stream.ReadExactly(data);
        var pos = 0;
        for (var i = 0; i < count; i++)
        {
            if (pos + 8 > data.Length)
                return null;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            var itemFlags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            var keyEnd = Array.IndexOf(data, (byte)0, pos + 8);
            if (keyEnd < 0 || keyEnd + 1 + length > data.Length)
                return null;
            tag.Items.Add(new Item(Encoding.UTF8.GetString(data, pos + 8, keyEnd - pos - 8), itemFlags, data.AsSpan(keyEnd + 1, (int)length).ToArray()));
            pos = keyEnd + 1 + (int)length;
        }
        return tag;
    }

    public string? Text(string key)
    {
        var item = Items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && (i.Flags & 6) == 0);
        if (item is null)
            return null;
        var text = Encoding.UTF8.GetString(item.Value).Replace('\0', '/').Trim();
        return text.Length == 0 ? null : text;
    }

    public void Set(string key, string value)
    {
        var index = Items.FindIndex(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var item = new Item(index >= 0 ? Items[index].Key : key, 0, Encoding.UTF8.GetBytes(value));
        if (index >= 0)
            Items[index] = item;
        else
            Items.Add(item);
    }

    public void Remove(string key) => Items.RemoveAll(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public byte[] ToBytes()
    {
        using var items = new MemoryStream();
        foreach (var item in Items)
        {
            var head = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)item.Value.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), item.Flags);
            items.Write(head);
            items.Write(Encoding.UTF8.GetBytes(item.Key));
            items.WriteByte(0);
            items.Write(item.Value);
        }
        byte[] Block(uint flags)
        {
            var block = new byte[32];
            "APETAGEX"u8.CopyTo(block);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), Version == 0 ? 2000 : Version);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), (uint)(items.Length + 32));
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)Items.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), flags);
            return block;
        }
        var header = HasHeader ? Block(0xA0000000) : [];
        return [.. header, .. items.ToArray(), .. Block(HasHeader ? 0x80000000 : 0)];
    }
}

/// <summary>ID3v2.2 uses three-letter frame ids: the ones the tool edits are brought up to v2.3 names, the rest are dropped.</summary>
internal static class MetadataId3V22
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["TT2"] = "TIT2", ["TP1"] = "TPE1", ["TAL"] = "TALB", ["TP2"] = "TPE2", ["TCO"] = "TCON", ["TYE"] = "TYER", ["TDA"] = "TDAT", ["TIM"] = "TIME",
        ["TRK"] = "TRCK", ["COM"] = "COMM", ["TCM"] = "TCOM", ["TCR"] = "TCOP", ["TPA"] = "TPOS", ["TT1"] = "TIT1", ["TT3"] = "TIT3", ["TP3"] = "TPE3",
        ["TEN"] = "TENC", ["TSS"] = "TSSE", ["TXT"] = "TEXT", ["TLA"] = "TLAN", ["TPB"] = "TPUB", ["TBP"] = "TBPM", ["TKE"] = "TKEY", ["TLE"] = "TLEN",
        ["TXX"] = "TXXX", ["ULT"] = "USLT",
    };

    public static MetadataId3Frame? Upgrade(string id, byte[] body)
    {
        if (id == "PIC" && body.Length >= 6)
        {
            // encoding, three-letter image format, picture type, description, data.
            var format = Encoding.Latin1.GetString(body, 1, 3).ToUpperInvariant();
            var mime = format switch { "JPG" => "image/jpeg", "PNG" => "image/png", _ => "image/" + format.ToLowerInvariant() };
            var wide = body[0] is 1 or 2;
            var pos = 5;
            while (pos + (wide ? 2 : 1) <= body.Length && !(wide ? body[pos] == 0 && body[pos + 1] == 0 : body[pos] == 0))
                pos += wide ? 2 : 1;
            pos += wide ? 2 : 1;
            return pos > body.Length ? null : MetadataId3.PictureFrame(mime, body[4], body[pos..]);
        }
        return Names.TryGetValue(id, out var name) ? new MetadataId3Frame(name, 0, body) : null;
    }
}
