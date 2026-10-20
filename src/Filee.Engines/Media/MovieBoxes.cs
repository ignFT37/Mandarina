// Small, bounds-checked ISO BMFF box readers for photo metadata; media payloads are streamed, never loaded whole.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Media;

internal readonly record struct MovieBox(string Type, long Start, long Length, int HeaderLength)
{
    public long End => checked(Start + Length);
}

internal static class MovieBoxes
{
    /// <summary>Reads the box header at the stream's position; a box reaching past the end of the stream is refused.</summary>
    public static MovieBox ReadHeader(Stream file)
    {
        var start = file.Position;
        Span<byte> header = stackalloc byte[8];
        file.ReadExactly(header);
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = Encoding.Latin1.GetString(header[4..]);
        var headerLength = 8;
        if (size == 1)
        {
            file.ReadExactly(header);
            var extended = BinaryPrimitives.ReadUInt64BigEndian(header);
            if (extended > long.MaxValue)
                throw new InvalidDataException("Media box is too large.");
            size = (long)extended;
            headerLength = 16;
        }
        else if (size == 0)
            size = file.Length - start;
        if (size < headerLength || size > file.Length - start)
            throw new InvalidDataException("Media box extends outside its container.");
        return new(type, start, size, headerLength);
    }

    /// <summary>The boxes in <paramref name="data"/> (a parent's payload), after <paramref name="skip"/> bytes.</summary>
    public static List<(string Type, byte[] Payload)> Children(byte[] data, int skip = 0)
    {
        using var stream = new MemoryStream(data);
        stream.Position = skip;
        var result = new List<(string, byte[])>();
        while (stream.Position < stream.Length)
        {
            var box = ReadHeader(stream);
            var payload = new byte[checked((int)(box.Length - box.HeaderLength))];
            stream.ReadExactly(payload);
            result.Add((box.Type, payload));
        }
        return result;
    }

    /// <summary>Copies exactly <paramref name="count"/> bytes; a source that ends sooner is an error.</summary>
    public static async Task CopyRangeAsync(Stream source, Stream target, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), ct);
            if (read == 0)
                throw new EndOfStreamException();
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            count -= read;
        }
    }
}
