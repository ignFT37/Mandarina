// Mandarina: the Move action ends by showing the folder the files went to. That used to start explorer.exe, which opens a NEW window
// every time; now a File Explorer window that is already showing exactly that folder is brought to the front instead (restored first
// when it was minimized), and a new one opens only when there is none.
//
// The windows are listed through Shell.Application, late-bound like GetFileManagerSelection does it: Windows() gives one entry per
// window (and, on Windows 11, one per TAB), each with HWND, LocationURL ("file:///C:/Users/x") and Document.Folder.Self.Path.
// Which window shows the folder is a pure function over a list (ExplorerFolders.Find), tested without Explorer; the two things that touch
// the system sit behind small interfaces (IExplorerWindows, IWindowFront). Only File Explorer windows (class CabinetWClass) the user could
// see right now on this desktop are ever offered: not the desktop (a window of the shell too, should it be listed), not a window on
// another virtual desktop, not a hidden one. The user's windows are never closed or navigated, only brought to the front.
//
// Tabs (Windows 11 build 26200, measured by MandarinaExplorerWindowsTests.The_tabs_of_one_window_are_told_apart): Windows() lists one
// entry per tab, in the order they were opened, and every entry reports the HWND of the window. Nothing in the entries tells the tab on
// show from the others (not the HWND, not Visible, not the size, not the visibility of the window's ShellTabWindowClass children, which
// are all visible). The window title does: "B pestaña y 1 pestaña más - Explorador de archivos" starts with the name of the tab on show,
// and that is what MarkActiveTabs reads. When the title cannot tell (two tabs with the same name, a title in some other shape) the tab
// counts as possibly on show, and the window is brought forward anyway. A folder that is open only in a tab behind another one does NOT
// count as shown: a new window opens, as before (explorer.exe opens a new window for it; it does not switch to the tab). Selecting a tab
// of the user's window would be touching it, which this never does.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace Filee.Platform.Windows;

/// <summary>One File Explorer window, or one tab of one, as Shell.Application lists it.</summary>
/// <param name="Window">The top-level window. Every tab of a window reports the same one.</param>
/// <param name="Url">LocationURL: "file:///C:/Users/x" for a folder on disk, "file:///::{…}" for a virtual one.</param>
/// <param name="Path">Document.Folder.Self.Path: the folder's path, "::{…}" for a virtual one.</param>
/// <param name="Name">LocationName: the folder's display name ("Descargas"), as the window title starts with it.</param>
/// <param name="IsActiveTab">False for a tab behind another one, true for the tab on show, null when that could not be told.</param>
public readonly record struct ExplorerWindow(nint Window, string? Url, string? Path, string? Name = null, bool? IsActiveTab = true);

/// <summary>The File Explorer windows that are open.</summary>
public interface IExplorerWindows
{
    /// <summary>The folder windows the user can see on this desktop, the one in front first, one entry per tab.</summary>
    IReadOnlyList<ExplorerWindow> List();
}

/// <summary>Puts a window in front of the user.</summary>
public interface IWindowFront
{
    void BringToFront(nint window);
}

[SupportedOSPlatform("windows")]
public static class ExplorerFolders
{
    /// <summary>How long the whole lookup may take; a stuck Explorer must not hold the Move back.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// What two spellings of the same folder have in common: "C:\Users\Me\Docs" is also "c:\users\me\docs\", "file:///C:/Users/Me/Docs",
    /// "file:///C:/Users/Me/D%6Fcs" and "C:/Users/Me/./Docs"; "file://srv/share/x" is "\\srv\share\x". Compare keys without regard to case.
    /// Null for nothing; a virtual folder ("::{…}") keeps its own spelling and matches nothing else.
    /// </summary>
    public static string? Key(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["file:".Length..];
            var slashes = 0;
            while (slashes < text.Length && text[slashes] == '/')
                slashes++;
            text = Unescape(text[slashes..]);
            // file:///C:/x and file:/C:/x are drive paths, file://server/share and file:////server/share are network ones.
            if (slashes is 2 or >= 4)
                text = "//" + text;
        }

