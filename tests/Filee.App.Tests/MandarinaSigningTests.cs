// Mandarina: what a code-signing service (SignPath Foundation) and the user need to find. The programs say "Mandarina" where Task
// Manager and SmartScreen read it, with one product name and version everywhere; the README, the Release notes and the installer
// carry the "Code signing policy"; and what the installer asks about start-up and the context menu reaches the program.

using System.Diagnostics;
using Filee.App.Mandarina;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public sealed class MandarinaSigningTests
{
    private const string PrivacySentence = "This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it";

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

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepoRoot, .. path]));

    // ---- file properties ----

    /// <summary>Mandarina's own DLLs next to the tests (the app, the engines and the platform layers; the command line is checked when packaging).</summary>
    private static IEnumerable<string> OwnBinaries() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory)
            .Where(f => Path.GetFileName(f) is { } n && n.StartsWith("Filee", StringComparison.OrdinalIgnoreCase)
                && !n.StartsWith("Filee.App.Tests", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(n) is ".dll" or ".exe");

    [Fact]
    public void Our_programs_and_libraries_are_named_Mandarina_with_one_version()
    {
        var files = OwnBinaries().ToList();
        Assert.Contains(files, f => Path.GetFileName(f) == "Filee.dll");
        Assert.Contains(files, f => Path.GetFileName(f) == "Filee.Core.dll");
        Assert.Contains(files, f => Path.GetFileName(f) == "Filee.Engines.dll");

        foreach (var file in files)
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            Assert.True(info.ProductName == "Mandarina", $"{Path.GetFileName(file)}: ProductName is '{info.ProductName}'");
            Assert.True(info.FileDescription?.StartsWith("Mandarina", StringComparison.Ordinal) == true,
                $"{Path.GetFileName(file)}: FileDescription is '{info.FileDescription}'");
        }
        var versions = files.Select(f => FileVersionInfo.GetVersionInfo(f).ProductVersion).Distinct().ToList();
        Assert.True(versions.Count == 1, "ProductVersion differs between files: " + string.Join(", ", versions));
    }

    [Fact]
    public void The_command_line_names_itself_Mandarina_too()
    {
        // filee-cli.exe is not next to the tests; its project says what the version resource will.
        var project = Read("src", "Filee.Cli", "Filee.Cli.csproj");
        Assert.Contains("<AssemblyTitle>Mandarina", project);
        Assert.Contains("<AssemblyTitle>Mandarina</AssemblyTitle>", Read("Directory.Build.props"));
    }

    // ---- the policy text ----

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.es.md")]
    public void The_readmes_have_the_code_signing_policy(string readme)
    {
        var text = Read(readme);
        Assert.Contains("## Code signing policy", text);
        Assert.Contains("https://signpath.org", text);
        Assert.Contains("https://about.signpath.io", text); // the attribution line, ready to switch on
        Assert.Contains("@ignFT37", text);
        Assert.Contains(PrivacySentence, text);
        Assert.Contains("engines.json", text);
    }

    [Fact]
    public void Release_notes_carry_the_policy()
    {
        var workflow = Read(".github", "workflows", "release.yml");
        Assert.Contains("Code signing policy", workflow);
        Assert.Contains("SignPath Foundation", workflow);
        Assert.Contains("@ignFT37", workflow);
        Assert.Contains(PrivacySentence, System.Text.RegularExpressions.Regex.Replace(workflow, @"\s+", " "));
    }

    // ---- what the installer tells the user ----

    [Fact]
    public void The_installer_offers_start_up_and_the_menu_ticked_by_default()
    {
        var script = Read("installer", "Mandarina.iss");
        foreach (var task in new[] { "startup", "shellmenu" })
        {
            var line = script.Split('\n').Single(l => l.StartsWith($"Name: \"{task}\"", StringComparison.Ordinal));
            Assert.DoesNotContain("unchecked", line);
            Assert.Contains("GroupDescription", line);
        }
        // Spanish and English texts that point to Settings > General.
        Assert.Contains("en.IntegrationGroup=", script);
        Assert.Contains("es.IntegrationGroup=", script);
        Assert.Contains("Ajustes > General", script);
        Assert.Contains("Settings > General", script);
        // The file the program reads, and the uninstaller removes it if the program never did.
        Assert.Contains(InstallerChoices.FileName, script);
        Assert.Contains("\"startWithWindows\":", script);
        Assert.Contains("\"contextMenu\":", script);
        Assert.Contains($"Name: \"{{app}}\\{InstallerChoices.FileName}\"", script);
        // The uninstaller still cleans up what the program registered.
        Assert.Contains("--uninstall-cleanup", script);
    }

    // ---- first start applies what was ticked ----

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mandarina-setup-opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Unticked_options_are_applied_once_and_the_file_is_deleted()
    {
        var folder = Folder();
        try
        {
            File.WriteAllText(Path.Combine(folder, InstallerChoices.FileName), "{\"startWithWindows\":false,\"contextMenu\":false}");
            var settings = new AppSettings();
            Assert.True(settings.StartWithSystem && settings.ContextMenuEnabled); // the defaults

            Assert.True(InstallerChoices.Consume(folder, settings));
            Assert.False(settings.StartWithSystem);
            Assert.False(settings.ContextMenuEnabled);
            Assert.False(File.Exists(Path.Combine(folder, InstallerChoices.FileName)));

            // A later start has nothing to apply: what the user changes in Settings stays.
            settings.StartWithSystem = true;
            Assert.False(InstallerChoices.Consume(folder, settings));
            Assert.True(settings.StartWithSystem);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Ticked_options_change_nothing_and_a_missing_or_broken_file_is_ignored()
    {
        var folder = Folder();
        try
        {
            var settings = new AppSettings();
            Assert.False(InstallerChoices.Consume(folder, settings)); // portable copy: no file
            Assert.False(InstallerChoices.Consume(null, settings));

            File.WriteAllText(Path.Combine(folder, InstallerChoices.FileName), "{\"startWithWindows\":true,\"contextMenu\":true}");
            Assert.False(InstallerChoices.Consume(folder, settings));
            Assert.True(settings.StartWithSystem && settings.ContextMenuEnabled);

            File.WriteAllText(Path.Combine(folder, InstallerChoices.FileName), "not json");
            Assert.Null(InstallerChoices.Read(folder));
            Assert.False(InstallerChoices.Consume(folder, settings));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Only_the_option_that_was_written_is_applied()
    {
        var folder = Folder();
        try
        {
            File.WriteAllText(Path.Combine(folder, InstallerChoices.FileName), "{\"contextMenu\":false}");
            var settings = new AppSettings();
            Assert.True(InstallerChoices.Consume(folder, settings));
            Assert.True(settings.StartWithSystem);
            Assert.False(settings.ContextMenuEnabled);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
