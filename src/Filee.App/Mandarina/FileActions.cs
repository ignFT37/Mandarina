// Mandarina: the actions wheel (Alt on Windows). Sharing is Windows' Share sheet (Nearby sharing,
// Phone Link, mail, …) and Terminal opens Windows Terminal. Actions work with files, folders and mixed selections,
// and change nothing but what they say (Move is the only one that touches the originals).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Filee.App.Mandarina;

/// <summary>
/// How an action ended: done (the ✓), left without doing anything (the picker was closed, the user cancelled), or failed with a
/// <see cref="Problem"/> worth telling the user (a localized sentence). A bool converts to it.
/// </summary>
public readonly record struct ActionResult(bool Succeeded, string? Problem = null)
{
    public static implicit operator ActionResult(bool succeeded) => new(succeeded);

    public static ActionResult Failed(string problem) => new(false, problem);
}

public sealed class FileActions(IPlatformServices platform, ILocalizer loc, ILogger<FileActions> log)
{
    /// <summary>
    /// How long after the share sheet closes the ZIPs made of folders are deleted. The sheet hands the files to the target the user
    /// picked, and a target may read them a little later than the sheet closes; a ZIP that is still in use then is tried again later.
    /// </summary>
    internal TimeSpan ShareCleanupDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Where the ZIPs of folders to share go; null for the temp folder (tests give their own).</summary>
    internal string? StagingRoot { get; init; }

    /// <summary>
    /// Where the wheel's progress ring starts for action <paramref name="id"/>: empty when the action waits for the user (the folder
    /// picker of Move) or has to compress folders first (Share), full when it only has to open something.
    /// </summary>
    internal static double StartProgress(string id, IReadOnlyList<string> paths) =>
        id == "move" || (id == "share" && paths.Any(Directory.Exists)) ? 0 : 1;

