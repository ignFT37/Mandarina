// Mandarina: what a failed run says in the panel's footer. FFmpeg, the compressors and the file system complain in short English
// (or in whatever language Windows speaks); the causes a person can act on (damaged file, disk full, codec not supported…) are
// recognised and said in the app's language, and what the engine really said goes to the log, where a bug report needs it.
// A message that is not recognised is shown as it is: guessing a cause would hide the real one. The exception is a picture that
// Magick.NET cannot read (HEIC and the rest): its text is a path and a line of source code, so the panel says "damaged or not
// a readable format" (or the more exact cause) and the log keeps Magick's words.

using Filee.Core.Localization;
using ImageMagick;

namespace Filee.App.Mandarina.Panels;

public static class PanelErrors
{
    /// <summary>The words each cause has in FFmpeg's and ffprobe's error text (lower-case). The first cause with a match wins.</summary>
    private static readonly (string Key, string[] Words)[] Causes =
    [
        ("panel.error.no_space", ["no space left on device", "not enough space on the disk", "disk full"]),
        ("panel.error.out_of_memory", ["cannot allocate memory", "out of memory", "not enough memory"]),
        ("panel.error.no_permission", ["permission denied", "access is denied", "operation not permitted", "used by another process"]),
        ("panel.error.not_found", ["no such file or directory", "could not find file", "cannot find the file"]),
        ("panel.error.codec",
        [
            "decoder (codec", "unknown decoder", "decoder not found", "no decoder for", "unsupported codec", "codec not currently supported",
            "unknown encoder", "encoder not found", "could not find tag for codec", "unable to find a suitable output format",
        ]),
        ("panel.error.no_stream", ["does not contain any stream", "matches no streams", "output file is empty"]),
        ("panel.error.damaged",
        [
            "invalid data found when processing input", "moov atom not found", "error while decoding", "corrupt", "truncated",
            "header missing", "invalid sync", "end of file", "partial file", "could not find codec parameters",
        ]),
    ];

    /// <summary>How much of an engine's error text the log keeps (the end of it, where the cause is).</summary>
    private const int DetailLength = 2000;

    /// <summary>The i18n key of the cause that FFmpeg's or ffprobe's error text points to, or null when it is none of the known ones.</summary>
    public static string? Recognize(string engineText)
    {
        var text = engineText.ToLowerInvariant();
        foreach (var (key, words) in Causes)
        {
            if (words.Any(word => text.Contains(word, StringComparison.Ordinal)))
                return key;
        }
        return null;
    }

    /// <summary>
    /// The exception for a failed FFmpeg or ffprobe run. Its message is the short English summary (a tool that retries without
    /// an option the build lacks reads it), the key says the cause in the user's language, and the detail keeps the end of
    /// FFmpeg's own text for the log.
    /// </summary>
    public static PanelException FromEngine(string stderr, string summary, string fallback) => new(summary.Length > 0 ? summary : fallback)
    {
        Key = Recognize(stderr),
        Detail = stderr.Length > DetailLength ? stderr[^DetailLength..] : stderr.Trim(),
    };

    /// <summary>The sentence the user reads for <paramref name="ex"/>: its cause in their language when it is recognised, else the message.</summary>
    public static string Describe(Exception ex, ILocalizer loc)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PanelException { Key: { } key } panel)
            {
                var text = panel.Args.Length == 0 ? loc[key] : loc.Format(key, panel.Args);
                // A generic cause says nothing of which file it is about; a batch can tell. The tool's own sentences name it already.
                return panel.Subject is { Length: > 0 } subject && key.StartsWith("panel.error.", StringComparison.Ordinal)
                    ? loc.Format("panel.error.subject", subject, text)
                    : text;
            }
            if (FileSystemCause(current) is { } cause)
                return loc[cause];
            if (current is MagickException magick)
                return loc[PictureCause(magick)];
        }
        return ex.Message;
    }

    /// <summary>
    /// The i18n key that says in plain words why Magick.NET could not read or write a picture. Its own text names a temporary
    /// path and a line of its source ("insufficient image data in file … @ error/heic.c/ReadHEICImage/1029") and says nothing a
    /// person can act on; the words go to the log.
    /// </summary>
    internal static string PictureCause(MagickException ex) => ex switch
    {
        MagickResourceLimitErrorException or MagickCacheErrorException => "panel.error.out_of_memory",
        MagickMissingDelegateErrorException or MagickPolicyErrorException => "panel.error.codec",
        // A file that cannot be opened for the usual reasons (no permission, gone) says so in its text.
        _ => Recognize(ex.Message) ?? "panel.error.damaged",
    };

    /// <summary>
    /// The exception for a picture Magick.NET could not read or write: the cause in the user's language, the file it is about,
    /// and Magick's own text for the log.
    /// </summary>
    public static PanelException FromPicture(MagickException ex, string? subject = null) => new(ex.Message, ex)
    {
        Key = PictureCause(ex),
        Detail = ex.Message,
        Subject = subject,
    };

    /// <summary>What the engine or the system really said, word for word, for the log.</summary>
    public static string Original(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PanelException { Detail: { Length: > 0 } detail })
                return detail;
        }
        return ex.Message;
    }

    /// <summary>The causes the file system reports as exceptions: no permission, nothing there, the disk full, a file in use.</summary>
    internal static string? FileSystemCause(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "panel.error.no_permission",
        FileNotFoundException or DirectoryNotFoundException => "panel.error.not_found",
        OutOfMemoryException => "panel.error.out_of_memory",
        IOException io when IsWin32(io, 112) || IsWin32(io, 39) => "panel.error.no_space",  // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        IOException io when IsWin32(io, 32) || IsWin32(io, 33) => "panel.error.no_permission", // sharing and lock violations
        _ => null,
    };

    private static bool IsWin32(IOException ex, int code) => ex.HResult == unchecked((int)(0x80070000u | (uint)code));
}
