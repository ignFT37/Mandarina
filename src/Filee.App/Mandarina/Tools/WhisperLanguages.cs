// Mandarina: the languages Whisper understands, named in themselves ("Español", "Deutsch", "한국어") so anybody finds theirs
// whatever language the app is in. The first entry of the list the panel shows is "detect it" (code ""); it is added there.

using System.Globalization;

namespace Filee.App.Mandarina.Tools;

/// <summary>A language of the model: its code (what the filter takes) and its name in that language.</summary>
public sealed record WhisperLanguage(string Code, string Name);

public static class WhisperLanguages
{
    /// <summary>Whisper's language codes: ISO 639-1, and the few that only have three letters.</summary>
    private static readonly string[] Codes =
    [
        "af", "am", "ar", "as", "az", "ba", "be", "bg", "bn", "bo", "br", "bs", "ca", "cs", "cy", "da", "de", "el", "en", "es", "et", "eu",
        "fa", "fi", "fo", "fr", "gl", "gu", "ha", "haw", "he", "hi", "hr", "ht", "hu", "hy", "id", "is", "it", "ja", "jw", "ka", "kk", "km",
        "kn", "ko", "la", "lb", "ln", "lo", "lt", "lv", "mg", "mi", "mk", "ml", "mn", "mr", "ms", "mt", "my", "ne", "nl", "nn", "no", "oc",
        "pa", "pl", "ps", "pt", "ro", "ru", "sa", "sd", "si", "sk", "sl", "sn", "so", "sq", "sr", "su", "sv", "sw", "ta", "te", "tg", "th",
        "tk", "tl", "tr", "tt", "uk", "ur", "uz", "vi", "yi", "yo", "yue", "zh",
    ];

    /// <summary>Languages the system's culture data does not know by these codes.</summary>
    private static readonly Dictionary<string, string> Own = new()
    {
        ["ba"] = "Башҡортса", ["haw"] = "ʻŌlelo Hawaiʻi", ["jw"] = "Basa Jawa", ["la"] = "Latina", ["ln"] = "Lingála", ["mg"] = "Malagasy",
        ["mi"] = "Māori", ["sa"] = "संस्कृतम्", ["sn"] = "chiShona", ["tk"] = "Türkmen", ["yue"] = "粵語",
    };

    /// <summary>All the languages, by name.</summary>
    public static IReadOnlyList<WhisperLanguage> All { get; } =
        Codes.Select(code => new WhisperLanguage(code, NameOf(code))).OrderBy(language => language.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The name of the language of a code, in that language, capitalized.</summary>
    public static string NameOf(string code)
    {
        string name;
        try
        {
            name = Own.TryGetValue(code, out var own) ? own : CultureInfo.GetCultureInfo(code).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
        return name.Length == 0 ? code : char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..];
    }
}
