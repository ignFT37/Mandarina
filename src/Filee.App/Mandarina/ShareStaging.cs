// Mandarina: what the Share action hands to Windows' share sheet. The sheet takes files, not folders, so every folder in the selection is
// compressed first into a ZIP of its own, in a temporary folder of its own (%TEMP%\Mandarina\share-<guid>\Fotos.zip), and the loose files
// go as they are. FileActions removes the ZIPs once the sheet is closed; if the app dies first, the next start sweeps the ones that are
// a day old.
//
// How a folder is read: files are opened allowing other programs to read, write and delete them (a log that is being written, a
// document open in Word); one that still cannot be read stops the whole share with a message that names it, and everything made so far
// is deleted. Links inside a folder (symbolic links, junctions, volume mount points) are not followed: they could lead out of the folder,
// to another drive, or round in a circle. OneDrive's online-only files are not links and are read like any other (which downloads them).

using System.Buffers;
using System.IO.Compression;

namespace Filee.App.Mandarina;

/// <summary>Why a folder could not be turned into a ZIP.</summary>
internal enum ShareProblem
{
    /// <summary>Another program has a file open that does not allow reading.</summary>
    InUse,

    /// <summary>The user may not read a file or folder.</summary>
    NoAccess,

    /// <summary>The disk the ZIP is written to is full.</summary>
    NoSpace,

    /// <summary>Anything else; the inner exception says what.</summary>
    Other,
}

/// <summary>A folder of the selection could not be compressed. The message is English, for the log; the action shows its own.</summary>
internal sealed class ShareStagingException(ShareProblem problem, string folder, string? item, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public ShareProblem Problem { get; } = problem;

    /// <summary>The folder's name.</summary>
    public string Folder { get; } = folder;

    /// <summary>The file that could not be read, relative to the folder ("sub/informe.docx"), when it is one file.</summary>
    public string? Item { get; } = item;
}

internal sealed class ShareStaging : IDisposable
{
    /// <summary>How old a leftover folder must be before the start-up sweep removes it.</summary>
    internal static readonly TimeSpan SweepAge = TimeSpan.FromDays(1);

    /// <summary>The share sheet gets paths through WinRT, which stops at MAX_PATH: ZIP names are cut to keep the whole path below this.</summary>
    private const int MaxZipPath = 240;

    private const int BufferSize = 128 * 1024;

