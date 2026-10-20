// Mandarina: the METADATA tool's reader/editor of XMP, the XML that Adobe tools, phones and PDF writers keep next to the EXIF
// and the document info. It only changes properties that are already there (the same property in the same shape), removes
// them, or drops whole groups (a thumbnail); everything else in the packet is written back as it was.

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Filee.App.Mandarina.Tools;

/// <summary>The namespaces of the XMP properties the tool maps to its fields.</summary>
internal static class MetadataXmpNs
{
    public static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public static readonly XNamespace X = "adobe:ns:meta/";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    public static readonly XNamespace Tiff = "http://ns.adobe.com/tiff/1.0/";
    public static readonly XNamespace Exif = "http://ns.adobe.com/exif/1.0/";
    public static readonly XNamespace ExifEx = "http://cipa.jp/exif/1.0/";
    public static readonly XNamespace Aux = "http://ns.adobe.com/exif/1.0/aux/";
    public static readonly XNamespace Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    public static readonly XNamespace IptcCore = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";
    public static readonly XNamespace IptcExt = "http://iptc.org/std/Iptc4xmpExt/2008-02-29/";
    public static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    public static readonly XNamespace XmpRights = "http://ns.adobe.com/xap/1.0/rights/";
    public static readonly XNamespace Xml = XNamespace.Xml;
}

internal sealed class MetadataXmp
{
    private readonly XDocument _document;

    private MetadataXmp(XDocument document)
    {
        _document = document;
    }

