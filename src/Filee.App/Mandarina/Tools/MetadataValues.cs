// Mandarina: how the METADATA tool writes and reads the values it shows in its fields, whatever the culture and the file
// format: dates ("2024-05-17 10:20:30"), coordinates in decimal degrees and ISO 6709 positions.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Filee.App.Mandarina.Tools;

internal static partial class MetadataValues
{
    /// <summary>The way a date is shown and typed in the panel.</summary>
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly string[] UserDateFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy:MM:dd HH:mm:ss",
        "yyyy:MM:dd HH:mm", "yyyy:MM:dd", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd",
    ];

    /// <summary>Reads a date typed in the panel (several separators are accepted; a date alone means midnight).</summary>
    public static bool TryParseDate(string? text, out DateTime value)
    {
        value = default;
        return !string.IsNullOrWhiteSpace(text)
            && DateTime.TryParseExact(text.Trim(), UserDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            && value.Year >= 1000;
    }

    /// <summary>
    /// Reads a date as files write it: EXIF ("2024:05:17 10:20:30"), ISO 8601 with or without a zone and fractions, IPTC
    /// ("20240517"), or the RFC 1123 form of PNG. The clock time that is written is kept (no zone conversion).
    /// </summary>
    public static bool TryParseLooseDate(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (TryParseDate(text, out value))
            return true;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var offset) && offset.Year >= 1000)
        {
            // Files write the clock of the place: DateTimeOffset.DateTime keeps that clock whatever the offset.
            value = offset.DateTime;
            return true;
        }
        if (text.Length >= 8 && text[..8].All(char.IsAsciiDigit)
            && DateTime.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            return true;
        return false;
    }

    public static string ForUser(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string ForExif(DateTime value) => value.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ForXmp(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"2024-05-17T10:20:30.000000Z", the way FFmpeg and MP4 files state a creation time.</summary>
    public static string ForVideo(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss.000000", CultureInfo.InvariantCulture) + "Z";

    /// <summary>Reads a coordinate typed in decimal degrees ("40.4168" or "40,4168"); false when it is out of range.</summary>
    public static bool TryParseCoordinate(string? text, bool latitude, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
            return false;
        return Math.Abs(value) <= (latitude ? 90 : 180);
    }

    public static string ForUser(double coordinate) => coordinate.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>True for an ISO 6709 position as videos store it: "+40.4168-003.7038/", optionally with an altitude.</summary>
    public static bool IsIso6709(string? text) => !string.IsNullOrWhiteSpace(text) && Iso6709().IsMatch(text.Trim());

    [GeneratedRegex(@"^[+-]\d{2}(\.\d+)?[+-]\d{3}(\.\d+)?([+-]\d+(\.\d+)?)?(CRS\w+)?/?$")]
    private static partial Regex Iso6709();
}
