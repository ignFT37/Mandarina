// Mandarina: puts the last touch on a PDF that PdfSharp has just written. PdfSharp's files are plain and regular (classic
// cross-reference table, every object written out, nothing compressed in object streams), so the objects can be copied as they
// are, the Info dictionary and the XMP stream replaced by what the METADATA tool wants, and a new table written. PdfSharp's own
// stamps (its name as producer and creator, the modification date, the XMP it generates, the "Created with PDFsharp" comment)
// do not survive.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal static partial class PdfMetadataRewriter
{
    [GeneratedRegex(@"/Info\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex InfoReference();

    [GeneratedRegex(@"/Root\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex RootReference();

    [GeneratedRegex(@"/Metadata\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex MetadataReference();

    [GeneratedRegex(@"/ID\s*\[[^\]]*\]")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"(\d+)\s+(\d+)\s+R")]
    private static partial Regex AnyReference();

    [GeneratedRegex(@">>\s*stream")]
    private static partial Regex StreamStart();

    /// <summary>
    /// Rewrites <paramref name="pdf"/>: the Info dictionary becomes <paramref name="infoDictionary"/> (null: none) and the document's
    /// XMP becomes <paramref name="xmp"/> (null: none). Everything else is copied as it is.
    /// </summary>
    public static byte[] Rewrite(byte[] pdf, string? infoDictionary, byte[]? xmp)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var startXref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (startXref < 0 || !long.TryParse(text.AsSpan(startXref + 9).Trim().ToString().Split('\n', '\r', ' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var xrefAt)
            || xrefAt < 0 || xrefAt + 4 > pdf.Length || !text.AsSpan((int)xrefAt, 4).SequenceEqual("xref"))
            throw new PanelException("The PDF could not be put together again.");

        // The cross-reference table.
        var objects = new SortedDictionary<int, (long Offset, int Generation)>();
        var cursor = (int)xrefAt + 4;
        int size = 0;
        while (true)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                cursor++;
            if (string.CompareOrdinal(text, cursor, "trailer", 0, 7) == 0)
            {
                cursor += 7;
                break;
            }
            var lineEnd = text.IndexOf('\n', cursor);
            var parts = text[cursor..lineEnd].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var count))
                throw new PanelException("The PDF could not be put together again.");
            cursor = lineEnd + 1;
            for (var i = 0; i < count; i++)
            {
                // Entries are 20 bytes: "0000000015 00000 n \n".
                var entry = text.Substring(cursor, 20);
                cursor += 20;
                if (entry[17] == 'n')
                    objects[first + i] = (long.Parse(entry[..10], CultureInfo.InvariantCulture), int.Parse(entry.AsSpan(11, 5), CultureInfo.InvariantCulture));
            }
            size = Math.Max(size, first + count);
        }
        var trailer = text[cursor..startXref];

        var rootMatch = RootReference().Match(trailer);
        if (!rootMatch.Success)
            throw new PanelException("The PDF could not be put together again.");
        var rootNumber = int.Parse(rootMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var infoMatch = InfoReference().Match(trailer);
        var infoNumber = infoMatch.Success ? int.Parse(infoMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var identifier = Identifier().Match(trailer);

        // Each object runs from its offset to the start of the next one.
        var ordered = objects.OrderBy(pair => pair.Value.Offset).ToList();
        var chunks = new Dictionary<int, byte[]>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var start = ordered[i].Value.Offset;
            var end = i + 1 < ordered.Count ? ordered[i + 1].Value.Offset : xrefAt;
            chunks[ordered[i].Key] = pdf.AsSpan((int)start, (int)(end - start)).ToArray();
        }
        var headerLength = ordered.Count > 0 ? (int)ordered[0].Value.Offset : 0;

        // The XMP: the catalog's reference is kept (and its stream replaced) or dropped.
        var catalog = Encoding.Latin1.GetString(chunks[rootNumber]);
        var metadataMatch = MetadataReference().Match(catalog);
        if (metadataMatch.Success)
        {
            var metadataNumber = int.Parse(metadataMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            if (xmp is null)
            {
                catalog = catalog.Remove(metadataMatch.Index, metadataMatch.Length);
                chunks[rootNumber] = Encoding.Latin1.GetBytes(catalog);
                chunks[metadataNumber] = Null(metadataNumber);
            }
            else
            {
                chunks[metadataNumber] = Stream(metadataNumber, xmp);
            }
        }
        else if (xmp is not null)
        {
            // Not expected from PdfSharp, but harmless: a new object for the stream, hung from the catalog.
            var number = size++;
            chunks[number] = Stream(number, xmp);
            var close = catalog.LastIndexOf(">>", StringComparison.Ordinal);
            catalog = catalog.Insert(close, $"/Metadata {number} 0 R");
            chunks[rootNumber] = Encoding.Latin1.GetBytes(catalog);
        }

        // The Info dictionary.
        if (infoNumber == 0 && infoDictionary is not null)
            infoNumber = size++;
        if (infoNumber != 0)
            chunks[infoNumber] = infoDictionary is null ? Null(infoNumber) : Encoding.Latin1.GetBytes($"{infoNumber} 0 obj\n{infoDictionary}\nendobj\n");

        // PdfSharp makes its own XMP after it has dropped the objects nobody refers to, so the original XMP is still in the file,
        // unreferenced; any other object nothing leads to goes the same way: its bytes must not carry the old values on.
        var reachable = Reachable(chunks, rootNumber, infoDictionary is null ? 0 : infoNumber);
        foreach (var number in chunks.Keys.ToList())
            if (!reachable.Contains(number))
                chunks[number] = Null(number);

        // The new file.
        using var output = new MemoryStream();
        output.Write(pdf.AsSpan(0, headerLength));
        var offsets = new long[size];
        foreach (var number in chunks.Keys.OrderBy(n => n))
        {
            offsets[number] = output.Position;
            output.Write(chunks[number]);
        }
        var xrefStart = output.Position;
        var table = new StringBuilder();
        table.Append("xref\n0 ").Append(size).Append('\n');
        table.Append("0000000000 65535 f \n");
        for (var number = 1; number < size; number++)
            table.Append(chunks.ContainsKey(number) ? $"{offsets[number]:D10} 00000 n \n" : "0000000000 65535 f \n");
        table.Append("trailer\n<<");
        if (identifier.Success)
            table.Append(identifier.Value);
        if (infoNumber != 0 && infoDictionary is not null)
            table.Append($"/Info {infoNumber} 0 R");
        table.Append($"/Root {rootNumber} 0 R/Size {size}>>\nstartxref\n{xrefStart}\n%%EOF\n");
        output.Write(Encoding.Latin1.GetBytes(table.ToString()));
        return output.ToArray();
    }

    /// <summary>The objects that can be reached from the catalog and the Info dictionary by following references.</summary>
    private static HashSet<int> Reachable(Dictionary<int, byte[]> chunks, int root, int info)
    {
        var seen = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(root);
        if (info != 0)
            pending.Push(info);
        while (pending.Count > 0)
        {
            var number = pending.Pop();
            if (!chunks.TryGetValue(number, out var chunk) || !seen.Add(number))
                continue;
            var text = Encoding.Latin1.GetString(chunk);
            var stream = StreamStart().Match(text);
            var end = stream.Success ? stream.Index + 2 : text.Length;
            foreach (Match reference in AnyReference().Matches(text, 0).Where(m => m.Index < end))
                pending.Push(int.Parse(reference.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return seen;
    }

    private static byte[] Null(int number) => Encoding.Latin1.GetBytes($"{number} 0 obj\nnull\nendobj\n");

    private static byte[] Stream(int number, byte[] data)
    {
        var head = Encoding.Latin1.GetBytes($"{number} 0 obj\n<</Type/Metadata/Subtype/XML/Length {data.Length}>>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream\nendobj\n");
        return [.. head, .. data, .. tail];
    }

    // ───────── Text in a PDF ─────────

    /// <summary>A PDF text string: a literal for plain ASCII, UTF-16 with its byte-order mark in hex for anything else.</summary>
    public static string TextToken(string value)
    {
        if (value.All(c => c is >= ' ' and <= '~'))
            return "(" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal) + ")";
        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
    }

    /// <summary>A PDF date: <c>D:20240517102030+02'00'</c>.</summary>
    public static string DateText(DateTime value)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(value);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"D:{value:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'");
    }
}
