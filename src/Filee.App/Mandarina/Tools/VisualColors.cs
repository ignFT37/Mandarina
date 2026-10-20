// Mandarina: the colours of the VISUALIZE tool: the two defaults, the swatches offered for the wave and for the background, and
// how a "#RRGGBB" is read and handed to FFmpeg.

using Avalonia.Media;

namespace Filee.App.Mandarina.Tools;

public static class VisualColors
{
    /// <summary>The wave: Mandarina's orange.</summary>
    internal const string DefaultWave = "#FF5200";

    /// <summary>The background: a dark plum, the ink of the panels.</summary>
    internal const string DefaultBackground = "#1B1620";

    /// <summary>The swatches for the wave: the orange, white, then warm to cool hues, and the ink for a light background.</summary>
    public static readonly IReadOnlyList<string> WavePalette =
        ["#FF5200", "#FFFFFF", "#FFD54A", "#FF8FB1", "#A98CEF", "#3B82F6", "#2EC4B6", "#4CC38A", "#1B1620"];

    /// <summary>The swatches for the background: darks first, then light ones and the orange.</summary>
    public static readonly IReadOnlyList<string> BackgroundPalette =
        ["#1B1620", "#000000", "#1E2A4A", "#12352F", "#3B1F4D", "#F2EDE6", "#FFFFFF", "#FFD54A", "#FF5200"];

    /// <summary>Reads "#RRGGBB" or "#RGB" (the # is optional).</summary>
    internal static bool TryParse(string? text, out Color color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 3 or 6 } || !hex.All(Uri.IsHexDigit))
            return false;
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(digit => new string(digit, 2)));
        color = Color.FromRgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
        return true;
    }

    /// <summary>"#RRGGBB", upper case.</summary>
    internal static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>The colour of <paramref name="text"/> as "#RRGGBB", or <paramref name="fallback"/> when it is not a colour.</summary>
    internal static string Normalize(string? text, string fallback) => TryParse(text, out var color) ? ToHex(color) : fallback;

    /// <summary>The colour the way FFmpeg's filters read it: "0xRRGGBB".</summary>
    internal static string ForFilter(string? text, string fallback) => "0x" + Normalize(text, fallback)[1..];
}
