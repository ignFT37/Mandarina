// Mandarina: what Office could really do the last time it was tried. Before the first conversion with Word, Excel or
// PowerPoint the engine converts a tiny file of its own (the self-test); if the application hangs or fails, it is left
// out of the routes instead of failing every file. This is the memory of that.
//
// Why: an Office can hang when it saves or exports -- a window nobody sees (activation, sign-in, a first-run notice), an
// add-in, a broken installation -- and a hung Office would fail every file after the same long wait. (An Office that is
// installed but not activated -- "reduced functionality" mode -- still converts all three.)
//
//  * A Works result is kept in memory for an hour.
//  * A Broken result is kept in memory for ten minutes, so a slow start or an update doesn't leave Office out for good.
//    When Windows says Office is not activated it is also saved in the engine data folder (office-self-test.json) for a
//    day, so an Office that hangs for that reason doesn't make every start of Mandarina wait for the self-test again. It
//    is forgotten when the Office version or its license changes, and by Settings → Engines → Check again.

using System.Text.Json;
using Filee.Engines.Office.Com;

namespace Filee.Engines.Office;

/// <summary>Results of the Office self-test.</summary>
internal static class OfficeSelfTest
{
    internal enum Verdict
    {
        Unknown,
        Works,
        Broken,
    }

    /// <param name="Key">Localization key of why it is broken (office.error.*).</param>
    internal sealed record Result(Verdict Verdict, string? Key = null, string? Detail = null);

    private sealed class Entry
    {
        public string Fingerprint { get; set; } = "";
        public DateTime At { get; set; }
        public bool Works { get; set; }
        public string? Key { get; set; }
        public string? Detail { get; set; }
    }

    private static readonly TimeSpan WorksFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan BrokenFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SavedFor = TimeSpan.FromDays(1);

    private static readonly object Gate = new();
    private static readonly Dictionary<OfficeKind, Entry> Memory = [];
    private static string? _directory;

    // The saved file, read once (Edges asks for the verdicts every time a route is planned).
    private static Dictionary<string, Entry>? _saved;
    private static string? _savedDirectory;

    private static Dictionary<string, Entry> Saved(string directory)
    {
        if (_saved is null || _savedDirectory != directory)
        {
            _saved = Load(directory);
            _savedDirectory = directory;
        }
        return _saved;
    }

    /// <summary>The verdict for <paramref name="kind"/> with this Office version and license (<paramref name="fingerprint"/>).</summary>
    public static Result Get(string directory, OfficeKind kind, string fingerprint)
    {
        lock (Gate)
        {
            _directory = directory;
            var now = DateTime.UtcNow;
            if (Memory.TryGetValue(kind, out var known) && known.Fingerprint == fingerprint
                && now - known.At < (known.Works ? WorksFor : BrokenFor))
                return Of(known);
            var saved = Saved(directory).GetValueOrDefault(kind.ToString());
            return saved is not null && saved.Fingerprint == fingerprint && !saved.Works && now - saved.At < SavedFor
                ? Of(saved) : new Result(Verdict.Unknown);
        }
    }

    /// <param name="persist">Also save a Broken result in the engine data folder (Office is not activated).</param>
    public static void Record(string directory, OfficeKind kind, string fingerprint, bool works, string? key, string? detail, bool persist)
    {
        lock (Gate)
        {
            _directory = directory;
            var entry = new Entry { Fingerprint = fingerprint, At = DateTime.UtcNow, Works = works, Key = key, Detail = detail };
            Memory[kind] = entry;
            var saved = Saved(directory);
            if (persist && !works)
                saved[kind.ToString()] = entry;
            else if (!saved.Remove(kind.ToString()))
                return;
            Save(directory, saved);
        }
    }

    /// <summary>Forgets what is in memory, not the saved file: what a new run of Mandarina would know (tests).</summary>
    internal static void ForgetMemory()
    {
        lock (Gate)
        {
            Memory.Clear();
            _saved = null;
        }
    }

    /// <summary>Forgets every result, also the saved ones (Settings → Engines → Check again).</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Memory.Clear();
            _saved = null;
            if (_directory is not null)
                Save(_directory, []);
        }
    }

    private static Result Of(Entry entry) =>
        entry.Works ? new Result(Verdict.Works) : new Result(Verdict.Broken, entry.Key, entry.Detail);

    private static string FileOf(string directory) => Path.Combine(directory, "office-self-test.json");

    private static Dictionary<string, Entry> Load(string directory)
    {
        try
        {
            var path = FileOf(directory);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Save(string directory, Dictionary<string, Entry> entries)
    {
        try
        {
            var path = FileOf(directory);
            if (entries.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a result that can't be saved is just asked again next time
        }
    }
}
