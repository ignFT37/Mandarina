using Filee.Platform.Windows;

namespace Filee.Platform.Tests;

/// <summary>
/// Mandarina: what it registers for Explorer carries its own names and never takes away what a Filee installed next to it
/// registered (the Send To shortcut and the Windows 11 menu package are both called "Filee" there).
/// </summary>
public sealed class MandarinaOwnNamesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("mandarina-own-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private string Shortcut(string name, string target)
    {
        var path = Path.Combine(_folder, name);
        ShellLinks.Create(path, target);
        return path;
    }

    [Fact]
    public void The_old_send_to_shortcut_is_removed_only_when_it_points_at_this_program()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows shortcuts need the Windows shell.");
            return;
        }
        var mine = Path.Combine(_folder, "Mandarina.exe");
        var filees = Path.Combine(_folder, "Filee.exe");
        File.WriteAllText(mine, "x");
        File.WriteAllText(filees, "x");

        var legacy = Shortcut("Filee.lnk", mine);
        WindowsPlatformServices.RemoveLegacySendToShortcut(_folder, "", null, mine);
        Assert.False(File.Exists(legacy));

        legacy = Shortcut("Filee.lnk", filees);
        WindowsPlatformServices.RemoveLegacySendToShortcut(_folder, "", null, mine);
        Assert.True(File.Exists(legacy), "A shortcut that belongs to Filee stays.");
    }

    [Fact]
    public void A_missing_old_send_to_shortcut_is_not_an_error()
    {
        WindowsPlatformServices.RemoveLegacySendToShortcut(_folder, Path.Combine(_folder, "Mandarina.exe"));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void The_explorer_menu_package_is_removed_only_when_it_points_at_this_programs_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Explorer menu package is Windows 11.");
            return;
        }
        var program = Path.Combine(_folder, "Mandarina");
        var theirs = Path.Combine(_folder, "Filee");
        var version = new Version(1, 0, 0, 0);

        Assert.True(ExplorerMenuRegistration.IsOwnedBy(new("p", version, program), program + Path.DirectorySeparatorChar));
        Assert.False(ExplorerMenuRegistration.IsOwnedBy(new("p", version, theirs), program));
        Assert.False(ExplorerMenuRegistration.IsOwnedBy(new("p", version, null), program));
    }
}
