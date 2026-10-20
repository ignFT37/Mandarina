using System.Text.Json;
using System.Text.RegularExpressions;
using Filee.App.Services;

namespace Filee.App.Tests;

/// <summary>Keeps en / es / ja / ko / zh-CN / pl / hi / ru in sync so no language silently falls back to English.</summary>
public partial class LocalizationTests
{
    private static string I18nDirectory
    {
        get
        {
            var dir = new DirectoryInfo(TestPaths.Start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "Filee.App", "Assets", "i18n");
        }
    }

    private static Dictionary<string, string> Load(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(I18nDirectory, code + ".json")))!;

    public static TheoryData<string> Languages => new() { "es", "ko", "zh-CN" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_has_exactly_the_english_keys(string code)
    {
        var english = Load("en").Keys.ToHashSet();
        var other = Load(code).Keys.ToHashSet();

        Assert.Empty(english.Except(other));
        Assert.Empty(other.Except(english));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Format_placeholders_match_english(string code)
    {
        var english = Load("en");
        foreach (var (key, value) in Load(code))
            Assert.True(
                Placeholders(english[key]).SetEquals(Placeholders(value)),
                $"{code}:{key} placeholders differ from English");
    }

    [Fact]
    public void Every_key_used_in_xaml_exists()
    {
        var english = Load("en");
        var src = Path.GetFullPath(Path.Combine(I18nDirectory, "..", ".."));
        var missing = Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => DynamicResourceKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(k => k.Contains('.') && !k.StartsWith("Icon.", StringComparison.Ordinal))
            .Where(k => !english.ContainsKey(k))
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }

    // Mandarina: Filee's tests that Russian and Japanese are offered are gone: Mandarina offers en, es, ko and zh-CN only.
    /// <summary>Han characters look different in Japanese, Korean and Chinese fonts: the UI language's come first.</summary>
    [Theory]
    [InlineData("ja", "Noto Sans JP", "Yu Gothic UI")]
    [InlineData("ko", "Noto Sans KR", "Malgun Gothic")]
    [InlineData("zh-CN", "Noto Sans SC", "Microsoft YaHei")]
    [InlineData("en", "Noto Sans KR", "Malgun Gothic")]
    public void The_ui_languages_cjk_fonts_come_first(string language, string embedded, string system)
    {
        var families = ThemeService.FontFamilies(language).Split(", ").Select(f => f.Replace("fonts:Filee#", "")).ToList();
        string[] embeddedCjk = ["Noto Sans JP", "Noto Sans KR", "Noto Sans SC"];
        string[] systemCjk = ["Yu Gothic UI", "Malgun Gothic", "Microsoft YaHei"];

        Assert.Equal(embedded, families.First(embeddedCjk.Contains));
        Assert.Equal(system, families.First(systemCjk.Contains));
        Assert.True(embeddedCjk.Max(families.IndexOf) < systemCjk.Min(families.IndexOf), "embedded fonts before system ones");
    }

    private static HashSet<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Value).ToHashSet();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\{DynamicResource ([\w.]+)\}")]
    private static partial Regex DynamicResourceKey();
}
