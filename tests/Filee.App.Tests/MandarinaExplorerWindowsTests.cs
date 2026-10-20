// Mandarina: Move ends by showing its destination, and a File Explorer window that already shows exactly that folder is brought to the front
// instead of one more piling up. Which window shows the folder is a pure function, checked here with lists; the COM part is only read
// (whatever windows this PC has open). Opening real Explorer windows, minimizing one, running Move on it and counting the windows with
// Shell.Application is opt-in (MANDARINA_UI_TESTS=1): it shows windows on the screen for a moment and closes only the ones it opened.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Filee.App.Mandarina;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

[SupportedOSPlatform("windows")]
public sealed class MandarinaExplorerWindowsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-explorer").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static bool UiTests => Environment.GetEnvironmentVariable("MANDARINA_UI_TESTS") == "1";

    // ───────── Which spelling is which folder ─────────

    [Theory]
    [InlineData(@"C:\Users\Me\Docs", @"c:\users\me\docs\")]
    [InlineData(@"C:\Users\Me\Docs", "file:///C:/Users/Me/Docs")]
    [InlineData(@"C:\Users\Me\Docs", "FILE:///c:/users/me/docs/")]
    [InlineData(@"C:\Users\Me\Docs", "file:///C:/Users/Me/D%6Fcs")]
    [InlineData(@"C:\Users\Me\My Docs", "file:///C:/Users/Me/My%20Docs")]
    [InlineData(@"C:\Users\Me\Docs", "file:/C:/Users/Me/Docs")]
    [InlineData(@"C:\Users\Me\Docs", "C:/Users/Me/./Docs")]
    [InlineData(@"C:\Users\Me\Docs", @"C:\Users\Me\x\..\Docs")]
    [InlineData(@"C:\Users\Me\Docs", @"C:\Users\\Me\Docs")]
    [InlineData(@"C:\Users\Me\Docs", @"\\?\C:\Users\Me\Docs")]
    [InlineData(@"C:\Users\Me\Docs ", @"C:\Users\Me\Docs")]
    [InlineData(@"\\srv\share\x", "file://srv/share/x")]
    [InlineData(@"\\srv\share\x", "file:////srv/share/x")]
    [InlineData(@"\\srv\share\x y", "file://SRV/share/x%20y/")]
    [InlineData(@"\\srv\share\x", @"\\?\UNC\srv\share\x")]
    [InlineData(@"\\srv\share", @"\\srv\share\")]
    [InlineData(@"D:\", "file:///D:/")]
    [InlineData(@"D:\", "D:")]
    [InlineData(@"D:\", "d:\\")]
    [InlineData("C:\\Users\\Ñandú\\Mañana", "file:///C:/Users/%C3%91and%C3%BA/Ma%C3%B1ana")]
    [InlineData(@"C:\a#b", "file:///C:/a%23b")]
    [InlineData(@"C:\100%", "file:///C:/100%25")]
    [InlineData("C:\\ünï cödé", "file:///C:/%C3%BCn%C3%AF%20c%C3%B6d%C3%A9")]
    public void Every_spelling_of_a_folder_is_the_same_folder(string one, string other)
    {
        Assert.NotNull(ExplorerFolders.Key(one));
        Assert.Equal(ExplorerFolders.Key(one), ExplorerFolders.Key(other), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explorer_writes_the_letters_of_its_code_page_as_single_bytes_and_that_is_read_too()
    {
        // Measured on Windows 11: the address of "...\A pestaña" is "file:///C:/…/A%20pesta%F1a", not "%C3%B1" as the URI standard says.
        if (!OperatingSystem.IsWindows() || NativeWindows.GetACP() != 1252)
        {
            Assert.Skip("This check is for a Windows whose ANSI code page is 1252.");
            return;
        }

        Assert.Equal(ExplorerFolders.Key(@"C:\Users\Me\A pestaña"), ExplorerFolders.Key("file:///C:/Users/Me/A%20pesta%F1a"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExplorerFolders.Key(@"C:\Users\Ñandú\Mañana"), ExplorerFolders.Key("file:///C:/Users/%D1and%FA/Ma%F1ana/"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExplorerFolders.Key(@"C:\Users\Ñandú\Mañana"), ExplorerFolders.Key("file:///C:/Users/%C3%91and%C3%BA/Ma%F1ana"), StringComparer.OrdinalIgnoreCase); // both in one address
        Assert.Equal((nint)3, ExplorerFolders.Find(@"C:\Users\Me\A pestaña", [new ExplorerWindow(3, "file:///C:/Users/Me/A%20pesta%F1a", null)])!.Value.Window);
    }

    [Theory]
    [InlineData("100%", "100%")]                           // a % that starts no escape stays as it is
    [InlineData("%", "%")]
    [InlineData("%4", "%4")]
    [InlineData("%zz", "%zz")]
    [InlineData("a%41b", "aAb")]
    [InlineData("%C3%B1%20%E2%82%AC", "ñ €")]              // UTF-8, as the URI standard says
    public void Escapes_are_decoded_and_what_is_not_an_escape_is_left_alone(string address, string expected) =>
        Assert.Equal(ExplorerFolders.Key(@"C:\x\" + expected), ExplorerFolders.Key("file:///C:/x/" + address), StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a")]                       // the parent is not the folder
    [InlineData(@"C:\a\b", @"C:\a\b\c")]                   // nor a child
    [InlineData(@"C:\a\b", @"C:\a\bc")]                    // nor a folder whose name starts the same
    [InlineData(@"C:\a\b", @"D:\a\b")]                     // nor the same path on another drive
    [InlineData(@"C:\a\b", "file:///C:/a/b%20c")]
    [InlineData(@"C:\a", @"\\a")]                          // nor a network name
    [InlineData(@"\\srv\share\x", @"\\srv\other\x")]
    [InlineData(@"C:\", "file:///::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")] // This PC is not drive C
    public void Different_folders_stay_different(string one, string other) =>
        Assert.NotEqual(ExplorerFolders.Key(one), ExplorerFolders.Key(other), StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_no_folder(string? nothing)
    {
        Assert.Null(ExplorerFolders.Key(nothing));
        Assert.Null(ExplorerFolders.Find(@"C:\a", [new ExplorerWindow(1, nothing, nothing)]));
        Assert.Null(ExplorerFolders.Find(nothing!, [new ExplorerWindow(1, "file:///C:/a", @"C:\a")]));
    }

    // ───────── Which window shows the folder ─────────

    private static ExplorerWindow Window(nint handle, string path, bool? active = true, string? name = null) =>
        new(handle, "file:///" + path.Replace('\\', '/').Replace(" ", "%20"), path, name ?? System.IO.Path.GetFileName(path), active);

    [Fact]
    public void The_window_that_shows_the_folder_is_found_by_its_path_or_by_its_address()
    {
        var windows = new[] { Window(1, @"C:\a"), Window(2, @"C:\Users\Me\My Docs"), Window(3, @"C:\b") };

        Assert.Equal((nint)2, ExplorerFolders.Find(@"c:\users\me\my docs\", windows)!.Value.Window);
        Assert.Equal((nint)3, ExplorerFolders.Find(@"C:\b", windows)!.Value.Window);
        Assert.Null(ExplorerFolders.Find(@"C:\c", windows));
        Assert.Null(ExplorerFolders.Find(@"C:\Users\Me", windows));
        Assert.Null(ExplorerFolders.Find(@"C:\a", []));

        // Explorer says one of the two (a library, a drive that is gone): the other still does.
        Assert.Equal((nint)4, ExplorerFolders.Find(@"C:\a", [new ExplorerWindow(4, "file:///C:/a", null)])!.Value.Window);
        Assert.Equal((nint)5, ExplorerFolders.Find(@"C:\a", [new ExplorerWindow(5, null, @"C:\a")])!.Value.Window);
        Assert.Null(ExplorerFolders.Find(@"C:\a", [new ExplorerWindow(6, null, null)]));
    }

    [Fact]
    public void A_network_folder_is_found_too()
    {
        var windows = new[]
        {
            new ExplorerWindow(1, "file://srv/share/Fotos%20de%20verano", @"\\srv\share\Fotos de verano"),
            new ExplorerWindow(2, "file:///::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"), // Network
        };

        Assert.Equal((nint)1, ExplorerFolders.Find(@"\\SRV\Share\Fotos de verano", windows)!.Value.Window);
        Assert.Equal((nint)1, ExplorerFolders.Find(@"\\srv\share\Fotos de verano\", windows)!.Value.Window);
        Assert.Null(ExplorerFolders.Find(@"\\srv\share", windows));
    }

    [Fact]
    public void With_two_windows_on_the_folder_the_one_in_front_wins()
    {
        // The list comes front to back: after a Move that opened a window every time, there may be several.
        var windows = new[] { Window(7, @"C:\x"), Window(3, @"C:\x"), Window(9, @"C:\y") };

        Assert.Equal((nint)7, ExplorerFolders.Find(@"C:\x", windows)!.Value.Window);
    }

    [Fact]
    public void A_tab_behind_another_one_does_not_show_the_folder()
    {
        var behind = new[] { Window(1, @"C:\a", active: false), Window(1, @"C:\b", active: true) };

        Assert.Null(ExplorerFolders.Find(@"C:\a", behind));                                  // a new window will show it
        Assert.Equal((nint)1, ExplorerFolders.Find(@"C:\b", behind)!.Value.Window);

        // A tab that may be on show counts only when no window is sure about it.
        var maybe = new[] { Window(1, @"C:\a", active: null), Window(2, @"C:\a", active: true) };
        Assert.Equal((nint)2, ExplorerFolders.Find(@"C:\a", maybe)!.Value.Window);
        Assert.Equal((nint)1, ExplorerFolders.Find(@"C:\a", [maybe[0]])!.Value.Window);
    }

    // ───────── Which tab of a window is on show ─────────

    [Fact]
    public void A_window_with_one_entry_has_no_tab_behind_it()
    {
        var marked = ExplorerFolders.MarkActiveTabs([Window(1, @"C:\a", active: null), Window(2, @"C:\b", active: null)], _ => null);

        Assert.All(marked, window => Assert.True(window.IsActiveTab));
    }

    [Fact]
    public void The_tab_on_show_is_the_one_the_window_title_names()
    {
        // The titles as Windows 11 writes them (measured, Spanish): "<tab on show> y N pestañas más - Explorador de archivos".
        ExplorerWindow[] tabs =
        [
            Window(1, @"C:\a", null, "A pestaña"), Window(1, @"C:\b", null, "B pestaña"), Window(1, @"C:\c", null, "C pestaña"),
            Window(2, @"C:\d", null, "Delta"), Window(2, @"C:\e", null, "Épsilon"),
            Window(3, @"C:\f", null, "Solo"),
        ];
        var titles = new Dictionary<nint, string?>
        {
            [1] = "B pestaña y 2 más pestañas - Explorador de archivos",
            [2] = "Delta y 1 pestaña más - Explorador de archivos",
            [3] = "Solo - Explorador de archivos",
        };

        var marked = ExplorerFolders.MarkActiveTabs(tabs, window => titles[window]);

        Assert.Equal([false, true, false, true, false, true], marked.Select(window => window.IsActiveTab));
        Assert.Equal(tabs.Select(tab => tab.Path), marked.Select(window => window.Path)); // same entries, same order
    }

    [Theory]
    [InlineData("A pestaña - Explorador de archivos")]                      // a window with one tab
    [InlineData("A pestaña y 1 pestaña más - Explorador de archivos")]      // with more
    [InlineData("A pestaña and 3 more tabs - File Explorer")]               // whatever the language says after the name
    [InlineData("A pestaña和另外 2 个标签页 - 文件资源管理器")]
    [InlineData("a pestaña")]
    public void The_name_at_the_start_of_the_title_is_what_counts_not_what_follows(string title)
    {
        ExplorerWindow[] tabs = [Window(1, @"C:\a", null, "A pestaña"), Window(1, @"C:\b", null, "B pestaña")];

        var marked = ExplorerFolders.MarkActiveTabs(tabs, _ => title);

        Assert.Equal([true, false], marked.Select(window => window.IsActiveTab));
    }

    [Fact]
    public void A_tab_whose_name_starts_another_ones_does_not_take_its_place()
    {
        ExplorerWindow[] tabs = [Window(1, @"C:\b", null, "B"), Window(1, @"C:\bp", null, "B pestaña")];

        Assert.Equal([false, true], ExplorerFolders.MarkActiveTabs(tabs, _ => "B pestaña y 1 pestaña más - Explorador de archivos").Select(window => window.IsActiveTab));
        Assert.Equal([true, false], ExplorerFolders.MarkActiveTabs(tabs, _ => "B y 1 pestaña más - Explorador de archivos").Select(window => window.IsActiveTab));
    }

    [Fact]
    public void Tabs_with_the_same_name_cannot_be_told_apart()
    {
        ExplorerWindow[] tabs = [Window(1, @"C:\x\Docs", null), Window(1, @"D:\y\Docs", null), Window(1, @"C:\other", null)];

        var marked = ExplorerFolders.MarkActiveTabs(tabs, _ => "Docs y 2 pestañas más - Explorador de archivos");

        Assert.Equal([null, null, false], marked.Select(window => window.IsActiveTab));
    }

    [Fact]
    public void A_title_bar_with_the_full_path_names_the_tab_by_its_path()
    {
        ExplorerWindow[] tabs = [Window(1, @"C:\x\Docs", null, "Docs"), Window(1, @"D:\y\Docs", null, "Docs")];

        var marked = ExplorerFolders.MarkActiveTabs(tabs, _ => @"D:\y\Docs y 1 pestaña más - Explorador de archivos");

        Assert.Equal([false, true], marked.Select(window => window.IsActiveTab));
    }

    [Fact]
    public void The_home_tab_a_new_tab_shows_has_no_folder_name_to_confuse_with()
    {
        // A new tab shows Home: Windows() lists it with no address and a path like "::{F874310E-…}" (measured).
        ExplorerWindow[] tabs =
        [
            new ExplorerWindow(1, "file:///C:/x/A%20pesta%F1a", @"C:\x\A pestaña", "A pestaña", null),
            new ExplorerWindow(1, "", "::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", "Inicio", null),
        ];

        Assert.Equal([true, false], ExplorerFolders.MarkActiveTabs(tabs, _ => "A pestaña y 1 pestaña más - Explorador de archivos").Select(window => window.IsActiveTab));
        Assert.Equal([false, true], ExplorerFolders.MarkActiveTabs(tabs, _ => "Inicio y 1 pestaña más - Explorador de archivos").Select(window => window.IsActiveTab));
        Assert.Null(ExplorerFolders.Find(@"C:\x", ExplorerFolders.MarkActiveTabs(tabs, _ => "Inicio y 1 pestaña más - Explorador de archivos")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Something else entirely")]
    public void A_title_that_names_no_tab_leaves_them_all_open_to_doubt(string? title)
    {
        ExplorerWindow[] tabs = [Window(1, @"C:\a", null), Window(1, @"C:\b", null)];

        var marked = ExplorerFolders.MarkActiveTabs(tabs, _ => title);

        Assert.All(marked, window => Assert.Null(window.IsActiveTab));
    }

    // ───────── Bringing it forward ─────────

    private sealed class FakeWindows(params ExplorerWindow[] windows) : IExplorerWindows
    {
        public int Listed { get; private set; }
        public Exception? Failure { get; init; }

        public IReadOnlyList<ExplorerWindow> List()
        {
            Listed++;
            return Failure is null ? windows : throw Failure;
        }
    }

    private sealed class FakeFront : IWindowFront
    {
        public List<nint> Brought { get; } = [];
        public void BringToFront(nint window) => Brought.Add(window);
    }

    [Fact]
    public void The_window_that_shows_the_folder_is_brought_forward_and_nothing_else()
    {
        var front = new FakeFront();

        Assert.True(ExplorerFolders.TryBringToFront(@"C:\b", new FakeWindows(Window(1, @"C:\a"), Window(2, @"C:\b")), front));

        Assert.Equal([(nint)2], front.Brought);
    }

    [Fact]
    public void Without_a_window_on_the_folder_nothing_is_brought_forward_and_the_caller_opens_one()
    {
        var front = new FakeFront();

        Assert.False(ExplorerFolders.TryBringToFront(@"C:\c", new FakeWindows(Window(1, @"C:\a")), front));
        Assert.False(ExplorerFolders.TryBringToFront(@"C:\c", new FakeWindows(), front));

        Assert.Empty(front.Brought);
    }

    [Theory]
    [InlineData(typeof(COMException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(InvalidOperationException))]
    public void An_Explorer_that_cannot_be_asked_means_open_the_folder_as_before(Type failure)
    {
        var front = new FakeFront();
        var windows = new FakeWindows(Window(1, @"C:\a")) { Failure = (Exception)Activator.CreateInstance(failure)! };

        Assert.False(ExplorerFolders.TryBringToFront(@"C:\a", windows, front));

        Assert.Equal(1, windows.Listed);
        Assert.Empty(front.Brought);
    }

    // ───────── The real Explorer, read only ─────────

    [Fact]
    public void The_real_list_of_windows_can_be_read_and_holds_only_folder_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("File Explorer is Windows'.");
            return;
        }

        var windows = OnSta(() => new ShellExplorerWindows().List());

        // Whatever is open on this PC (maybe nothing): each entry belongs to a window, and the tabs are all marked.
        Assert.All(windows, window => Assert.NotEqual((nint)0, window.Window));
        Assert.All(windows.Where(window => windows.Count(other => other.Window == window.Window) == 1), window => Assert.True(window.IsActiveTab));
        // One entry per window on show: no two entries of a window are both the tab on show.
        Assert.All(windows.GroupBy(window => window.Window), window => Assert.True(window.Count(tab => tab.IsActiveTab == true) <= 1));
    }

    [Fact]
    public void A_folder_nobody_shows_is_not_found_in_the_real_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("File Explorer is Windows'.");
            return;
        }

        // A folder that exists only for this test cannot be on show anywhere: the lookup says so without opening or touching a thing.
        var nowhere = Directory.CreateDirectory(System.IO.Path.Combine(_root, "nadie la muestra")).FullName;

        Assert.False(ExplorerFolders.TryBringToFront(nowhere));
    }

    // ───────── Move, with a fake platform ─────────

    private sealed class Texts : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key + ": " + string.Join(", ", args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    private sealed class RecordingPlatform : IPlatformServices
    {
        public bool WindowExists { get; init; }
        public List<string> Focused { get; } = [];
        public List<string> Revealed { get; } = [];
        public bool FocusFolderInFileManager(string folder)
        {
            Focused.Add(folder);
            return WindowExists;
        }
        public void RevealInFileManager(string path) => Revealed.Add(path);
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) => Task.FromResult(new ModernContextMenuResult(false));
        public void RaiseTopmost(nint windowHandle) { }
    }

    [Fact]
    public async Task Move_puts_the_files_in_the_folder_and_shows_it_by_bringing_its_window_forward()
    {
        var (source, target) = (Directory.CreateDirectory(System.IO.Path.Combine(_root, "origen")).FullName, Directory.CreateDirectory(System.IO.Path.Combine(_root, "destino")).FullName);
        File.WriteAllText(System.IO.Path.Combine(source, "a.txt"), "uno");
        var folder = Directory.CreateDirectory(System.IO.Path.Combine(source, "carpeta")).FullName;
        File.WriteAllText(System.IO.Path.Combine(folder, "b.txt"), "dos");
        File.WriteAllText(System.IO.Path.Combine(target, "a.txt"), "ya estaba");
        var platform = new RecordingPlatform { WindowExists = true };

        Assert.True(await new FileActions(platform, new Texts(), NullLogger<FileActions>.Instance).MoveToAsync([System.IO.Path.Combine(source, "a.txt"), folder], target));

        Assert.Equal("ya estaba", File.ReadAllText(System.IO.Path.Combine(target, "a.txt")));          // never overwrites
        Assert.Equal("uno", File.ReadAllText(System.IO.Path.Combine(target, "a 2.txt")));
        Assert.Equal("dos", File.ReadAllText(System.IO.Path.Combine(target, "carpeta", "b.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(source));
        Assert.Equal([target], platform.Focused);
        Assert.Empty(platform.Revealed); // a window came forward: no second one
    }

    [Fact]
    public async Task Move_opens_the_folder_when_no_window_shows_it()
    {
        var (source, target) = (Directory.CreateDirectory(System.IO.Path.Combine(_root, "origen")).FullName, Directory.CreateDirectory(System.IO.Path.Combine(_root, "destino")).FullName);
        File.WriteAllText(System.IO.Path.Combine(source, "a.txt"), "uno");
        var platform = new RecordingPlatform();

        Assert.True(await new FileActions(platform, new Texts(), NullLogger<FileActions>.Instance).MoveToAsync([System.IO.Path.Combine(source, "a.txt")], target));

        Assert.True(File.Exists(System.IO.Path.Combine(target, "a.txt")));
        Assert.Equal([target], platform.Focused);
        Assert.Equal([target], platform.Revealed);
    }

    [Fact]
    public async Task Move_into_the_folder_the_files_are_in_leaves_them_and_still_shows_it()
    {
        var folder = Directory.CreateDirectory(System.IO.Path.Combine(_root, "aqui")).FullName;
        File.WriteAllText(System.IO.Path.Combine(folder, "a.txt"), "uno");
        var platform = new RecordingPlatform { WindowExists = true };

        Assert.True(await new FileActions(platform, new Texts(), NullLogger<FileActions>.Instance).MoveToAsync([System.IO.Path.Combine(folder, "a.txt")], folder + "\\"));

        Assert.Equal(["a.txt"], Directory.GetFiles(folder).Select(System.IO.Path.GetFileName));
        Assert.Equal([folder + "\\"], platform.Focused);
    }

    // ───────── Real Explorer windows (MANDARINA_UI_TESTS=1) ─────────

    [Fact]
    public async Task Move_brings_the_window_that_shows_the_folder_forward_instead_of_opening_another()
    {
        if (!UiTests || !OperatingSystem.IsWindows())
        {
            Assert.Skip("Opens Explorer windows on the screen: set MANDARINA_UI_TESTS=1 to run it.");
            return;
        }
        var source = Directory.CreateDirectory(System.IO.Path.Combine(_root, "origen")).FullName;
        var shown = Directory.CreateDirectory(System.IO.Path.Combine(_root, "destino con ventana")).FullName;
        var unshown = Directory.CreateDirectory(System.IO.Path.Combine(_root, "destino sin ventana")).FullName;
        using var explorer = new OurWindows(_root);
        var actions = new FileActions(new WindowsPlatformServices(), new Texts(), NullLogger<FileActions>.Instance);

        // A window on the destination, minimized the way Explorer often waits in the taskbar.
        var window = await explorer.OpenAsync(shown);
        NativeWindows.ShowWindow(window, NativeWindows.SW_MINIMIZE);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(NativeWindows.IsIconic(window));
        var windowsBefore = OnSta(() => new ShellExplorerWindows().ListRaw());
        var framesBefore = windowsBefore.Select(entry => entry.Window).ToHashSet();

        File.WriteAllText(System.IO.Path.Combine(source, "foto.txt"), "hola");
        Assert.True(await actions.MoveToAsync([System.IO.Path.Combine(source, "foto.txt")], shown));
        await Task.Delay(1500, TestContext.Current.CancellationToken); // a second window, if one were going to open, has had time to

        Assert.Equal("hola", File.ReadAllText(System.IO.Path.Combine(shown, "foto.txt")));
        var windowsAfter = OnSta(() => new ShellExplorerWindows().ListRaw());
        Assert.Single(windowsAfter, entry => OurWindows.Shows(entry, shown));
        Assert.Equal(framesBefore, windowsAfter.Select(entry => entry.Window).ToHashSet()); // not one window more
        Assert.False(NativeWindows.IsIconic(window), "the minimized window was not restored");
        explorer.Note($"after Move on a minimized window: foreground is the window = {NativeWindows.GetForegroundWindow() == window}, minimized = {NativeWindows.IsIconic(window)}");

        // A folder no window shows opens one, as before.
        File.WriteAllText(System.IO.Path.Combine(source, "otra.txt"), "adiós");
        Assert.True(await actions.MoveToAsync([System.IO.Path.Combine(source, "otra.txt")], unshown));
        Assert.NotEqual(0, await explorer.WaitForWindowAsync(unshown));
        Assert.Single(OnSta(() => new ShellExplorerWindows().ListRaw()), entry => OurWindows.Shows(entry, unshown));
    }

    [Fact]
    public async Task The_tabs_of_one_window_are_told_apart()
    {
        if (!UiTests || !OperatingSystem.IsWindows())
        {
            Assert.Skip("Opens Explorer windows on the screen: set MANDARINA_UI_TESTS=1 to run it.");
            return;
        }
        var (a, b, c) = (Directory.CreateDirectory(System.IO.Path.Combine(_root, "A pestaña")).FullName, Directory.CreateDirectory(System.IO.Path.Combine(_root, "B pestaña")).FullName,
            Directory.CreateDirectory(System.IO.Path.Combine(_root, "C pestaña")).FullName);
        using var explorer = new OurWindows(_root);
        // What Windows() says about a window with tabs is written to mandarina-explorer-tabs.txt (next to the screenshots) as it goes, and the
        // checks are made at the end: one that fails does not hide what the rest of the scenario shows.
        var report = new StringBuilder();
        var failures = new List<string>();
        void Dump(string moment)
        {
            report.AppendLine($"== {moment}");
            report.AppendLine(explorer.Describe());
            File.WriteAllText(System.IO.Path.Combine(TestServices.ScreenshotDirectory, "mandarina-explorer-tabs.txt"), report.ToString());
        }
        void Check(bool ok, string what)
        {
            report.AppendLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok)
                failures.Add(what);
        }
        bool? ActiveTab(IReadOnlyList<ExplorerWindow> windows, string folder, nint window) =>
            windows.Where(entry => OurWindows.Shows(entry, folder) && entry.Window == window).Select(entry => entry.IsActiveTab).DefaultIfEmpty(null).First();
        async Task<bool> AddTabShowingAsync(nint frame, string folder, params string[] known)
        {
            // The tab strip's "+" button is pressed through UI Automation (no mouse, no keys); the new tab shows Home, and then goes to the folder.
            report.AppendLine(await explorer.AddTabAsync(frame));
            var fresh = await explorer.WaitForTabAsync(frame, entry => !known.Any(folderKnown => OurWindows.Shows(entry, folderKnown)));
            if (fresh is null)
            {
                Dump("no new tab appeared");
                return false;
            }
            report.AppendLine($"the new tab shows: url={fresh.Value.Url} path={fresh.Value.Path}");
            var error = OnSta(() => explorer.NavigateEntry(frame, fresh.Value.Url, folder));
            if (error is not null)
            {
                report.AppendLine("Navigate2 on the new tab failed: " + error);
                return false;
            }
            return await explorer.WaitForTabAsync(frame, entry => OurWindows.Shows(entry, folder)) is not null;
        }

        var frame = await explorer.OpenAsync(a);
        Dump("window opened on A");

        // A second tab, in front: A is behind it.
        if (!await AddTabShowingAsync(frame, b, a))
        {
            Assert.Skip("Could not add a tab to the window (needs Windows 11 22H2 or later): see mandarina-explorer-tabs.txt");
            return;
        }
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Dump("tab B added in front of A");

        var entries = OnSta(() => new ShellExplorerWindows().ListRaw()).Where(entry => OurWindows.Shows(entry, a) || OurWindows.Shows(entry, b)).ToList();
        Check(entries.Count == 2, $"Windows() lists one entry per tab: {entries.Count} entries for the two tabs");
        Check(entries.Count == 2 && entries[0].Window == entries[1].Window, "every tab reports the HWND of the window");
        var marked = OnSta(() => new ShellExplorerWindows().List());
        Check(ActiveTab(marked, b, frame) == true, "B (on show) is marked active");
        Check(ActiveTab(marked, a, frame) == false, "A (behind B) is marked as a tab behind");
        Check(ExplorerFolders.Find(b, marked) is { Window: var found } && found == frame, "the window that shows B is found for B");
        Check(ExplorerFolders.Find(a, marked) is null, "no window is found for A, which is behind a tab");

        // Move into the folder on show: the window is used. Into the one behind a tab: what the window shows is not that folder.
        var source = Directory.CreateDirectory(System.IO.Path.Combine(_root, "origen")).FullName;
        var actions = new FileActions(new WindowsPlatformServices(), new Texts(), NullLogger<FileActions>.Instance);
        var framesBefore = OnSta(() => new ShellExplorerWindows().ListRaw()).Select(entry => entry.Window).ToHashSet();
        File.WriteAllText(System.IO.Path.Combine(source, "uno.txt"), "1");
        Check(await actions.MoveToAsync([System.IO.Path.Combine(source, "uno.txt")], b), "Move into B worked");
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Check(framesBefore.SetEquals(OnSta(() => new ShellExplorerWindows().ListRaw()).Select(entry => entry.Window)), "Move into B (on show) opened no new window");

        File.WriteAllText(System.IO.Path.Combine(source, "dos.txt"), "2");
        Check(await actions.MoveToAsync([System.IO.Path.Combine(source, "dos.txt")], a), "Move into A worked");
        var opened = await explorer.WaitForWindowAsync(a, besides: frame);
        Dump("after Move into A, which was behind B");
        report.AppendLine(opened != 0 ? "Move into A (behind a tab) opened a new window, as it does for a folder nobody shows"
            : "Move into A (behind a tab) opened no new window: Explorer itself reused the tab");

        // A third tab in front: the other two are behind it.
        if (await AddTabShowingAsync(frame, c, a, b))
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Dump("tab C added in front");
            marked = OnSta(() => new ShellExplorerWindows().List());
            Check(ActiveTab(marked, c, frame) == true, "C (in front) is marked active");
            Check(ActiveTab(marked, a, frame) == false, "A is a tab behind now");
            Check(ActiveTab(marked, b, frame) == false, "B is a tab behind now");
        }
        else
            Check(false, "a third tab could not be added");

        Dump("end");
        Assert.True(failures.Count == 0, "see mandarina-explorer-tabs.txt: " + string.Join("; ", failures));
    }

    // ───────── Helpers for the real windows ─────────

    /// <summary>Runs <paramref name="work"/> on an STA thread of its own (what COM wants) and brings its result back.</summary>
    private static void OnSta(Action work) => OnSta(() =>
    {
        work();
        return true;
    });

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static class NativeWindows
    {
        public const int SW_MINIMIZE = 6;
        public const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll")] public static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] public static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] public static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] public static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint window, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint window, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(nint window, EnumChild callback, nint data);
        [DllImport("user32.dll")] public static extern nint GetParent(nint window);
        [DllImport("kernel32.dll")] public static extern uint GetACP();

        public delegate bool EnumChild(nint window, nint data);

        public static string Text(nint window)
        {
            var text = new StringBuilder(512);
            GetWindowTextW(window, text, text.Capacity);
            return text.ToString();
        }

        public static string Class(nint window)
        {
            var name = new StringBuilder(256);
            GetClassNameW(window, name, name.Capacity);
            return name.ToString();
        }
    }

    /// <summary>
    /// The Explorer windows a UI test opened, which are the only ones it ever closes: a window counts only if it was not there before the
    /// test and every one of its tabs shows a folder under the test's own temporary folder. The user's windows are only read.
    /// </summary>
    private sealed class OurWindows(string root) : IDisposable
    {
        private readonly HashSet<nint> _before = OnSta(() => new ShellExplorerWindows().ListRaw()).Select(entry => entry.Window).ToHashSet();
        private readonly string _folderName = System.IO.Path.GetFileName(root); // unique to this test run, whatever the spelling of the temp path before it
        private readonly StringBuilder _notes = new();

        public static bool Shows(ExplorerWindow entry, string folder) =>
            string.Equals(ExplorerFolders.Key(entry.Path), ExplorerFolders.Key(folder), StringComparison.OrdinalIgnoreCase)
            || string.Equals(ExplorerFolders.Key(entry.Url), ExplorerFolders.Key(folder), StringComparison.OrdinalIgnoreCase);

        public void Note(string text) => _notes.AppendLine(text);

        private bool IsOurs(IEnumerable<ExplorerWindow> tabs) =>
            tabs.All(tab => ExplorerFolders.Key(tab.Path) is { } path && path.Contains("\\" + _folderName + "\\", StringComparison.OrdinalIgnoreCase));

        /// <summary>The windows (not tabs) that are ours.</summary>
        private List<nint> Ours() => OnSta(() => new ShellExplorerWindows().ListRaw())
            .GroupBy(entry => entry.Window)
            .Where(window => !_before.Contains(window.Key) && IsOurs(window))
            .Select(window => window.Key)
            .ToList();

        /// <summary>Starts explorer.exe on the folder, as Move does, and waits for the window that shows it.</summary>
        public async Task<nint> OpenAsync(string folder)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
            var window = await WaitForWindowAsync(folder);
            Assert.NotEqual((nint)0, window);
            Assert.DoesNotContain(window, _before); // a window of the user's must never be the one this test works on
            return window;
        }

        public async Task<nint> WaitForWindowAsync(string folder, nint besides = 0)
        {
            for (var i = 0; i < 60; i++)
            {
                var found = OnSta(() => new ShellExplorerWindows().ListRaw()).FirstOrDefault(entry => Shows(entry, folder) && entry.Window != besides && !_before.Contains(entry.Window));
                if (found.Window != 0)
                    return found.Window;
                await Task.Delay(250, TestContext.Current.CancellationToken);
            }
            return 0;
        }

        /// <summary>Waits for an entry of <paramref name="window"/> (one of its tabs) that <paramref name="accept"/> likes.</summary>
        public async Task<ExplorerWindow?> WaitForTabAsync(nint window, Func<ExplorerWindow, bool> accept)
        {
            for (var i = 0; i < 40; i++)
            {
                var found = OnSta(() => new ShellExplorerWindows().ListRaw()).FirstOrDefault(entry => entry.Window == window && accept(entry));
                if (found.Window != 0)
                    return found;
                await Task.Delay(250, TestContext.Current.CancellationToken);
            }
            return null;
        }

        /// <summary>Takes the tab of our window that shows <paramref name="fromUrl"/> to <paramref name="folder"/> (Navigate2). Null, or why it did not work.</summary>
        public string? NavigateEntry(nint window, string? fromUrl, string folder)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            try
            {
                dynamic windows = shell.Windows();
                for (int i = 0, count = windows.Count; i < count; i++)
                {
                    dynamic entry = windows.Item(i);
                    if (entry is not null && (nint)(long)entry.HWND == window && (string?)entry.LocationURL == fromUrl)
                    {
                        entry.Navigate2(folder);
                        return null;
                    }
                }
                return "the tab is not in Windows()";
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        /// <summary>
        /// Presses the "+" of the window's tab strip through UI Automation (Windows PowerShell has the client) and returns what it saw and did.
        /// Nothing is sent to the keyboard or the mouse: the button is invoked, in our own window only.
        /// </summary>
        public async Task<string> AddTabAsync(nint window)
        {
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-InputFormat", "None", "-STA", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(AddTabScript)) })
                start.ArgumentList.Add(argument);
            start.Environment["MANDARINA_FRAME"] = window.ToString();
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            return await output;
        }

        private const string AddTabScript = """
            $ErrorActionPreference = 'Stop'
            Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
            $AE = [Windows.Automation.AutomationElement]
            $frame = $AE::FromHandle([IntPtr]::new([long]$env:MANDARINA_FRAME))
            function Controls {
                $cond = New-Object Windows.Automation.OrCondition(
                    (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Button)),
                    (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::TabItem)),
                    (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Tab)))
                $frame.FindAll([Windows.Automation.TreeScope]::Descendants, $cond)
            }
            function Show($when) {
                "-- UI Automation, $when"
                foreach ($e in Controls) { '   {0} | id={1} | name={2} | class={3} | offscreen={4}' -f $e.Current.ControlType.ProgrammaticName, $e.Current.AutomationId, $e.Current.Name, $e.Current.ClassName, $e.Current.IsOffscreen }
            }
            Show 'before'
            $add = $null
            foreach ($e in Controls) { if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.AutomationId -eq 'AddButton') { $add = $e; break } }
            if (-not $add) { foreach ($e in Controls) { if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -match 'tab|pesta|nuev|new') { $add = $e; break } } }
            if (-not $add) { 'NO ADD BUTTON FOUND'; exit 0 }
            "pressing: id=$($add.Current.AutomationId) name=$($add.Current.Name)"
            $add.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 1500
            Show 'after'
            """;

        /// <summary>What Windows() says about every window of ours, and the window's own title and tab windows: for the notes.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            var entries = OnSta(() => new ShellExplorerWindows().ListRaw());
            foreach (var entry in entries.Where(entry => !_before.Contains(entry.Window)))
                text.AppendLine($"  entry window={entry.Window} name='{entry.Name}' url={entry.Url} path={entry.Path}");
            text.Append(OnSta(() => VisibleProperties()));
            foreach (var window in entries.Select(entry => entry.Window).Where(window => !_before.Contains(window)).Distinct())
            {
                text.AppendLine($"  window {window}: class={NativeWindows.Class(window)} title='{NativeWindows.Text(window)}' minimized={NativeWindows.IsIconic(window)}");
                NativeWindows.EnumChildWindows(window, (child, _) =>
                {
                    var name = NativeWindows.Class(child);
                    if (name.Contains("Tab", StringComparison.Ordinal))
                        text.AppendLine($"    child {child} {name} visible={NativeWindows.IsWindowVisible(child)} parent={NativeWindows.GetParent(child)}");
                    return true;
                }, 0);
            }
            return text.ToString();
        }

        /// <summary>The browser object's own Visible / Busy / ReadyState for each of our entries, in case one of them tells the tab on show.</summary>
        private string VisibleProperties()
        {
            var text = new StringBuilder();
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            try
            {
                dynamic windows = shell.Windows();
                for (int i = 0, count = windows.Count; i < count; i++)
                {
                    dynamic entry = windows.Item(i);
                    try
                    {
                        if (entry is null || _before.Contains((nint)(long)entry.HWND))
                            continue;
                        text.AppendLine($"  entry #{i}: hwnd={(long)entry.HWND} Visible={entry.Visible} Busy={entry.Busy} ReadyState={entry.ReadyState} LocationName='{entry.LocationName}' Left={entry.Left} Top={entry.Top} W={entry.Width} H={entry.Height}");
                    }
                    catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                        text.AppendLine($"  entry #{i}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
            return text.ToString();
        }

        public void Dispose()
        {
            if (_notes.Length > 0)
                File.AppendAllText(System.IO.Path.Combine(TestServices.ScreenshotDirectory, "mandarina-explorer-notes.txt"), "\n" + _notes);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var ours = Ours();
                if (ours.Count == 0)
                    return;
                foreach (var window in ours)
                    NativeWindows.PostMessage(window, NativeWindows.WM_CLOSE, 0, 0);
                Thread.Sleep(250);
            }
        }
    }
}
