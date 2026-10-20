// Mandarina: the PDF side of the METADATA tool. PdfSharp opens the document and writes it again, so the pages, fonts, images and
// outlines are untouched; but PdfSharp also stamps every PDF it saves with its own producer, a creator, a modification date and a
// fresh XMP packet built from the Info dictionary. So the file it writes is read once more (PdfMetadataRewriter) and exactly what the
// user chose is put in its place: the Info entries (or none), the XMP of the original edited to match (or none).

using System.Text;
using Filee.App.Mandarina.Panels;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Mandarina.Tools;

internal enum PdfMetadataField
{
    Title,
    Author,
    Subject,
    Keywords,
    Creator,
    Producer,
}

/// <summary>The PDF asks for a password, so it cannot be changed.</summary>
internal sealed class PdfMetadataProtectedException(Exception inner) : Exception("The PDF is password-protected.", inner);

/// <summary>What the panel needs to know about a PDF: the text of each field it has and how many tags it carries.</summary>
internal sealed record PdfMetadataInfo(IReadOnlyDictionary<PdfMetadataField, string> Values, int Tags, int OtherTags);

internal static class PdfMetadataEditor
{
    private static readonly PdfMetadataField[] Fields = Enum.GetValues<PdfMetadataField>();

    /// <summary>The Info keys the fields stand for.</summary>
    private static string Key(PdfMetadataField field) => "/" + field;

    /// <summary>The XMP property of each field (dc:title…), and how it is written there.</summary>
    private static (System.Xml.Linq.XNamespace Ns, string Name) Property(PdfMetadataField field) => field switch
    {
        PdfMetadataField.Title => (MetadataXmpNs.Dc, "title"),
        PdfMetadataField.Author => (MetadataXmpNs.Dc, "creator"),
        PdfMetadataField.Subject => (MetadataXmpNs.Dc, "description"),
        PdfMetadataField.Keywords => (MetadataXmpNs.Pdf, "Keywords"),
        PdfMetadataField.Creator => (MetadataXmpNs.Xmp, "CreatorTool"),
        _ => (MetadataXmpNs.Pdf, "Producer"),
    };

    // ───────── Reading ─────────

