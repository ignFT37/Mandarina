// Mandarina: how panels write and read times ("1:05.30"), sizes and percentages, whatever the culture.

using System.Globalization;

namespace Filee.App.Mandarina.Panels;

public static class PanelFormat
{
    /// <summary>"0:05.30", "1:05.30", "1:02:03.40" (hundredths; the dot is always the separator).</summary>
    public static string Time(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var hundredths = (long)Math.Round(seconds * 100);
        var hours = hundredths / 360_000;
        var minutes = hundredths / 6000 % 60;
        var secs = hundredths / 100 % 60;
        var rest = hundredths % 100;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}.{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}.{rest:00}");
    }

    /// <summary>
    /// Reads "12", "12.5", "1:05", "1:05.3" or "1:02:03.4" (a comma may replace the dot) as seconds; false when it is
    /// not a time.
    /// </summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3)
            return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0 || !double.IsFinite(value))
                return false;
            total = total * 60 + value;
        }
        seconds = total;
        return true;
    }

    /// <summary>"2.2 MB", "850 KB" (decimal units, as Explorer shows them).</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1000 && unit < units.Length - 1)
        {
            size /= 1000;
            unit++;
        }
        return string.Format(CultureInfo.CurrentCulture, unit == 0 ? "{0:0} {1}" : "{0:0.#} {1}", size, units[unit]);
    }
}