    /// <summary>Formats that are compressed already: deflating them again costs time and saves nothing, so they are stored.</summary>
    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxl",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wma",
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".mpg", ".mpeg",
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst", ".cab",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk",
    };

    /// <summary>Names Windows reserves for devices, whatever the extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private string? _folder;

    private ShareStaging(IReadOnlyList<string> files, string? folder, IReadOnlyList<string> skipped)
    {
        Files = files;
        _folder = folder;
        Skipped = skipped;
    }

    /// <summary>What goes to the sheet, in the order of the selection: the loose files as they are and a ZIP in place of each folder.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>The temporary folder holding the ZIPs; null when nothing was compressed or it has been deleted.</summary>
    public string? Folder => _folder;

    /// <summary>Links found inside the folders and left out (relative paths, for the log).</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>Where the temporary folders go.</summary>
    internal static string DefaultRoot => Path.Combine(Path.GetTempPath(), "Mandarina");

    /// <summary>
    /// Compresses the folders among <paramref name="paths"/> (call it off the UI thread: a big folder takes a while) and lists what to
    /// share. Things that are not there any more are left out. <paramref name="progress"/> gets 0 to 1 by bytes read.
    /// Throws <see cref="ShareStagingException"/> when a folder cannot be read or the ZIP cannot be written, and
    /// <see cref="OperationCanceledException"/> when <paramref name="cancel"/> fires; either way nothing is left on disk.
    /// </summary>
    public static ShareStaging Prepare(IReadOnlyList<string> paths, IProgress<double>? progress = null, CancellationToken cancel = default, string? root = null)
    {
        var items = new List<(string Path, bool IsFolder)>();
        foreach (var path in paths.Select(Trimmed).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
                items.Add((path, false));
            else if (Directory.Exists(path))
                items.Add((path, true));
        }
        if (!items.Any(item => item.IsFolder))
            return new ShareStaging([.. items.Select(item => item.Path)], null, []);

        var stage = Path.Combine(root ?? DefaultRoot, "share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var total = items.Where(item => item.IsFolder).Sum(item => Measure(item.Path, stage, cancel));
            var tracker = new Tracker(progress, total);
            var skipped = new List<string>();
            var files = new List<string>();
            foreach (var (path, isFolder) in items)
                files.Add(isFolder ? Compress(path, stage, tracker, skipped, cancel) : path);
            tracker.Finish();
            return new ShareStaging(files, stage, skipped);
        }
        catch
        {
            TryDelete(stage);
            throw;
        }
    }

    /// <summary>Deletes the temporary folder now, if it is still there (it is not an error when a program still holds a ZIP).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _folder, null) is { } folder)
            TryDelete(folder);
    }

    /// <summary>
    /// Deletes the temporary folder in a while, in the background. The delay is for a share target that reads its ZIP late; one that
    /// still holds the ZIP after that (a long upload) makes the delete fail, and it is tried again every <paramref name="retryEvery"/>.
    /// </summary>
    public void ReleaseSoon(TimeSpan delay, TimeSpan? retryEvery = null, int attempts = 20)
    {
        var folder = _folder;
        if (folder is null)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (Volatile.Read(ref _folder) is null)
                    return; // Dispose got there first
                if (TryDelete(folder))
                {
                    Interlocked.Exchange(ref _folder, null);
                    return;
                }
                await Task.Delay(retryEvery ?? TimeSpan.FromSeconds(15));
            }
        });
    }

    /// <summary>
    /// Removes the temporary folders an earlier run left behind (the app was closed or crashed while a sheet was open) that are older
    /// than <paramref name="olderThan"/>. Only folders named like this class makes them are touched. Returns how many went.
    /// </summary>
    public static int Sweep(TimeSpan olderThan, string? root = null)
    {
        var folder = root ?? DefaultRoot;
        if (!Directory.Exists(folder))
            return 0;
        var limit = DateTime.UtcNow - olderThan;
        var removed = 0;
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(folder))
            {
                if (!IsStagingName(Path.GetFileName(candidate)))
                    continue;
                var info = new DirectoryInfo(candidate);
                if (new[] { info.CreationTimeUtc, info.LastWriteTimeUtc }.Max() > limit)
                    continue;
                if (TryDelete(candidate))
                    removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the sweep is a courtesy: whatever it could not reach waits for the next start
        }
        return removed;
    }

    private static bool IsStagingName(string name) =>
        name.Length == "share-".Length + 32 && name.StartsWith("share-", StringComparison.Ordinal)
        && name["share-".Length..].All(char.IsAsciiHexDigitLower);

    private static bool TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"D:" is a drive's current folder, not the drive: it needs its slash back after the trailing ones are trimmed.</summary>
    private static string Trimmed(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + '\\' : trimmed;
    }

    // ───────── Reading a folder ─────────

    /// <summary>A file of a folder, or (null <paramref name="Info"/>) an empty folder, which the ZIP keeps as an entry of its own.</summary>
    private readonly record struct Found(string EntryName, FileInfo? Info);

    /// <summary>
    /// Everything in <paramref name="root"/> that goes into its ZIP: files as "Name/sub/file.txt" and empty folders as "Name/sub/". Links and
    /// <paramref name="stage"/> itself (when the temporary folder sits inside the folder being shared) are left out; what was left out
    /// is added to <paramref name="skipped"/> when given.
    /// </summary>
    private static IEnumerable<Found> Walk(string root, string name, string stage, List<string>? skipped, CancellationToken cancel)
    {
        var pending = new Stack<(DirectoryInfo Directory, string Prefix)>();
        pending.Push((new DirectoryInfo(root), name + "/"));
        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();
            var empty = true;
            foreach (var info in Children(directory, name, prefix))
            {
                cancel.ThrowIfCancellationRequested();
                if (IsLink(info) || string.Equals(info.FullName.TrimEnd('\\'), stage, StringComparison.OrdinalIgnoreCase))
                {
                    skipped?.Add(prefix + info.Name);
                    continue;
                }
                empty = false;
                if (info is DirectoryInfo sub)
                    pending.Push((sub, prefix + sub.Name + "/"));
                else
                    yield return new Found(prefix + info.Name, (FileInfo)info);
            }
            if (empty)
                yield return new Found(prefix, null);
        }
    }

    private static FileSystemInfo[] Children(DirectoryInfo directory, string folderName, string prefix)
    {
        try
        {
            return directory.GetFileSystemInfos();
        }
        catch (DirectoryNotFoundException)
        {
            return []; // removed since it was listed
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failed(ex, folderName, prefix.TrimEnd('/'));
        }
    }

    /// <summary>True for a symbolic link, a junction or a mount point. A OneDrive placeholder is a reparse point too, but not a link.</summary>
    private static bool IsLink(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            return false;
        try
        {
            return info.LinkTarget is not null;
        }
        catch (IOException)
        {
            return true; // a reparse point that cannot be read is not something to go into
        }
    }

    private static long Measure(string folder, string stage, CancellationToken cancel)
    {
        long total = 0;
        foreach (var found in Walk(folder, FolderName(folder), stage, null, cancel))
            total += found.Info?.Length ?? 0;
        return total;
    }

    // ───────── Writing the ZIP ─────────

    private static string Compress(string folder, string stage, Tracker tracker, List<string> skipped, CancellationToken cancel)
    {
        var name = FolderName(folder);
        var zipPath = FreeZipPath(stage, name);
        try
        {
            using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // Not a using: a ZIP that is being abandoned is not finished (that would write more, and a failure there would hide the real one).
            var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var found in Walk(folder, name, stage, skipped, cancel))
            {
                if (found.Info is null)
                    zip.CreateEntry(found.EntryName); // a name ending in "/" is a folder
                else
                    AddFile(zip, found.Info, found.EntryName, name, tracker, cancel);
            }
            zip.Dispose();
        }
        catch (Exception ex) when (ex is IOException and not ShareStagingException or UnauthorizedAccessException)
        {
            throw Failed(ex, name, null); // creating or finishing the ZIP itself (a full disk, mostly)
        }
        return zipPath;
    }

    private static void AddFile(ZipArchive zip, FileInfo file, string entryName, string folderName, Tracker tracker, CancellationToken cancel)
    {
        FileStream source;
        try
        {
            source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return; // deleted since it was listed: nothing to share
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failed(ex, folderName, entryName);
        }

        using (source)
        {
            var level = AlreadyCompressed.Contains(file.Extension) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
            var entry = zip.CreateEntry(entryName, level);
            entry.LastWriteTime = ZipTime(file.LastWriteTime);
            using var target = entry.Open();
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = ReadSome(source, buffer, folderName, entryName)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    tracker.Add(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>A read that fails halfway (another program locked a stretch of the file, a network drive went away) names the file.</summary>
    private static int ReadSome(FileStream source, byte[] buffer, string folderName, string entryName)
    {
        try
        {
            return source.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failed(ex, folderName, entryName);
        }
    }

    internal static ShareStagingException Failed(Exception ex, string folderName, string? item)
    {
        var problem = ex switch
        {
            UnauthorizedAccessException => ShareProblem.NoAccess,
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => ShareProblem.InUse,         // sharing and lock violations
            IOException io when (io.HResult & 0xFFFF) is 39 or 112 => ShareProblem.NoSpace,      // handle disk full, disk full
            _ => ShareProblem.Other,
        };
        var what = item is null ? $"folder \"{folderName}\"" : $"\"{item}\"";
        return new ShareStagingException(problem, folderName, item is null ? null : item[(item.LastIndexOf('/') + 1)..],
            $"Could not compress {what} to share it ({problem}): {ex.Message}", ex);
    }

    // ───────── Names ─────────

    /// <summary>The folder's name. A root has none of its own: the drive's letter ("D:\" is "D") or the share's name ("\\srv\data" is "data").</summary>
    internal static string FolderName(string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var root = (Path.GetPathRoot(folder) ?? "").TrimEnd('\\', '/');
        if (trimmed.Length > root.Length && Path.GetFileName(trimmed) is { Length: > 0 } name)
            return name;
        var last = root[(root.LastIndexOfAny(['\\', '/', ':']) + 1)..];
        if (last.Length == 0)
            last = new string([.. root.Where(char.IsLetterOrDigit)]);
        return last.Length > 0 ? last : "Folder";
    }

    /// <summary>
    /// "name.zip" in <paramref name="directory"/>, or "name 2.zip", "name 3.zip", … while that is taken (two folders with the same
    /// name from different places). The name is made safe: no characters Windows refuses, no device names, no trailing dot or space,
    /// and short enough for the whole path to stay under the limit the share sheet accepts.
    /// </summary>
    internal static string FreeZipPath(string directory, string folderName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = string.Concat(folderName.Select(c => invalid.Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        if (stem.Length == 0)
            stem = "Folder";
        if (ReservedNames.Contains(stem.Split('.')[0]))
            stem = "_" + stem;
        var room = Math.Max(8, MaxZipPath - directory.Length - 1 - ".zip".Length - " 99".Length);
        if (stem.Length > room)
            stem = stem[..room].TrimEnd(' ', '.');

        var path = Path.Combine(directory, stem + ".zip");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{stem} {n}.zip");
        return path;
    }

    private static readonly DateTime ZipEarliest = new(1980, 1, 1);
    private static readonly DateTime ZipLatest = new(2107, 12, 31, 23, 59, 58);

    /// <summary>A ZIP entry's time must be between 1980 and 2107; files outside it (a zeroed date) get the nearest.</summary>
    private static DateTimeOffset ZipTime(DateTime local) => new(local < ZipEarliest ? ZipEarliest : local > ZipLatest ? ZipLatest : local);

    /// <summary>Counts the bytes read and tells <paramref name="progress"/> about every percent.</summary>
    private sealed class Tracker(IProgress<double>? progress, long total)
    {
        private long _done;
        private double _reported;

        public void Add(long bytes)
        {
            _done += bytes;
            if (progress is null)
                return;
            var fraction = Math.Min(1, (double)_done / Math.Max(total, 1));
            if (fraction - _reported < 0.01)
                return;
            _reported = fraction;
            progress.Report(fraction);
        }

        public void Finish() => progress?.Report(1);
    }
}
