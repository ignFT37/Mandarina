// Mandarina: the METADATA tool's idea of a photo's tags, whatever the file format. The same fact lives in several places of a
// picture (the author is in the EXIF, in the XMP, in the IPTC and in a PNG text chunk), so a field that is changed or removed
// has to change in every place that holds it, or the old value would still be in the file. This class knows which places
// belong to which field; the format drivers (JPEG, PNG, WebP, TIFF, GIF, Magick) only find the blocks and write them back.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.App.Mandarina.Tools;

/// <summary>The fields of the photo panel.</summary>
internal enum PhotoMetadataField
{
    Description,
    Author,
    Copyright,
    Make,
    Model,
    Lens,
    Software,
    Date,
    Latitude,
    Longitude,
}

/// <summary>A free text a format keeps outside EXIF and XMP: a PNG text chunk (with its keyword), a JPEG or GIF comment (no keyword).</summary>
internal sealed class PhotoMetadataText(string? key, string value, object? source = null)
{
    public string? Key { get; } = key;

    public string Value { get; set; } = value;

    /// <summary>The text the file had.</summary>
    public string Original { get; } = value;

    /// <summary>True when the text has a new value that has to be written (not when it was removed).</summary>
    public bool Changed => !Removed && Value != Original;

    /// <summary>What the driver needs to find the chunk again when it writes.</summary>
    public object? Source { get; } = source;

    public bool Removed { get; set; }
}

internal sealed class PhotoMetadataTags
{
    private static readonly string[] DescriptionKeys = ["Description", "Comment"];

    /// <summary>The EXIF (or, for a TIFF file, the IFDs of the file). Null when the picture has none yet.</summary>
    public MetadataTiff? Exif { get; set; }

    public MetadataXmp? Xmp { get; set; }

    /// <summary>An XMP block exists but could not be read: a change to a field it may hold removes the block instead.</summary>
    public bool XmpUnreadable { get; set; }

    /// <summary>True when something wrote the XMP that the driver has to put back.</summary>
    public bool XmpChanged { get; set; }

    public MetadataIptc? Iptc { get; set; }

    public bool IptcUnreadable { get; set; }

    public bool IptcChanged { get; set; }

    public List<PhotoMetadataText> Texts { get; } = [];

    /// <summary>False for a format that has no EXIF (GIF): the fields it can hold go to XMP, created when needed.</summary>
    public bool CanStoreExif { get; init; } = true;

    /// <summary>True for a format with no place at all for these tags (BMP).</summary>
    public bool CannotStoreAnything { get; init; }

    /// <summary>Bytes the file carries after the picture (a phone's video, depth or HDR map after a JPEG): "Remove all" drops them.</summary>
    public long AttachedBytes { get; set; }

    /// <summary>The date to put in a PNG's last-change chunk: set when a date field changed, so the driver drops the old one.</summary>
    public bool DateTouched { get; private set; }

    private bool _gpsTouched;

    // ───────── Reading ─────────

    private string? Xmpt(XNamespace ns, string name) => Xmp?.GetText(ns, name);