    /// <summary>Reads the Info dictionary and the XMP of <paramref name="path"/>. Synchronous: call it from a background task.</summary>
    public static PdfMetadataInfo Read(string path)
    {
        using var document = Open(path, PdfDocumentOpenMode.Import);
        var info = ReadInfo(document);
        var xmp = ReadXmp(document);
        var values = new Dictionary<PdfMetadataField, string>();
        foreach (var field in Fields)
        {
            var text = info.GetValueOrDefault(Key(field));
            if (string.IsNullOrWhiteSpace(text) && xmp is not null)
                text = xmp.GetText(Property(field).Ns, Property(field).Name);
            if (!string.IsNullOrWhiteSpace(text))
                values[field] = text.Trim();
        }
        var tags = info.Count(pair => !string.IsNullOrEmpty(pair.Value)) + (xmp?.PropertyCount ?? 0);
        var covered = values.Count;
        return new PdfMetadataInfo(values, tags, Math.Max(0, tags - covered));
    }

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfMetadataProtectedException(ex);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException or FormatException or InvalidCastException or NullReferenceException)
        {
            throw new PanelException("This PDF cannot be read: " + ex.Message, ex);
        }
    }

    /// <summary>Every entry of the Info dictionary as text (dates as the "D:2024…" string).</summary>
    private static Dictionary<string, string> ReadInfo(PdfDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in document.Info.Elements.Keys)
        {
            var text = document.Info.Elements[key] switch
            {
                PdfString s => s.Value,
                PdfName n => n.Value,
                PdfDate d => d.Value.ToString("O"),
                var other => other?.ToString(),
            };
            if (text is not null)
                result[key] = text;
        }
        return result;
    }

    private static MetadataXmp? ReadXmp(PdfDocument document)
    {
        var bytes = ReadXmpBytes(document);
        return bytes is null ? null : MetadataXmp.TryParse(bytes);
    }

    private static byte[]? ReadXmpBytes(PdfDocument document)
    {
        try
        {
            if (document.Internals.Catalog.Elements.GetDictionary("/Metadata") is { Stream: { } stream })
                return stream.UnfilteredValue;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or NotSupportedException or ArgumentException or IOException)
        {
        }
        return null;
    }

    // ───────── Writing ─────────

    /// <summary>
    /// Writes a copy of <paramref name="source"/> at <paramref name="output"/> with <paramref name="changes"/> applied to the Info
    /// dictionary and to the original XMP; a field with null or empty text is removed. Fields not in the dictionary are untouched.
    /// </summary>
    public static void Edit(string source, string output, IReadOnlyDictionary<PdfMetadataField, string?> changes)
    {
        Info original;
        byte[]? originalXmp;
        using (var read = Open(source, PdfDocumentOpenMode.Import))
        {
            original = Info.From(read);
            originalXmp = ReadXmpBytes(read);
        }

        var finalInfo = original.Clone();
        foreach (var (field, value) in changes)
        {
            if (string.IsNullOrEmpty(value))
                finalInfo.Remove(Key(field));
            else
                finalInfo.SetText(Key(field), value);
        }

        byte[]? xmpBytes = null;
        if (originalXmp is not null)
        {
            var xmp = MetadataXmp.TryParse(originalXmp);
            if (xmp is not null)
            {
                foreach (var (field, value) in changes)
                {
                    var (ns, name) = Property(field);
                    if (string.IsNullOrEmpty(value))
                        xmp.Remove(ns, name);
                    else
                        xmp.SetText(ns, name, value);
                }
                xmpBytes = xmp.ToPacket();
            }
            else
            {
                // An XMP that cannot be read cannot be kept in step with the Info: it goes rather than say the old values.
                xmpBytes = null;
            }
        }
        Save(source, output, finalInfo, xmpBytes, strip: false);
    }

    /// <summary>Writes a copy of <paramref name="source"/> at <paramref name="output"/> with no Info, no XMP and no authors on comments.</summary>
    public static void Strip(string source, string output) => Save(source, output, info: null, xmp: null, strip: true);

    private static void Save(string source, string output, Info? info, byte[]? xmp, bool strip)
    {
        byte[] saved;
        using (var document = Open(source, PdfDocumentOpenMode.Modify))
        {
            if (strip)
                RemoveHiddenMetadata(document);
            using var stream = new MemoryStream();
            document.Save(stream, closeStream: false);
            saved = stream.ToArray();
        }
        var rewritten = PdfMetadataRewriter.Rewrite(saved, info?.ToDictionaryText(), xmp);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(rewritten);
    }

    private static readonly HashSet<string> MarkupAnnotations =
    [
        "/Text", "/FreeText", "/Line", "/Square", "/Circle", "/Polygon", "/PolyLine", "/Highlight", "/Underline", "/Squiggly", "/StrikeOut",
        "/Stamp", "/Caret", "/Ink", "/FileAttachment", "/Sound", "/Redact",
    ];

    /// <summary>
    /// Removes what a PDF keeps about its history outside the Info dictionary and the catalog's XMP: the metadata streams of pages
    /// and other objects, the private data of the program that made it, the modification date of a page, the name and date
    /// that comments (not form fields) carry, and the EXIF, XMP and IPTC of the photos inside (a PDF made from phone photos holds
    /// each JPEG as it came, GPS included; its compressed picture is not touched).
    /// </summary>
    private static void RemoveHiddenMetadata(PdfDocument document)
    {
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is not PdfDictionary dictionary)
                continue;
            var elements = dictionary.Elements;
            elements.Remove("/Metadata");
            elements.Remove("/PieceInfo");
            elements.Remove("/LastModified");
            if (elements.ContainsKey("/Subtype") && elements.ContainsKey("/Rect") && elements["/Subtype"] is PdfName subtype && MarkupAnnotations.Contains(subtype.Value))
            {
                elements.Remove("/T");
                elements.Remove("/M");
                elements.Remove("/CreationDate");
            }
            if (dictionary.Stream is { } stream && IsJpegImage(elements))
                CleanPhoto(stream);
        }
    }

    /// <summary>True for an image whose stream is a plain JPEG (DCTDecode is its only filter).</summary>
    private static bool IsJpegImage(PdfDictionary.DictionaryElements elements)
    {
        if (elements["/Subtype"] is not PdfName { Value: "/Image" })
            return false;
        return elements["/Filter"] switch
        {
            PdfName name => name.Value == "/DCTDecode",
            PdfArray { Elements.Count: 1 } array => array.Elements[0] is PdfName { Value: "/DCTDecode" },
            _ => false,
        };
    }

    private static void CleanPhoto(PdfDictionary.PdfStream stream)
    {
        var original = stream.Value;
        if (!MetadataJpeg.LooksLikeJpeg(original))
            return;
        try
        {
            var clean = PhotoMetadataJpeg.Strip(original);
            if (clean.Length < original.Length)
                stream.Value = clean;
        }
        catch (Exception ex) when (ex is InvalidDataException or PanelException)
        {
            // A JPEG that cannot be followed is left as it is.
        }
    }

    /// <summary>The Info dictionary as an ordered list of keys and values, in the form PdfMetadataRewriter writes it.</summary>
    internal sealed class Info
    {
        private readonly List<(string Key, string Token)> _entries = [];

        public static Info From(PdfDocument document)
        {
            var info = new Info();
            foreach (var key in document.Info.Elements.Keys)
            {
                switch (document.Info.Elements[key])
                {
                    case PdfString s:
                        info._entries.Add((key, PdfMetadataRewriter.TextToken(s.Value)));
                        break;
                    case PdfName n:
                        info._entries.Add((key, n.Value));
                        break;
                    case PdfDate d:
                        info._entries.Add((key, PdfMetadataRewriter.TextToken(PdfMetadataRewriter.DateText(d.Value))));
                        break;
                }
            }
            // PdfSharp stamps the document it opens with the time of the opening and, on saving, with its own name.
            // What the file had is what is kept.
            return info;
        }

        public Info Clone()
        {
            var copy = new Info();
            copy._entries.AddRange(_entries);
            return copy;
        }

        public void Remove(string key) => _entries.RemoveAll(e => e.Key == key);

        public void SetText(string key, string value)
        {
            var token = PdfMetadataRewriter.TextToken(value);
            var index = _entries.FindIndex(e => e.Key == key);
            if (index >= 0)
                _entries[index] = (key, token);
            else
                _entries.Add((key, token));
        }

        public string ToDictionaryText() => "<<" + string.Concat(_entries.Select(e => e.Key + e.Token)) + ">>";
    }
}