    /// <summary>The XMP in <paramref name="packet"/> (UTF-8, with or without its xpacket wrapper); null when it is not readable XML.</summary>
    public static MetadataXmp? TryParse(ReadOnlySpan<byte> packet)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
            using var reader = XmlReader.Create(new MemoryStream(packet.ToArray()), settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return document.Root is null || document.Descendants(MetadataXmpNs.Rdf + "Description").FirstOrDefault() is null ? null : new MetadataXmp(document);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A packet with nothing in it yet (for a format that has no other place for some fields).</summary>
    public static MetadataXmp CreateEmpty()
    {
        var root = new XElement(MetadataXmpNs.X + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", MetadataXmpNs.X.NamespaceName),
            new XElement(MetadataXmpNs.Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", MetadataXmpNs.Rdf.NamespaceName),
                new XElement(MetadataXmpNs.Rdf + "Description", new XAttribute(MetadataXmpNs.Rdf + "about", ""))));
        return new MetadataXmp(new XDocument(root));
    }

    private IEnumerable<XElement> Descriptions => _document.Descendants(MetadataXmpNs.Rdf + "Description");

    // ───────── Reading ─────────

    /// <summary>The properties of the packet (a property is an attribute of an rdf:Description or one of its child elements).</summary>
    public IEnumerable<XName> PropertyNames()
    {
        foreach (var description in Descriptions)
        {
            foreach (var attribute in description.Attributes().Where(IsProperty))
                yield return attribute.Name;
            foreach (var element in description.Elements())
                yield return element.Name;
        }
    }

    public int PropertyCount => PropertyNames().Count();

    private static bool IsProperty(XAttribute attribute) =>
        !attribute.IsNamespaceDeclaration && attribute.Name != MetadataXmpNs.Rdf + "about" && attribute.Name.Namespace != MetadataXmpNs.Xml
        && attribute.Name.Namespace != MetadataXmpNs.X;

    public bool Has(XNamespace ns, string name) => Find(ns + name).Any();

    /// <summary>The text of a property: its value, or the first (default-language) item of its list. Null when absent or blank.</summary>
    public string? GetText(XNamespace ns, string name)
    {
        var key = ns + name;
        foreach (var description in Descriptions)
        {
            if (description.Attribute(key) is { } attribute && !string.IsNullOrWhiteSpace(attribute.Value))
                return attribute.Value.Trim();
            if (description.Element(key) is { } element && TextOf(element) is { Length: > 0 } text)
                return text;
        }
        return null;
    }

    private static string? TextOf(XElement element)
    {
        var container = element.Elements().FirstOrDefault(e => e.Name.Namespace == MetadataXmpNs.Rdf && e.Name.LocalName is "Alt" or "Seq" or "Bag");
        string? text;
        if (container is not null)
        {
            var items = container.Elements(MetadataXmpNs.Rdf + "li").ToList();
            var chosen = items.FirstOrDefault(i => (string?)i.Attribute(MetadataXmpNs.Xml + "lang") == "x-default") ?? items.FirstOrDefault();
            text = chosen?.Value;
        }
        else
        {
            text = element.HasElements ? null : element.Value;
        }
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private IEnumerable<(XElement Description, XAttribute? Attribute, XElement? Element)> Find(XName key)
    {
        foreach (var description in Descriptions)
        {
            var attribute = description.Attribute(key);
            var element = description.Element(key);
            if (attribute is not null || element is not null)
                yield return (description, attribute, element);
        }
    }

    // ───────── Editing ─────────

    /// <summary>
    /// Gives an existing property a new text, in the shape it already has (attribute, simple element, or the one default
    /// item of a language list or an array). Old translations and extra items go: they would still say the old value.
    /// Returns false when the property is not there.
    /// </summary>
    public bool SetText(XNamespace ns, string name, string value)
    {
        var found = false;
        foreach (var (_, attribute, element) in Find(ns + name).ToList())
        {
            found = true;
            attribute?.SetValue(value);
            if (element is null)
                continue;
            var container = element.Elements().FirstOrDefault(e => e.Name.Namespace == MetadataXmpNs.Rdf && e.Name.LocalName is "Alt" or "Seq" or "Bag");
            if (container is null)
            {
                element.ReplaceNodes(value);
                continue;
            }
            var isAlt = container.Name.LocalName == "Alt";
            var item = new XElement(MetadataXmpNs.Rdf + "li", value);
            if (isAlt)
                item.SetAttributeValue(MetadataXmpNs.Xml + "lang", "x-default");
            container.ReplaceNodes(item);
        }
        return found;
    }

    /// <summary>Removes a property wherever it is. Returns false when it was not there.</summary>
    public bool Remove(XNamespace ns, string name)
    {
        var found = false;
        foreach (var (_, attribute, element) in Find(ns + name).ToList())
        {
            found = true;
            attribute?.Remove();
            element?.Remove();
        }
        return found;
    }

    /// <summary>Removes every property whose name satisfies <paramref name="match"/> (a group such as the GPS properties). True when any went.</summary>
    public bool RemoveMatching(Func<XName, bool> match)
    {
        var removed = false;
        foreach (var description in Descriptions.ToList())
        {
            foreach (var attribute in description.Attributes().Where(a => IsProperty(a) && match(a.Name)).ToList())
            {
                attribute.Remove();
                removed = true;
            }
            foreach (var element in description.Elements().Where(e => match(e.Name)).ToList())
            {
                element.Remove();
                removed = true;
            }
        }
        return removed;
    }

    /// <summary>The kinds of value a property can have in XMP, for adding one that is not there yet.</summary>
    public enum Shape
    {
        /// <summary>A plain value: <c>&lt;ns:name&gt;text&lt;/ns:name&gt;</c>.</summary>
        Simple,

        /// <summary>A text with translations (dc:description, dc:rights, dc:title).</summary>
        LanguageAlternative,

        /// <summary>An ordered list (dc:creator).</summary>
        Sequence,
    }

    /// <summary>Sets a property, adding it to the first description in the given <paramref name="shape"/> when it is not there.</summary>
    public void SetOrAdd(XNamespace ns, string name, string value, Shape shape, string prefix)
    {
        if (SetText(ns, name, value))
            return;
        var description = Descriptions.First();
        if (description.GetPrefixOfNamespace(ns) is null)
            description.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
        var element = new XElement(ns + name);
        switch (shape)
        {
            case Shape.Simple:
                element.Value = value;
                break;
            case Shape.LanguageAlternative:
                element.Add(new XElement(MetadataXmpNs.Rdf + "Alt", new XElement(MetadataXmpNs.Rdf + "li", new XAttribute(MetadataXmpNs.Xml + "lang", "x-default"), value)));
                break;
            default:
                element.Add(new XElement(MetadataXmpNs.Rdf + "Seq", new XElement(MetadataXmpNs.Rdf + "li", value)));
                break;
        }
        description.Add(element);
    }

    /// <summary>Drops the thumbnails the packet carries (<c>xmp:Thumbnails</c>, a Photoshop thumbnail): small copies of the picture.</summary>
    public void RemoveThumbnails()
    {
        Remove(MetadataXmpNs.Xmp, "Thumbnails");
        Remove(MetadataXmpNs.Photoshop, "ThumbnailImage");
        foreach (var image in _document.Descendants().Where(e => e.Name.LocalName == "image" && e.Name.NamespaceName.Contains("/g/img/", StringComparison.Ordinal)).ToList())
            image.Remove();
    }

    // ───────── Writing ─────────

    /// <summary>The packet as bytes: UTF-8 inside the usual xpacket wrapper, without padding.</summary>
    public byte[] ToPacket()
    {
        var root = _document.Root!;
        var xml = root.ToString(SaveOptions.DisableFormatting);
        // The byte-order mark inside begin="" is the UTF-8 one (EF BB BF), written as the character U+FEFF.
        return new UTF8Encoding(false).GetBytes($"<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n{xml}\n<?xpacket end=\"w\"?>");
    }

    /// <summary>The XMP form of a GPS coordinate: "DD,MM.mmmmK" (K the hemisphere letter).</summary>
    public static string GpsCoordinate(double degrees, char positive, char negative)
    {
        var units = (long)Math.Round(Math.Abs(degrees) * 60 * 1_000_000, MidpointRounding.AwayFromZero);
        var whole = units / (60L * 1_000_000);
        var minutes = (units % (60L * 1_000_000)) / 1_000_000.0;
        return string.Create(CultureInfo.InvariantCulture, $"{whole},{minutes:0.######}{(degrees < 0 ? negative : positive)}");
    }

    /// <summary>Reads "DD,MM.mmmmK" or "DD,MM,SS.sK" into decimal degrees; null when it is neither.</summary>
    public static double? ParseGpsCoordinate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        var letter = char.ToUpperInvariant(text[^1]);
        var negative = letter is 'S' or 'W';
        if (letter is 'N' or 'S' or 'E' or 'W')
            text = text[..^1];
        var parts = text.Split(',');
        if (parts.Length is < 2 or > 3)
            return null;
        var culture = CultureInfo.InvariantCulture;
        if (!double.TryParse(parts[0], NumberStyles.Float, culture, out var degrees)
            || !double.TryParse(parts[1], NumberStyles.Float, culture, out var minutes))
            return null;
        var seconds = 0.0;
        if (parts.Length == 3 && !double.TryParse(parts[2], NumberStyles.Float, culture, out seconds))
            return null;
        var value = Math.Abs(degrees) + minutes / 60 + seconds / 3600;
        return negative || degrees < 0 ? -value : value;
    }
}
