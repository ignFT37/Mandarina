// Mandarina: guards for what the repository says about itself. The interface calls the app Mandarina in every language (only the
// support and sponsor texts talk about Filee, the project it is built on), and no file carries a path or a name that belongs to
// one machine. A merge from upstream that brings Filee's wording back, or a script written on one PC, fails here.

using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;

namespace Filee.App.Tests;

public sealed class MandarinaBrandingTests
{
    /// <summary>User names the tests and Filee's docs invent for fake paths; a real account called like this is not checked.</summary>
    private static readonly string[] FakeUsers = ["Me", "Test", "User", "Kim", "Public", "Default", "Admin", "Administrator"];

    /// <summary>The texts that are about Filee itself (its licence, its sponsors), so they keep its name in every language.</summary>
    private static readonly HashSet<string> AboutFileeKeys =
    [
        "about.license", "about.support_title", "about.support_text", "sponsor.title", "sponsor.lead", "sponsor.star",
        "feedback.notice_title", "feedback.notice_text",
    ];

    public static TheoryData<string> Languages => new() { "en", "es", "ko", "zh-CN" };

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(TestPaths.Start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void The_interface_calls_the_app_Mandarina(string code)
    {
        var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(RepoRoot, "src", "Filee.App", "Assets", "i18n", code + ".json")))!;

        Assert.Equal("Mandarina", texts["app.name"]);
        var mentionFilee = texts.Where(t => t.Value.Contains("Filee", StringComparison.Ordinal)).Select(t => t.Key).ToHashSet();
        Assert.Empty(mentionFilee.Except(AboutFileeKeys));
    }

    /// <summary>The update controls look at Filee's releases: installing one would replace Mandarina with Filee.</summary>
    [AvaloniaFact]
    public void The_about_page_offers_no_updates_from_Filee()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>());
        vm.Navigate("about");
        var main = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        try
        {
            main.Show();
            for (var i = 0; i < 40; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            var about = main.GetVisualDescendants().OfType<AboutPage>().Single();
            var visible = about.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.DoesNotContain(visible.OfType<Button>(), b => b.Content as string == loc["general.check_now"]);
            Assert.DoesNotContain(visible.OfType<ToggleSwitch>(), t => t.Content as string == loc["general.check_updates"]);
            Assert.Contains(visible.OfType<TextBlock>(), t => t.Text == "Mandarina"); // the page itself did render
        }
        finally
        {
            main.CloseForGood();
        }
    }

    /// <summary>
    /// Whoever runs the tests must not find their own profile path in the repository: it would be a script or a test written on
    /// one machine and committed with its paths. (The fake users the tests invent for path handling, "Me" or "Test", are skipped.)
    /// </summary>
    [Fact]
    public void No_file_carries_the_profile_path_of_whoever_runs_the_tests()
    {
        var user = Environment.UserName;
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(user) && !FakeUsers.Contains(user, StringComparer.OrdinalIgnoreCase),
            "this user name is one the tests use for fake paths");
        var ownPath = new Regex(@"[A-Za-z]:[\\/]+Users[\\/]+" + Regex.Escape(user) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase);

        var offenders = new List<string>();
        foreach (var file in PublishedTextFiles())
            foreach (Match match in ownPath.Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}: {match.Value}");

        Assert.Empty(offenders);
    }

    /// <summary>Source, tests, scripts and docs, without build output, engines or the maintainer's private <c>local/</c> folder.</summary>
    private static IEnumerable<string> PublishedTextFiles()
    {
        var skipped = new[] { "bin", "obj", ".git", "local", "engines", "node_modules", "Releases", "media" };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".axaml", ".csproj", ".props", ".slnx", ".json", ".md", ".ps1", ".py", ".yml", ".iss", ".cmd", ".html",
        };
        var pending = new Stack<string>([RepoRoot]);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if (!skipped.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);
            foreach (var file in Directory.EnumerateFiles(folder))
                if (extensions.Contains(Path.GetExtension(file)) && !file.EndsWith(".Designer.cs", StringComparison.Ordinal)
                    && Path.GetFileName(file) != nameof(MandarinaBrandingTests) + ".cs")
                    yield return file;
        }
    }
}