    private string? TextFor(params string[] keys) =>
        Texts.Where(t => !t.Removed && t.Key is not null && keys.Contains(t.Key, StringComparer.OrdinalIgnoreCase))
            .Select(t => t.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private string? CommentText() => Texts.Where(t => !t.Removed && t.Key is null).Select(t => t.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public string? Get(PhotoMetadataField field)
    {
        switch (field)
        {
            case PhotoMetadataField.Description:
                return First(Exif?.GetString(MetadataTiffIfd.Main, MetadataTiff.ImageDescription), Exif?.GetXpString(MetadataTiffIfd.Main, MetadataTiff.XpComment),
                    Exif?.GetUserComment(), Xmpt(MetadataXmpNs.Dc, "description"), Xmpt(MetadataXmpNs.Tiff, "ImageDescription"),
                    Iptc?.GetText(MetadataIptc.CaptionAbstract), TextFor(DescriptionKeys), CommentText());
            case PhotoMetadataField.Author:
                return First(Exif?.GetString(MetadataTiffIfd.Main, MetadataTiff.Artist), Exif?.GetXpString(MetadataTiffIfd.Main, MetadataTiff.XpAuthor),
                    Xmpt(MetadataXmpNs.Dc, "creator"), Xmpt(MetadataXmpNs.Tiff, "Artist"), Iptc?.GetText(MetadataIptc.ByLine), TextFor("Author"));
            case PhotoMetadataField.Copyright:
                return First(Exif?.GetString(MetadataTiffIfd.Main, MetadataTiff.Copyright), Xmpt(MetadataXmpNs.Dc, "rights"), Xmpt(MetadataXmpNs.Tiff, "Copyright"),
                    Iptc?.GetText(MetadataIptc.CopyrightNotice), TextFor("Copyright"));
            case PhotoMetadataField.Make:
                return First(Exif?.GetString(MetadataTiffIfd.Main, MetadataTiff.Make), Xmpt(MetadataXmpNs.Tiff, "Make"));
            case PhotoMetadataField.Model:
                return First(Exif?.GetString(MetadataTiffIfd.Main, MetadataTiff.Model), Xmpt(MetadataXmpNs.Tiff, "Model"));
            case PhotoMetadataField.Lens:
                return First(Exif?.GetString(MetadataTiffIfd.Exif, MetadataTiff.LensModel), Xmpt(MetadataXmpNs.Aux, "Lens"), Xmpt(MetadataXmpNs.ExifEx, "LensModel"));
            case PhotoMetadataField.Software:
                return First(Exif?.GetString(MetadataTiffIfd.Main, MetadataTiff.Software), Xmpt(MetadataXmpNs.Xmp, "CreatorTool"), Xmpt(MetadataXmpNs.Tiff, "Software"),
                    Iptc?.GetText(MetadataIptc.OriginatingProgram), TextFor("Software"));
            case PhotoMetadataField.Date:
                return ReadDate();
            case PhotoMetadataField.Latitude:
                return ReadPosition() is { } latitude ? MetadataValues.ForUser(latitude.Latitude) : null;
            default:
                return ReadPosition() is { } longitude ? MetadataValues.ForUser(longitude.Longitude) : null;
        }
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private string? ReadDate()
    {
        string?[] candidates =
        [
            Exif?.GetString(MetadataTiffIfd.Exif, MetadataTiff.DateTimeOriginal), Exif?.GetString(MetadataTiffIfd.Exif, MetadataTiff.DateTimeDigitized),
            Exif?.GetString(MetadataTiffIfd.Main, MetadataTiff.DateTime), Xmpt(MetadataXmpNs.Exif, "DateTimeOriginal"), Xmpt(MetadataXmpNs.Photoshop, "DateCreated"),
            Xmpt(MetadataXmpNs.Xmp, "CreateDate"), Xmpt(MetadataXmpNs.Tiff, "DateTime"), IptcDate(), TextFor("Creation Time"),
        ];
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (MetadataValues.TryParseLooseDate(candidate, out var date))
                return MetadataValues.ForUser(date);
        }
        // A value that is not a date (a blank template such as "0000:00:00 00:00:00") is shown as it is, so it can be seen and removed.
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
    }

    private string? IptcDate()
    {
        var date = Iptc?.GetText(MetadataIptc.DateCreated);
        if (date is not { Length: >= 8 })
            return null;
        var time = Iptc?.GetText(MetadataIptc.TimeCreated);
        return time is { Length: >= 6 } && time[..6].All(char.IsAsciiDigit) ? $"{date[..8]} {time[..2]}:{time[2..4]}:{time[4..6]}" : date;
    }

    private (double Latitude, double Longitude)? ReadPosition()
    {
        if (Exif?.GetGps() is { } position)
            return position;
        if (MetadataXmp.ParseGpsCoordinate(Xmpt(MetadataXmpNs.Exif, "GPSLatitude")) is { } latitude
            && MetadataXmp.ParseGpsCoordinate(Xmpt(MetadataXmpNs.Exif, "GPSLongitude")) is { } longitude)
            return (latitude, longitude);
        return null;
    }

    /// <summary>True when the picture carries a position or something located (names of places), so there is a location to remove.</summary>
    public bool HasLocation =>
        Exif?.HasGpsIfd == true || Xmp?.PropertyNames().Any(IsXmpLocation) == true || (Iptc is not null && LocationDatasets.Any(Iptc.Has));

    // ───────── What belongs to which field ─────────

    private static readonly int[] LocationDatasets =
        [MetadataIptc.City, MetadataIptc.SubLocation, MetadataIptc.ProvinceState, MetadataIptc.CountryCode, MetadataIptc.CountryName];

    private static bool IsXmpLocation(XName name) =>
        (name.Namespace == MetadataXmpNs.Exif && name.LocalName.StartsWith("GPS", StringComparison.Ordinal))
        || (name.Namespace == MetadataXmpNs.Photoshop && name.LocalName is "City" or "State" or "Country")
        || (name.Namespace == MetadataXmpNs.IptcCore && name.LocalName is "Location" or "CountryCode")
        || (name.Namespace == MetadataXmpNs.IptcExt && name.LocalName is "LocationShown" or "LocationCreated");

    private static bool IsXmpDate(XName name) =>
        (name.Namespace == MetadataXmpNs.Exif && name.LocalName is "DateTimeOriginal" or "DateTimeDigitized" or "SubSecTimeOriginal" or "SubSecTimeDigitized"
            or "OffsetTimeOriginal" or "OffsetTimeDigitized" or "GPSTimeStamp")
        || (name.Namespace == MetadataXmpNs.Xmp && name.LocalName is "CreateDate" or "ModifyDate" or "MetadataDate")
        || (name.Namespace == MetadataXmpNs.Photoshop && name.LocalName == "DateCreated")
        || (name.Namespace == MetadataXmpNs.Tiff && name.LocalName == "DateTime");

    /// <summary>The number of tags this field accounts for in the file: the count the "+ N more tags" line leaves out.</summary>
    public int Covered()
    {
        var covered = 0;
        if (Exif is { } exif)
        {
            ushort[] main =
            [
                MetadataTiff.ImageDescription, MetadataTiff.XpComment, MetadataTiff.Artist, MetadataTiff.XpAuthor, MetadataTiff.Copyright, MetadataTiff.Make,
                MetadataTiff.Model, MetadataTiff.Software, MetadataTiff.DateTime,
            ];
            ushort[] sub =
            [
                MetadataTiff.LensModel, MetadataTiff.DateTimeOriginal, MetadataTiff.DateTimeDigitized, MetadataTiff.UserComment,
            ];
            covered += exif.Entries(MetadataTiffIfd.Main).Count(v => main.Contains(v.Tag));
            covered += exif.Entries(MetadataTiffIfd.Exif).Count(v => sub.Contains(v.Tag));
            covered += exif.Entries(MetadataTiffIfd.Gps).Count(v => v.Tag is >= MetadataTiff.GpsLatitudeRef and <= MetadataTiff.GpsLongitude);
        }
        if (Xmp is { } xmp)
        {
            (XNamespace, string)[] mapped =
            [
                (MetadataXmpNs.Dc, "description"), (MetadataXmpNs.Dc, "creator"), (MetadataXmpNs.Dc, "rights"), (MetadataXmpNs.Tiff, "ImageDescription"), (MetadataXmpNs.Tiff, "Artist"),
                (MetadataXmpNs.Tiff, "Copyright"), (MetadataXmpNs.Tiff, "Make"), (MetadataXmpNs.Tiff, "Model"), (MetadataXmpNs.Tiff, "Software"), (MetadataXmpNs.Tiff, "DateTime"),
                (MetadataXmpNs.Aux, "Lens"), (MetadataXmpNs.ExifEx, "LensModel"), (MetadataXmpNs.Xmp, "CreatorTool"), (MetadataXmpNs.Xmp, "CreateDate"),
                (MetadataXmpNs.Exif, "DateTimeOriginal"), (MetadataXmpNs.Exif, "GPSLatitude"), (MetadataXmpNs.Exif, "GPSLongitude"), (MetadataXmpNs.Photoshop, "DateCreated"),
            ];
            covered += xmp.PropertyNames().Count(name => mapped.Any(m => m.Item1 + m.Item2 == name));
        }
        if (Iptc is { } iptc)
        {
            int[] known =
            [
                MetadataIptc.CaptionAbstract, MetadataIptc.ByLine, MetadataIptc.CopyrightNotice, MetadataIptc.OriginatingProgram,
                MetadataIptc.DateCreated, MetadataIptc.TimeCreated,
            ];
            covered += iptc.Numbers().Count(known.Contains);
        }
        covered += Texts.Count(t => !t.Removed && (t.Key is null || DescriptionKeys.Concat(["Author", "Copyright", "Software", "Creation Time"]).Contains(t.Key, StringComparer.OrdinalIgnoreCase)));
        return covered;
    }

    /// <summary>How many tags the picture carries in all its blocks.</summary>
    public int Total() =>
        (Exif?.TagCount ?? 0) + (Xmp?.PropertyCount ?? 0) + (Iptc?.Count ?? 0) + Texts.Count(t => !t.Removed);

    // ───────── Editing ─────────

    /// <summary>Makes sure the EXIF exists for a change that has to go into it.</summary>
    private MetadataTiff? ExifForWriting() => CanStoreExif ? Exif ??= MetadataTiff.CreateEmpty() : null;

    /// <summary>
    /// The XMP to change, marked as changed. A block that could not be read cannot be edited: it is marked changed too, which
    /// makes the driver drop it (its old values must not stay). A format without EXIF gets a new one for its fields.
    /// </summary>
    private MetadataXmp? XmpForWriting()
    {
        if (XmpUnreadable)
        {
            XmpChanged = true;
            return null;
        }
        if (Xmp is null && !CanStoreExif && !CannotStoreAnything)
            Xmp = MetadataXmp.CreateEmpty();
        if (Xmp is not null)
            XmpChanged = true;
        return Xmp;
    }

    private MetadataIptc? IptcForWriting()
    {
        if (IptcUnreadable)
        {
            IptcChanged = true;
            return null;
        }
        if (Iptc is not null)
            IptcChanged = true;
        return Iptc;
    }

    /// <summary>
    /// Sets a field. An empty value removes it. Latitude and longitude go together through <see cref="SetLocation"/>; a date is
    /// the panel's "yyyy-MM-dd HH:mm:ss".
    /// </summary>
    public void Set(PhotoMetadataField field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Remove(field);
            return;
        }
        switch (field)
        {
            case PhotoMetadataField.Description:
                SetAscii(MetadataTiff.ImageDescription, value);
                // Windows' "Comments" and the Exif user comment hold the same kind of text: if they are there they must say the same.
                if (Exif?.Has(MetadataTiffIfd.Main, MetadataTiff.XpComment) == true)
                    Exif.SetXpString(MetadataTiffIfd.Main, MetadataTiff.XpComment, value);
                if (Exif?.Has(MetadataTiffIfd.Exif, MetadataTiff.UserComment) == true)
                    Exif.SetUserComment(value);
                SetXmp(MetadataXmpNs.Dc, "description", value, MetadataXmp.Shape.LanguageAlternative, "dc");
                SetXmp(MetadataXmpNs.Tiff, "ImageDescription", value, MetadataXmp.Shape.Simple, "tiff");
                IptcSet(MetadataIptc.CaptionAbstract, value);
                SetTexts(value, DescriptionKeys);
                SetComments(value);
                break;
            case PhotoMetadataField.Author:
                SetAscii(MetadataTiff.Artist, value);
                if (Exif?.Has(MetadataTiffIfd.Main, MetadataTiff.XpAuthor) == true)
                    Exif.SetXpString(MetadataTiffIfd.Main, MetadataTiff.XpAuthor, value);
                SetXmp(MetadataXmpNs.Dc, "creator", value, MetadataXmp.Shape.Sequence, "dc");
                SetXmp(MetadataXmpNs.Tiff, "Artist", value, MetadataXmp.Shape.Simple, "tiff");
                IptcSet(MetadataIptc.ByLine, value);
                SetTexts(value, ["Author"]);
                break;
            case PhotoMetadataField.Copyright:
                SetAscii(MetadataTiff.Copyright, value);
                SetXmp(MetadataXmpNs.Dc, "rights", value, MetadataXmp.Shape.LanguageAlternative, "dc");
                SetXmp(MetadataXmpNs.Tiff, "Copyright", value, MetadataXmp.Shape.Simple, "tiff");
                IptcSet(MetadataIptc.CopyrightNotice, value);
                SetTexts(value, ["Copyright"]);
                break;
            case PhotoMetadataField.Make:
                SetAscii(MetadataTiff.Make, value);
                SetXmp(MetadataXmpNs.Tiff, "Make", value, MetadataXmp.Shape.Simple, "tiff");
                break;
            case PhotoMetadataField.Model:
                SetAscii(MetadataTiff.Model, value);
                SetXmp(MetadataXmpNs.Tiff, "Model", value, MetadataXmp.Shape.Simple, "tiff");
                break;
            case PhotoMetadataField.Lens:
                SetAscii(MetadataTiff.LensModel, value, MetadataTiffIfd.Exif);
                SetXmp(MetadataXmpNs.Aux, "Lens", value, MetadataXmp.Shape.Simple, "aux");
                SetXmp(MetadataXmpNs.ExifEx, "LensModel", value, MetadataXmp.Shape.Simple, "exifEX");
                break;
            case PhotoMetadataField.Software:
                SetAscii(MetadataTiff.Software, value);
                SetXmp(MetadataXmpNs.Xmp, "CreatorTool", value, MetadataXmp.Shape.Simple, "xmp");
                SetXmp(MetadataXmpNs.Tiff, "Software", value, MetadataXmp.Shape.Simple, "tiff");
                IptcSet(MetadataIptc.OriginatingProgram, value);
                SetTexts(value, ["Software"]);
                break;
            case PhotoMetadataField.Date:
                if (MetadataValues.TryParseDate(value, out var date))
                    SetDate(date);
                break;
        }
    }

    public void Remove(PhotoMetadataField field)
    {
        switch (field)
        {
            case PhotoMetadataField.Description:
                RemoveExif(MetadataTiffIfd.Main, MetadataTiff.ImageDescription, MetadataTiff.XpComment);
                RemoveExif(MetadataTiffIfd.Exif, MetadataTiff.UserComment);
                RemoveXmp((MetadataXmpNs.Dc, "description"), (MetadataXmpNs.Tiff, "ImageDescription"));
                IptcRemove(MetadataIptc.CaptionAbstract);
                RemoveTexts(DescriptionKeys);
                RemoveComments();
                break;
            case PhotoMetadataField.Author:
                RemoveExif(MetadataTiffIfd.Main, MetadataTiff.Artist, MetadataTiff.XpAuthor);
                RemoveExif(MetadataTiffIfd.Exif, MetadataTiff.CameraOwnerName);
                RemoveXmp((MetadataXmpNs.Dc, "creator"), (MetadataXmpNs.Tiff, "Artist"), (MetadataXmpNs.Aux, "OwnerName"), (MetadataXmpNs.ExifEx, "CameraOwnerName"));
                IptcRemove(MetadataIptc.ByLine, MetadataIptc.ByLineTitle, MetadataIptc.Credit);
                RemoveTexts(["Author"]);
                break;
            case PhotoMetadataField.Copyright:
                RemoveExif(MetadataTiffIfd.Main, MetadataTiff.Copyright);
                RemoveXmp((MetadataXmpNs.Dc, "rights"), (MetadataXmpNs.Tiff, "Copyright"));
                IptcRemove(MetadataIptc.CopyrightNotice);
                RemoveTexts(["Copyright"]);
                break;
            case PhotoMetadataField.Make:
            case PhotoMetadataField.Model:
                // The maker note and the serial number name the camera as well as the Make and Model tags do.
                RemoveExif(MetadataTiffIfd.Main, field == PhotoMetadataField.Make ? MetadataTiff.Make : MetadataTiff.Model);
                if (field == PhotoMetadataField.Model)
                    RemoveExif(MetadataTiffIfd.Main, MetadataTiff.HostComputer);
                RemoveExif(MetadataTiffIfd.Exif, MetadataTiff.MakerNote, MetadataTiff.BodySerialNumber);
                RemoveXmp(field == PhotoMetadataField.Make ? (MetadataXmpNs.Tiff, "Make") : (MetadataXmpNs.Tiff, "Model"), (MetadataXmpNs.ExifEx, "BodySerialNumber"), (MetadataXmpNs.Aux, "SerialNumber"));
                break;
            case PhotoMetadataField.Lens:
                RemoveExif(MetadataTiffIfd.Exif, MetadataTiff.LensModel, MetadataTiff.LensMake, MetadataTiff.LensSpecification, MetadataTiff.LensSerialNumber);
                RemoveXmp((MetadataXmpNs.Aux, "Lens"), (MetadataXmpNs.Aux, "LensInfo"), (MetadataXmpNs.Aux, "LensID"), (MetadataXmpNs.Aux, "LensSerialNumber"), (MetadataXmpNs.ExifEx, "LensModel"),
                    (MetadataXmpNs.ExifEx, "LensMake"), (MetadataXmpNs.ExifEx, "LensSpecification"), (MetadataXmpNs.ExifEx, "LensSerialNumber"));
                break;
            case PhotoMetadataField.Software:
                RemoveExif(MetadataTiffIfd.Main, MetadataTiff.Software);
                RemoveXmp((MetadataXmpNs.Xmp, "CreatorTool"), (MetadataXmpNs.Tiff, "Software"));
                IptcRemove(MetadataIptc.OriginatingProgram);
                RemoveTexts(["Software"]);
                break;
            case PhotoMetadataField.Date:
                RemoveDate();
                break;
            case PhotoMetadataField.Latitude:
            case PhotoMetadataField.Longitude:
                RemoveLocation();
                break;
        }
    }

    /// <summary>Puts a position in the picture. Whatever else said where it was taken (altitude, place names) goes.</summary>
    public void SetLocation(double latitude, double longitude)
    {
        _gpsTouched = true;
        ExifForWriting()?.SetGps(latitude, longitude);
        if (XmpForWriting() is { } xmp)
        {
            var hadPosition = xmp.Has(MetadataXmpNs.Exif, "GPSLatitude") || xmp.Has(MetadataXmpNs.Exif, "GPSLongitude");
            xmp.RemoveMatching(IsXmpLocation);
            if (hadPosition || !CanStoreExif)
            {
                xmp.SetOrAdd(MetadataXmpNs.Exif, "GPSLatitude", MetadataXmp.GpsCoordinate(latitude, 'N', 'S'), MetadataXmp.Shape.Simple, "exif");
                xmp.SetOrAdd(MetadataXmpNs.Exif, "GPSLongitude", MetadataXmp.GpsCoordinate(longitude, 'E', 'W'), MetadataXmp.Shape.Simple, "exif");
            }
        }
        RemoveLocationNames();
    }

    /// <summary>Removes the position and every place name.</summary>
    public void RemoveLocation()
    {
        _gpsTouched = true;
        Exif?.RemoveGps();
        XmpForWriting()?.RemoveMatching(IsXmpLocation);
        RemoveLocationNames();
    }

    private void RemoveLocationNames()
    {
        if (IptcForWriting() is { } iptc)
            foreach (var number in LocationDatasets)
                iptc.Remove(number);
    }

    private void SetDate(DateTime date)
    {
        DateTouched = true;
        var exif = ExifForWriting();
        if (exif is not null)
        {
            exif.SetAscii(MetadataTiffIfd.Exif, MetadataTiff.DateTimeOriginal, MetadataValues.ForExif(date));
            if (exif.Has(MetadataTiffIfd.Exif, MetadataTiff.DateTimeDigitized))
                exif.SetAscii(MetadataTiffIfd.Exif, MetadataTiff.DateTimeDigitized, MetadataValues.ForExif(date));
            if (exif.Has(MetadataTiffIfd.Main, MetadataTiff.DateTime))
                exif.SetAscii(MetadataTiffIfd.Main, MetadataTiff.DateTime, MetadataValues.ForExif(date));
            // The fractions of a second and the zone belonged to the old time.
            RemoveExif(MetadataTiffIfd.Exif, MetadataTiff.SubSecTime, MetadataTiff.SubSecTimeOriginal, MetadataTiff.SubSecTimeDigitized,
                MetadataTiff.OffsetTime, MetadataTiff.OffsetTimeOriginal, MetadataTiff.OffsetTimeDigitized);
            RemoveGpsTimes();
        }
        var iso = MetadataValues.ForXmp(date);
        if (XmpForWriting() is { } xmp)
        {
            foreach (var (ns, name) in new[] { (MetadataXmpNs.Exif, "DateTimeOriginal"), (MetadataXmpNs.Exif, "DateTimeDigitized"), (MetadataXmpNs.Xmp, "CreateDate"), (MetadataXmpNs.Xmp, "ModifyDate"),
                         (MetadataXmpNs.Xmp, "MetadataDate"), (MetadataXmpNs.Photoshop, "DateCreated"), (MetadataXmpNs.Tiff, "DateTime") })
                xmp.SetText(ns, name, iso);
            xmp.RemoveMatching(name => name.Namespace == MetadataXmpNs.Exif && name.LocalName is "SubSecTimeOriginal" or "SubSecTimeDigitized" or "OffsetTimeOriginal" or "OffsetTimeDigitized" or "GPSTimeStamp");
            if (!CanStoreExif)
                xmp.SetOrAdd(MetadataXmpNs.Exif, "DateTimeOriginal", iso, MetadataXmp.Shape.Simple, "exif");
        }
        if (IptcForWriting() is { } iptc)
        {
            iptc.SetText(MetadataIptc.DateCreated, date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            iptc.SetText(MetadataIptc.TimeCreated, date.ToString("HHmmss", CultureInfo.InvariantCulture));
            iptc.SetText(MetadataIptc.DigitalCreationDate, date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            iptc.SetText(MetadataIptc.DigitalCreationTime, date.ToString("HHmmss", CultureInfo.InvariantCulture));
        }
        SetTexts(MetadataValues.ForXmp(date), ["Creation Time"]);
    }

    private void RemoveDate()
    {
        DateTouched = true;
        RemoveExif(MetadataTiffIfd.Exif, MetadataTiff.DateTimeOriginal, MetadataTiff.DateTimeDigitized, MetadataTiff.SubSecTime, MetadataTiff.SubSecTimeOriginal,
            MetadataTiff.SubSecTimeDigitized, MetadataTiff.OffsetTime, MetadataTiff.OffsetTimeOriginal, MetadataTiff.OffsetTimeDigitized);
        RemoveExif(MetadataTiffIfd.Main, MetadataTiff.DateTime);
        RemoveGpsTimes();
        if (XmpForWriting() is { } xmp)
            xmp.RemoveMatching(IsXmpDate);
        IptcRemove(MetadataIptc.DateCreated, MetadataIptc.TimeCreated, MetadataIptc.DigitalCreationDate, MetadataIptc.DigitalCreationTime);
        RemoveTexts(["Creation Time"]);
    }

    /// <summary>The GPS IFD keeps the time of the fix (and so of the photo) even when the position is gone.</summary>
    private void RemoveGpsTimes()
    {
        RemoveExif(MetadataTiffIfd.Gps, 0x0007, 0x001D);
        if (Exif is not null && !Exif.Entries(MetadataTiffIfd.Gps).Any(v => v.Tag != MetadataTiff.GpsVersionId) && Exif.HasGpsIfd)
            Exif.RemoveGps();
    }

    // ───────── The places ─────────

    private void SetAscii(ushort tag, string value, MetadataTiffIfd ifd = MetadataTiffIfd.Main) => ExifForWriting()?.SetAscii(ifd, tag, value);

    private void RemoveExif(MetadataTiffIfd ifd, params ushort[] tags)
    {
        if (Exif is null)
            return;
        foreach (var tag in tags)
            Exif.Remove(ifd, tag);
    }

    private void SetXmp(XNamespace ns, string name, string value, MetadataXmp.Shape shape, string prefix)
    {
        if (XmpForWriting() is not { } xmp)
            return;
        // Present in the file: update it. Absent: only a format without EXIF (GIF) needs it added, for these fields.
        if (!xmp.SetText(ns, name, value) && !CanStoreExif && IsAddedWhenAbsent(ns, name))
            xmp.SetOrAdd(ns, name, value, shape, prefix);
    }

    private static bool IsAddedWhenAbsent(XNamespace ns, string name) =>
        (ns == MetadataXmpNs.Dc) || (ns == MetadataXmpNs.Tiff && name is "Make" or "Model") || (ns == MetadataXmpNs.Aux && name == "Lens") || (ns == MetadataXmpNs.Xmp && name == "CreatorTool");

    private void RemoveXmp(params (XNamespace Ns, string Name)[] properties)
    {
        if (XmpForWriting() is not { } xmp)
            return;
        foreach (var (ns, name) in properties)
            xmp.Remove(ns, name);
    }

    private void IptcSet(int number, string value) => IptcForWriting()?.SetText(number, value);

    private void IptcRemove(params int[] numbers)
    {
        if (IptcForWriting() is not { } iptc)
            return;
        foreach (var number in numbers)
            iptc.Remove(number);
    }

    private void SetTexts(string value, string[] keys)
    {
        foreach (var text in Texts.Where(t => !t.Removed && t.Key is not null && keys.Contains(t.Key, StringComparer.OrdinalIgnoreCase)))
            text.Value = value;
    }

    private void RemoveTexts(string[] keys)
    {
        foreach (var text in Texts.Where(t => t.Key is not null && keys.Contains(t.Key, StringComparer.OrdinalIgnoreCase)))
            text.Removed = true;
    }

    /// <summary>A comment without a keyword (JPEG COM, GIF) is the picture's description: the first takes the text, the others go.</summary>
    private void SetComments(string value)
    {
        var comments = Texts.Where(t => !t.Removed && t.Key is null).ToList();
        for (var i = 0; i < comments.Count; i++)
        {
            if (i == 0)
                comments[i].Value = value;
            else
                comments[i].Removed = true;
        }
    }

    private void RemoveComments()
    {
        foreach (var text in Texts.Where(t => t.Key is null))
            text.Removed = true;
    }

    /// <summary>True when the position was touched (so a driver knows the GPS of a TIFF file may have changed).</summary>
    public bool LocationTouched => _gpsTouched;
}
