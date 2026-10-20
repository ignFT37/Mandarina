using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class MarkdownViewTests
{
    // Mandarina: the window is returned to be closed: one left open is drawn again at every tick of every later test.
    private static (MarkdownView View, Window Window) Show(string markdown, bool skipTitle = true)
    {
        var view = new MarkdownView { Markdown = markdown, SkipTitle = skipTitle };
        var window = new Window { Content = new ScrollViewer { Content = view }, Width = 800, Height = 600 };
        window.Show();
        return (view, window);
    }

    [AvaloniaFact]
    public void Third_party_notices_render_as_headings_tables_and_links()
    {
        TestServices.EnsureInitialized("en");
        using var reader = new StreamReader(AssetLoader.Open(new Uri("avares://Filee/Assets/THIRD-PARTY-NOTICES.md")));
        var markdown = reader.ReadToEnd();

        var (view, window) = Show(markdown);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Inlines?.Text ?? t.Text ?? "").ToList();
        Assert.DoesNotContain(texts, t => t.StartsWith('#') || t.Contains("|---"));  // no raw Markdown left
        Assert.DoesNotContain("Third-party notices", texts);                         // the title is the page's own
        Assert.Contains("Libraries (NuGet)", texts);
        Assert.True(view.GetVisualDescendants().OfType<Grid>().Count() >= 3);     // one per table
        var links = view.GetVisualDescendants().OfType<HyperlinkButton>().ToList();
        Assert.Contains(links, l => l.NavigateUri?.AbsoluteUri == "https://github.com/AvaloniaUI/Avalonia");
        window.Close();
    }

    [AvaloniaFact]
    public void Inline_code_emphasis_and_lists_keep_their_text()
    {
        TestServices.EnsureInitialized("en");

        var (view, window) = Show("# Title\n\nUse **bold** and `code`.\n\n- one\n- two\n", skipTitle: false);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Inlines?.Text ?? t.Text ?? "").ToList();
        Assert.Contains("Title", texts);
        Assert.Contains("Use bold and code.", texts);
        Assert.Contains("•", texts);
        Assert.Contains("two", texts);
        window.Close();
    }
}