    /// <summary>
    /// Removes the temporary ZIPs an earlier run left behind (it was closed with the share sheet open, or crashed). Call it at start-up.
    /// </summary>
    internal static void SweepLeftovers()
    {
        try
        {
            ShareStaging.Sweep(ShareStaging.SweepAge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a courtesy, not something to stop the app for
        }
    }

    /// <summary>
    /// Runs action <paramref name="id"/> on <paramref name="paths"/>. <paramref name="owner"/> hosts the folder picker
    /// and the clipboard; <paramref name="at"/> is where the wheel was (the floating shelf opens there).
    /// <paramref name="progress"/> (0 to 1) hears how far a long action is (compressing folders to share), and
    /// <paramref name="cancel"/> stops it. The result says whether it worked, and what to tell the user when it failed.
    /// </summary>
    public async Task<ActionResult> RunAsync(string id, IReadOnlyList<string> paths, Window owner, PixelPoint at,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (paths.Count == 0)
            return false;
        try
        {
            switch (id)
            {
                case "share":
                    return await ShareAsync(owner.TryGetPlatformHandle()?.Handle ?? 0, paths, progress, cancel);
                case "float":
                    FloatShelfWindow.Open(paths, loc, at);
                    return true;
                case "copy":
                    if (platform.CopyFilesToClipboard(paths))
                        return true;
                    return await CopyWithAvaloniaAsync(owner, paths);
                case "copyPath":
                    if (owner.Clipboard is not { } clipboard)
                        return false;
                    await clipboard.SetTextAsync(string.Join(Environment.NewLine, paths));
                    return true;
                case "move":
                    return await MoveAsync(owner, paths);
                case "terminal":
                    var first = paths[0];
                    var folder = Directory.Exists(first) ? first : Path.GetDirectoryName(first);
                    if (folder is null)
                        return false;
                    platform.OpenTerminal(folder);
                    return true;
                default:
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or NotSupportedException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            log.LogWarning(ex, "File action {Action} failed", id);
            return false;
        }
    }

    /// <summary>
    /// Opens the share sheet. It takes files, not folders, so every folder is compressed to a ZIP first (off the UI thread, with
    /// progress, and cancellable); the ZIPs are deleted when the sheet closes, or at once when it could not be opened.
    /// </summary>
    internal async Task<ActionResult> ShareAsync(nint ownerHandle, IReadOnlyList<string> paths, IProgress<double>? progress, CancellationToken cancel)
    {
        ShareStaging staged;
        try
        {
            staged = await Task.Run(() => ShareStaging.Prepare(paths, progress, cancel, StagingRoot), cancel);
        }
        catch (ShareStagingException ex)
        {
            log.LogWarning(ex, "Share: could not compress {Folder}", ex.Folder);
            return ActionResult.Failed(Describe(ex));
        }

        var opened = false;
        try
        {
            if (staged.Skipped.Count > 0)
                log.LogInformation("Share: left out {Count} link(s) inside the folders: {Links}", staged.Skipped.Count, string.Join(", ", staged.Skipped));
            if (staged.Files.Count == 0)
                return false;
            // Windows PowerShell opens the sheet and says when it is up: a few seconds, not on the UI thread.
            var delay = ShareCleanupDelay;
            opened = await Task.Run(() => platform.ShareFiles(staged.Files, ownerHandle, () => staged.ReleaseSoon(delay)), cancel);
            return opened;
        }
        finally
        {
            if (!opened)
                staged.Dispose();
        }
    }

    /// <summary>What went wrong with a folder to share, as a sentence in the user's language.</summary>
    private string Describe(ShareStagingException ex) => ex.Problem switch
    {
        ShareProblem.InUse => loc.Format("wheel.share.failed_inuse", ex.Folder, ex.Item ?? ex.Folder),
        ShareProblem.NoAccess => loc.Format("wheel.share.failed_access", ex.Folder, ex.Item ?? ex.Folder),
        ShareProblem.NoSpace => loc.Format("wheel.share.failed_space", ex.Folder),
        _ => loc.Format("wheel.share.failed", ex.Folder, ex.InnerException?.Message ?? ex.Message),
    };

    private static async Task<bool> CopyWithAvaloniaAsync(TopLevel owner, IReadOnlyList<string> paths)
    {
        if (owner.Clipboard is not { } clipboard)
            return false;
        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await owner.StorageProvider.TryGetFolderFromPathAsync(path)
                : await owner.StorageProvider.TryGetFileFromPathAsync(path);
            if (item is not null)
                items.Add(item);
        }
        if (items.Count == 0)
            return false;
        await clipboard.SetFilesAsync(items);
        return true;
    }

    /// <summary>Asks for a folder and moves the originals there; a name already taken there gets " 2", " 3", ….</summary>
    private async Task<bool> MoveAsync(TopLevel owner, IReadOnlyList<string> paths)
    {
        var start = Path.GetDirectoryName(paths[0].TrimEnd('\\', '/'));
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = loc["wheel.move.picker"],
            AllowMultiple = false,
            SuggestedStartLocation = start is null ? null : await owner.StorageProvider.TryGetFolderFromPathAsync(start),
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } target)
            return false;
        return await MoveToAsync(paths, target);
    }

    /// <summary>Moves the originals into <paramref name="target"/> and shows that folder (see <see cref="ShowFolderAsync"/>).</summary>
    internal async Task<bool> MoveToAsync(IReadOnlyList<string> paths, string target)
    {
        foreach (var path in paths)
        {
            var source = path.TrimEnd('\\', '/');
            var name = Path.GetFileName(source);
            if (string.Equals(Path.GetDirectoryName(source), target.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                continue; // already there
            var destination = FreePath(target, name);
            if (Directory.Exists(source))
                MoveFolder(source, destination);
            else
                File.Move(source, destination);
        }
        await ShowFolderAsync(target);
        return true;
    }

    /// <summary>
    /// Shows <paramref name="folder"/> in the file manager. A window that already shows exactly that folder comes to the front
    /// (restored if minimized) instead of one more piling up; with none, the folder opens in a new one.
    /// </summary>
    private async Task ShowFolderAsync(string folder)
    {
        // The lookup asks Explorer over COM and may have to wait for it: not on the UI thread.
        if (await Task.Run(() => platform.FocusFolderInFileManager(folder)))
            return;
        platform.RevealInFileManager(folder);
    }

    /// <summary><paramref name="name"/> in <paramref name="folder"/>, or "name 2.ext", "name 3.ext", … when taken.</summary>
    internal static string FreePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            path = Path.Combine(folder, $"{stem} {n}{extension}");
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
        }
    }

    /// <summary>Directory.Move, or copy then delete when the folder goes to another drive.</summary>
    private static void MoveFolder(string source, string destination)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, destination);
            return;
        }
        CopyFolder(source, destination);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyFolder(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
}
