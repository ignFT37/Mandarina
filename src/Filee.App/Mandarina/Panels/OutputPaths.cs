// Mandarina: where a panel tool saves its result. A tool writes a new copy beside the original and never touches it:
// "foto.jpg" → "foto recortado.jpg", and "foto recortado 2.jpg", "… 3.jpg" when that name is taken.

namespace Filee.App.Mandarina.Panels;

public static class OutputPaths
{
    /// <summary>
    /// "folder\name suffix.ext" beside <paramref name="source"/>, or with " 2", " 3", … after the suffix while the name is
    /// taken. <paramref name="extension"/> is ".png"-style (the source's own when null).
    /// </summary>
    public static string Beside(string source, string suffix, string? extension = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(source);
        extension ??= Path.GetExtension(source);
        if (extension.Length > 0 && extension[0] != '.')
            extension = "." + extension;
        return FileActions.FreePath(folder, $"{stem} {suffix}".TrimEnd() + extension);
    }

    /// <summary>
    /// One path per extension beside <paramref name="source"/>, all with the same name and none of them taken: "clip.txt" and
    /// "clip.srt", or "clip 2.txt" and "clip 2.srt" when either exists (a pair that belongs together keeps its numbers together).
    /// <paramref name="suffix"/> goes after the name when it is not empty. Extensions are ".txt"-style.
    /// </summary>
    public static string[] BesideAll(string source, string suffix, params string[] extensions)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        var name = $"{Path.GetFileNameWithoutExtension(source)} {suffix}".TrimEnd();
        for (var n = 1; ; n++)
        {
            var stem = n == 1 ? name : $"{name} {n}";
            var paths = extensions.Select(extension => Path.Combine(folder, stem + extension)).ToArray();
            if (paths.All(path => !File.Exists(path) && !Directory.Exists(path)))
                return paths;
        }
    }

    /// <summary>"folder\name suffix" for a folder a tool fills (frames, clips, pages); " 2", " 3", … when taken. Not created.</summary>
    public static string FolderBeside(string source, string suffix)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        var name = $"{Path.GetFileNameWithoutExtension(source)} {suffix}".TrimEnd();
        var path = Path.Combine(folder, name);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{name} {n}");
        return path;
    }

    /// <summary>
    /// A free path in a work folder for a temporary file (deleted by the caller). Panels keep their previews and
    /// intermediate files there so nothing is left beside the user's files.
    /// </summary>
    public static string Temp(string extension)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Mandarina", "panels");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Guid.NewGuid().ToString("N")[..12] + (extension.StartsWith('.') ? extension : "." + extension));
    }
}