        text = text.Replace('/', '\\');
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            text = @"\\" + text[@"\\?\UNC\".Length..];
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
            text = text[4..];

        if (text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
            text += '\\';
        var isDrivePath = text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '\\';
        if (!isDrivePath && !text.StartsWith(@"\\", StringComparison.Ordinal))
            return text; // "::{…}" and anything else that is not a path on disk
        try
        {
            text = Path.GetFullPath(text); // "." and ".." segments, doubled separators
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        var root = Path.GetPathRoot(text) ?? "";
        return text.Length > root.Length ? text.TrimEnd('\\') : text;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes the %xx escapes of an address. Explorer writes the letters its code page has as single bytes ("pesta%F1a", measured on a
    /// Spanish Windows 11), where the URI standard says UTF-8 ("pesta%C3%B1a"); both are read, UTF-8 first.
    /// </summary>
    private static string Unescape(string text)
    {
        if (!text.Contains('%'))
            return text;
        var result = new StringBuilder(text.Length);
        var bytes = new List<byte>();
        void Flush()
        {
            if (bytes.Count == 0)
                return;
            var raw = bytes.ToArray();
            try
            {
                result.Append(StrictUtf8.GetString(raw));
            }
            catch (DecoderFallbackException)
            {
                result.Append(AnsiEncoding().GetString(raw));
            }
            bytes.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            Flush();
            result.Append(text[i]);
        }
        Flush();
        return result.ToString();
    }

    /// <summary>The system's ANSI code page (1252 on Western European Windows), or Latin-1 if .NET has no table for it.</summary>
    private static Encoding AnsiEncoding()
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding((int)MandarinaWindowNative.GetACP()) ?? Encoding.Latin1;
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }

    private static bool Same(string wanted, string? candidate) =>
        Key(candidate) is { } key && string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The entry that shows exactly <paramref name="folder"/>, or null. <paramref name="windows"/> comes in front-to-back order, and
    /// the first sure match wins; a tab known to be behind another one never matches, one that may or may not be on show only
    /// when no sure match exists.
    /// </summary>
    public static ExplorerWindow? Find(string folder, IEnumerable<ExplorerWindow> windows)
    {
        if (Key(folder) is not { } wanted)
            return null;
        ExplorerWindow? maybe = null;
        foreach (var window in windows)
        {
            if (window.IsActiveTab == false || !(Same(wanted, window.Path) || Same(wanted, window.Url)))
                continue;
            if (window.IsActiveTab == true)
                return window;
            maybe ??= window;
        }
        return maybe;
    }

    /// <summary>
    /// Fills in <see cref="ExplorerWindow.IsActiveTab"/>. A window with one entry has no tab behind it. With several, the tab on show is
    /// the one the window title names: its folder's name, or its path, at the start of the title (see <see cref="TitleScore"/>). If no
    /// tab is named it is unknown for all of them; if several are (tabs with the same name) it is unknown for those and false for the rest.
    /// </summary>
    public static IReadOnlyList<ExplorerWindow> MarkActiveTabs(IReadOnlyList<ExplorerWindow> entries, Func<nint, string?> titleOf)
    {
        var titles = new Dictionary<nint, string?>();
        var marked = new List<ExplorerWindow>(entries.Count);
        foreach (var entry in entries)
        {
            var tabs = entries.Where(other => other.Window == entry.Window).ToList();
            if (tabs.Count == 1)
            {
                marked.Add(entry with { IsActiveTab = true });
                continue;
            }
            if (!titles.TryGetValue(entry.Window, out var title))
                titles[entry.Window] = title = titleOf(entry.Window);
            var best = tabs.Max(tab => TitleScore(tab, title));
            var mine = TitleScore(entry, title);
            var named = tabs.Count(tab => TitleScore(tab, title) == best);
            marked.Add(entry with { IsActiveTab = best == 0 ? null : mine != best ? false : named == 1 ? true : null });
        }
        return marked;
    }

    /// <summary>
    /// How well the title of a File Explorer window names <paramref name="entry"/>: how many characters of its path or its name the title
    /// starts with, 0 for not at all. Measured on Windows 11 (Spanish): a window with one tab is titled "A pestaña - Explorador de
    /// archivos", with several "B pestaña y 1 pestaña más - Explorador de archivos": the tab on show first, then words in the user's
    /// language about the others. So the name must start the title, whatever follows, and the longest name that does wins (a tab called
    /// "B" must not take "B pestaña"'s place).
    /// </summary>
    private static int TitleScore(ExplorerWindow entry, string? title)
    {
        if (string.IsNullOrEmpty(title))
            return 0;
        var score = 0;
        foreach (var name in new[] { entry.Path, entry.Name })
        {
            if (!string.IsNullOrEmpty(name) && !name.StartsWith("::", StringComparison.Ordinal) && title.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, name.Length);
        }
        return score;
    }

    /// <summary>
    /// Brings the window that shows <paramref name="folder"/> to the front. False when there is none (or Explorer could not be asked):
    /// the caller opens one. Never throws.
    /// </summary>
    public static bool TryBringToFront(string folder, IExplorerWindows windows, IWindowFront front)
    {
        try
        {
            if (Find(folder, windows.List()) is not { } found)
                return false;
            front.BringToFront(found.Window);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Explorer restarting, a window closing under us, a COM call that fails: opening the folder as before is always possible.
            Trace.TraceWarning($"Looking for an Explorer window on a folder failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// <see cref="TryBringToFront(string, IExplorerWindows, IWindowFront)"/> with the real Explorer. COM wants a thread of its own (an
    /// STA one; the caller may be a pool thread), and the answer is waited for only so long.
    /// </summary>
    public static bool TryBringToFront(string folder)
    {
        var found = false;
        var thread = new Thread(() => found = TryBringToFront(folder, new ShellExplorerWindows(), new Win32WindowFront()))
        {
            IsBackground = true,
            Name = "Mandarina Explorer windows",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(LookupTimeout) && found;
    }
}

/// <summary>The real list: Shell.Application's Windows(), in front-to-back order, with the tabs marked.</summary>
[SupportedOSPlatform("windows")]
public sealed class ShellExplorerWindows : IExplorerWindows
{
    /// <summary>Must run on an STA thread.</summary>
    public IReadOnlyList<ExplorerWindow> List() => Enumerate(raw: false);

    /// <summary>
    /// The entries as Windows() reports them, in its own order, with <see cref="ExplorerWindow.IsActiveTab"/> left unknown: for tests
    /// and measurements (the folder windows only, as in <see cref="List()"/>).
    /// </summary>
    public IReadOnlyList<ExplorerWindow> ListRaw() => Enumerate(raw: true);

    private static IReadOnlyList<ExplorerWindow> Enumerate(bool raw)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null)
            return [];
        var entries = new List<ExplorerWindow>();
        dynamic? shell = null;
        dynamic? windows = null;
        try
        {
            shell = Activator.CreateInstance(type)!;
            windows = shell.Windows();
            int count = windows.Count;
            for (var i = 0; i < count; i++)
            {
                if (Read(windows, i) is { } entry)
                    entries.Add(entry);
            }
        }
        catch (COMException)
        {
            return []; // no Explorer to ask (it is restarting, or this is a session without a shell)
        }
        finally
        {
            Release((object?)windows);
            Release((object?)shell);
        }
        if (raw)
            return entries.Select(entry => entry with { IsActiveTab = null }).ToList();

        var ranks = FrontToBack();
        var ordered = entries.OrderBy(entry => ranks.GetValueOrDefault(entry.Window, int.MaxValue)).ToList();
        return ExplorerFolders.MarkActiveTabs(ordered, TitleOf);
    }

    /// <summary>The entry at <paramref name="index"/> of the list, or null when it is not a folder window the user could see.</summary>
    private static ExplorerWindow? Read(dynamic windows, int index)
    {
        dynamic? entry = null;
        try
        {
            entry = windows.Item(index);
            if (entry is null)
                return null;
            var window = (nint)(long)entry.HWND;
            if (!IsVisibleFolderWindow(window))
                return null;
            return new ExplorerWindow(
                window,
                Text(() => (string?)entry.LocationURL),
                Text(() => (string?)entry.Document.Folder.Self.Path),
                Text(() => (string?)entry.LocationName),
                null);
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException or InvalidComObjectException)
        {
            return null; // a window that closed meanwhile, or an entry that is not a browser at all
        }
        finally
        {
            Release((object?)entry);
        }
    }

    private static string? Text(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException or InvalidComObjectException)
        {
            return null; // e.g. Document is null while a window is still loading
        }
    }

    private static void Release(object? com)
    {
        try
        {
            if (com is not null && Marshal.IsComObject(com))
                Marshal.FinalReleaseComObject(com);
        }
        catch (InvalidComObjectException)
        {
        }
    }

    /// <summary>A File Explorer window (not the desktop, nor any other window the shell lists) that is visible and on this virtual desktop.</summary>
    private static bool IsVisibleFolderWindow(nint window)
    {
        if (window == 0 || !MandarinaWindowNative.IsWindow(window) || !MandarinaWindowNative.IsWindowVisible(window))
            return false;
        if (NativeMethods.ClassNameOf(window) is not ("CabinetWClass" or "ExploreWClass"))
            return false;
        // A window on another virtual desktop is "cloaked": bringing it forward would switch desktops under the user.
        return MandarinaWindowNative.DwmGetWindowAttribute(window, MandarinaWindowNative.DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private static string? TitleOf(nint window)
    {
        var buffer = new char[512];
        var length = MandarinaWindowNative.GetWindowText(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    /// <summary>Where each top-level window is in the z-order: 0 is the one in front.</summary>
    private static Dictionary<nint, int> FrontToBack()
    {
        var ranks = new Dictionary<nint, int>();
        for (var window = MandarinaWindowNative.GetTopWindow(0); window != 0 && ranks.Count < 20000;
             window = MandarinaWindowNative.GetWindow(window, MandarinaWindowNative.GW_HWNDNEXT))
            ranks.TryAdd(window, ranks.Count);
        return ranks;
    }
}

/// <summary>
/// Brings a window of another program to the front. Windows lets only the program the user is working with do that; Move is the
/// user's own gesture, with the folder picker of this process the last thing they touched, so it normally may.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32WindowFront : IWindowFront
{
    public void BringToFront(nint window)
    {
        if (!MandarinaWindowNative.IsWindow(window))
            return;
        if (MandarinaWindowNative.IsIconic(window))
            MandarinaWindowNative.ShowWindow(window, MandarinaWindowNative.SW_RESTORE); // back to maximized if it was
        if (MandarinaWindowNative.SetForegroundWindow(window))
            return;
        // Refused: put the window on top without taking the focus (its taskbar button flashes), the way the wheel's own windows are raised.
        // One that is already topmost (PowerToys' "always on top") is above everything already, and must keep that flag.
        if ((MandarinaWindowNative.GetWindowLong(window, MandarinaWindowNative.GWL_EXSTYLE) & MandarinaWindowNative.WS_EX_TOPMOST) != 0)
            return;
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        NativeMethods.SetWindowPos(window, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
        NativeMethods.SetWindowPos(window, MandarinaWindowNative.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }
}

internal static partial class MandarinaWindowNative
{
    public const int SW_RESTORE = 9;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x8;
    public const nint HWND_NOTOPMOST = -2;
    public const uint GW_HWNDNEXT = 2;
    public const int DWMWA_CLOAKED = 14;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(nint window, int index);

    [LibraryImport("user32.dll")]
    public static partial nint GetTopWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowText(nint window, [Out] char[] text, int maxCount);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetACP();
}
