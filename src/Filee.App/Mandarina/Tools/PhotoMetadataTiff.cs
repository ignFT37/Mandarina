// Mandarina: the TIFF driver of the METADATA tool. A TIFF file is already a TIFF structure: the file is copied and its IFDs are
// edited in place, so the strips or tiles of the picture stay exactly where they were (16-bit pictures, several pages,
// compression and tiling are all untouched). Tags that were removed or replaced are overwritten with zeros.

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal static class PhotoMetadataTiff
{
    /// <summary>The tags of every page that describe who made the picture, with what, and when; the rest says how to read the pixels.</summary>
    private static readonly ushort[] Descriptive =
    [
        MetadataTiff.ImageDescription, MetadataTiff.Make, MetadataTiff.Model, MetadataTiff.Software, MetadataTiff.DateTime, MetadataTiff.Artist,
        MetadataTiff.HostComputer, MetadataTiff.DocumentName, MetadataTiff.PageName, MetadataTiff.Copyright, MetadataTiff.XmpPacket, MetadataTiff.IptcNaa,
        MetadataTiff.PhotoshopResources, MetadataTiff.XpTitle, MetadataTiff.XpComment, MetadataTiff.XpAuthor, MetadataTiff.XpKeywords, MetadataTiff.XpSubject,
    ];

    internal static PhotoMetadataTags Load(MetadataTiff tiff)
    {
        var tags = new PhotoMetadataTags { Exif = tiff };
        if (tiff.Find(MetadataTiffIfd.Main, MetadataTiff.XmpPacket) is { } xmp)
        {
            tags.Xmp = MetadataXmp.TryParse(xmp.Data);
            tags.XmpUnreadable = tags.Xmp is null;
        }
        if (tiff.Find(MetadataTiffIfd.Main, MetadataTiff.PhotoshopResources) is { } photoshop)
        {
            tags.Iptc = MetadataIptc.FromPhotoshop(photoshop.Data);
            tags.IptcUnreadable = tags.Iptc is null;
        }
        else if (tiff.Find(MetadataTiffIfd.Main, MetadataTiff.IptcNaa) is { } iptc)
        {
            tags.Iptc = MetadataIptc.FromRaw(iptc.Data);
            tags.IptcUnreadable = tags.Iptc is null;
        }
        return tags;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="output"/> and edits the copy in place.</summary>
    internal static void Edit(string source, string output, IReadOnlyDictionary<PhotoMetadataField, string?> changes)
    {
        File.Copy(source, output, overwrite: false);
        using var stream = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var tiff = MetadataTiff.Open(stream, isFile: true) ?? throw new PanelException("This is not a TIFF picture that can be edited.");
        var tags = Load(tiff);
        PhotoMetadata.Apply(tags, changes);

        if (tags.XmpChanged)
        {
            if (tags.Xmp is { } xmp)
                tiff.SetBytes(MetadataTiffIfd.Main, MetadataTiff.XmpPacket, MetadataTiff.TypeByte, xmp.ToPacket());
            else
                tiff.Remove(MetadataTiffIfd.Main, MetadataTiff.XmpPacket);
        }
        if (tags.IptcChanged)
        {
            if (tags.Iptc is not { } iptc || iptc.IsEmpty)
            {
                tiff.Remove(MetadataTiffIfd.Main, MetadataTiff.PhotoshopResources);
                tiff.Remove(MetadataTiffIfd.Main, MetadataTiff.IptcNaa);
            }
            else if (tiff.Has(MetadataTiffIfd.Main, MetadataTiff.PhotoshopResources))
            {
                tiff.SetBytes(MetadataTiffIfd.Main, MetadataTiff.PhotoshopResources, MetadataTiff.TypeByte, iptc.ToPhotoshop());
            }
            else
            {
                tiff.SetBytes(MetadataTiffIfd.Main, MetadataTiff.IptcNaa, MetadataTiff.TypeUndefined, iptc.ToRaw());
            }
        }
        tiff.Commit();
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="output"/> and removes every descriptive tag of every page from the copy.</summary>
    internal static void Strip(string source, string output)
    {
        File.Copy(source, output, overwrite: false);
        using var stream = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var pages = MetadataTiff.PageOffsets(stream);
        if (pages.Count == 0)
            throw new PanelException("This is not a TIFF picture that can be edited.");
        for (var i = 0; i < pages.Count; i++)
        {
            var tiff = (i == 0 ? MetadataTiff.Open(stream, isFile: true) : MetadataTiff.OpenPage(stream, pages[i]))
                ?? throw new PanelException("This is not a TIFF picture that can be edited.");
            foreach (var tag in Descriptive)
                tiff.Remove(MetadataTiffIfd.Main, tag);
            tiff.RemoveGps();
            tiff.RemoveExifIfd();
            tiff.Commit();
        }
    }
}
