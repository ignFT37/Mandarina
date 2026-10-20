// Apple Live Photo pairing: the content identifier the photo (Apple maker note) and its MOV (QuickTime metadata) share.

using System.Buffers.Binary;
using System.Text;
using ImageMagick;
using static Filee.Engines.Media.MovieBoxes;

namespace Filee.Engines.Media;

internal static class AppleLivePhoto
{
    internal const string ContentIdentifier = "com.apple.quicktime.content.identifier";
    private const int MaxMovieHeader = 16 * 1024 * 1024;

    /// <summary>The content identifier in the photo's Apple maker note (tag 17), or null.</summary>
    public static string? ImageIdentifier(string imagePath)
    {
        using var image = new MagickImage();
        image.Ping(imagePath);
        return ReadMakerNote(image.GetExifProfile()?.GetValue(ExifTag.MakerNote)?.Value);
    }

    internal static string? ReadMakerNote(byte[]? data)
    {
        if (data is null || data.Length < 16 || !data.AsSpan().StartsWith("Apple iOS\0"u8))
            return null;
        var little = data.AsSpan(12, 2).SequenceEqual("II"u8);
        if (!little && !data.AsSpan(12, 2).SequenceEqual("MM"u8))
            return null;
        ushort Read16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        uint Read32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
        var count = Read16(14);
        if (16L + count * 12L > data.Length)
            return null;
        for (var i = 0; i < count; i++)
        {
            var at = 16 + i * 12;
            if (Read16(at) != 17 || Read16(at + 2) != 2)
                continue;
            // Apple maker-note offsets are relative to the note itself.
            var length = Read32(at + 4);
            var offset = Read32(at + 8);
            if (length < 36 || length > 128 || offset > data.Length || length > data.Length - offset)
                return null;
            var id = Encoding.ASCII.GetString(data, (int)offset, (int)length).TrimEnd('\0');
            return Guid.TryParse(id, out var guid) ? guid.ToString("D") : null;
        }
        return null;
    }

    /// <summary>The content identifier in the movie's QuickTime metadata, or null.</summary>
    public static string? MovieIdentifier(string path)
    {
        using var file = File.OpenRead(path);
        var children = Children(ReadMovie(file));
        // Apple's movie-level meta is a plain container; other writers put a full-box meta under udta.
        var metadata = children.Where(c => c.Type == "meta").Select(c => c.Payload).ToList();
        foreach (var udta in children.Where(c => c.Type == "udta"))
            metadata.AddRange(Children(udta.Payload).Where(c => c.Type == "meta").Select(c => c.Payload));
        foreach (var data in metadata)
        {
            var atoms = Children(data, data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(new byte[4]) ? 4 : 0);
            var keys = atoms.FirstOrDefault(c => c.Type == "keys").Payload;
            var values = atoms.FirstOrDefault(c => c.Type == "ilst").Payload;
            if (keys is null || values is null || keys.Length < 8)
                continue;
            var keyList = Children(keys, 8);
            var index = keyList.FindIndex(k => k.Type == "mdta" && Encoding.UTF8.GetString(k.Payload) == ContentIdentifier) + 1;
            if (index == 0)
                continue;
            var itemType = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(itemType, (uint)index);
            foreach (var value in Children(values).Where(v => v.Type == Encoding.Latin1.GetString(itemType)))
            {
                var item = Children(value.Payload).FirstOrDefault(c => c.Type == "data").Payload;
                if (item is { Length: > 8 } && Guid.TryParse(Encoding.UTF8.GetString(item, 8, item.Length - 8), out var id))
                    return id.ToString("D");
            }
        }
        return null;
    }

    private static byte[] ReadMovie(Stream file)
    {
        file.Position = 0;
        while (file.Position < file.Length)
        {
            var box = ReadHeader(file);
            if (box.Type == "moov")
            {
                if (box.Length > MaxMovieHeader)
                    throw new InvalidDataException("Live Photo movie metadata is too large.");
                var data = new byte[checked((int)(box.Length - box.HeaderLength))];
                file.ReadExactly(data);
                return data;
            }
            file.Position = box.End;
        }
        throw new InvalidDataException("Live Photo has no movie metadata.");
    }
}
