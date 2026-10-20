// Mandarina: the titles under the icons of the tools wheel are upper-case and small, and a long one used to be broken in the middle of
// a word ("RECORTA" / "R"). Every title of every tools wheel, in the four languages, has to fit its petal word by word.

using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.Mandarina;
using Filee.App.Services;
using Filee.Core.Localization;

namespace Filee.App.Tests;

public sealed class MandarinaWheelLabelsTests
{
    /// <summary>Every kind of selection that has a tools wheel (one file and several).</summary>
    private static readonly string[][] Selections =
    [
        ["a.jpg"], ["a.jpg", "b.png"], ["a.mp3"], ["a.mp3", "b.wav"], ["a.mp4"], ["a.mp4", "b.mkv"],
        ["a.pdf"], ["a.pdf", "b.pdf"], ["a.zip"], ["a.svg", "b.svg"], ["a.gif"],
    ];

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void No_title_on_a_tools_wheel_is_broken_in_the_middle_of_a_word(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var bold = new Typeface(SoftPalette.FontFamilyFor(language), FontStyle.Normal, FontWeight.SemiBold);
        // The default size of the wheel (Settings > Donut) and Filee's hole ratio.
        const double outer = 124;
        var inner = outer * 0.42;

        var checkedTitles = 0;
        foreach (var selection in Selections)
        {
            var entries = WheelCatalog.ToolsFor(selection, loc);
            var room = DonutMenu.GlassTitleRoom(DonutMenu.GlassPetalWidth(entries.Count, inner, outer));
            foreach (var entry in entries)
            {
                var title = loc[entry.TitleKey].ToUpper(CultureInfo.CurrentUICulture);
                var size = DonutMenu.GlassTitleSize(title, bold, room);
                var widest = DonutMenu.WidestWord(title, bold, size);
                Assert.True(widest <= room,
                    $"[{language}] '{title}' on a wheel of {entries.Count} petals: its longest word is {widest:0.0} px at {size:0.0} pt, the petal gives {room:0.0}");
                Assert.InRange(size, DonutMenu.GlassTitleMinimum, 9);
                checkedTitles++;
            }
        }
        Assert.True(checkedTitles > 30, "the wheels did not list their tools");
    }

    [AvaloniaFact]
    public void A_short_title_keeps_the_normal_size_and_a_long_one_shrinks()
    {
        TestServices.EnsureInitialized("es");
        var bold = new Typeface(SoftPalette.FontFamilyFor("es"), FontStyle.Normal, FontWeight.SemiBold);
        Assert.Equal(9, DonutMenu.GlassTitleSize("MUTE", bold, 60));
        var shrunk = DonutMenu.GlassTitleSize("TRANSCRIBIRRRRRRRRR", bold, 60);
        Assert.InRange(shrunk, DonutMenu.GlassTitleMinimum, 8.9);
        Assert.True(DonutMenu.WidestWord("TRANSCRIBIRRRRRRRRR", bold, shrunk) <= 60 || shrunk == DonutMenu.GlassTitleMinimum);
    }
}
