// Mandarina: the FFmpeg command that cuts one stretch out of a video. Trim runs it once, Split once per clip. The stretch is
// first snapped to whole frames (the first frame at or after the start, up to the frame before the end), so the result has
// exactly those frames, starts on its first one with no gap, and consecutive clips of a split neither overlap nor leave a
// frame out. Precise mode re-encodes; "no re-encode" copies the streams and can only start on a key frame.

using System.Globalization;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>A stretch of a video in whole frames: the time of its first frame, how long it lasts and how many frames that is.</summary>
internal readonly record struct VideoCutSpan(double Start, double Length, int Frames);

internal static class VideoCut
{
    /// <summary>The shortest a re-encoded cut may last, in seconds (one frame at 1000 fps): never zero or less, whatever the margin takes off.</summary>
    private const double MinLength = 0.001;

    /// <summary>The frames of <paramref name="probe"/>'s video that lie from <paramref name="start"/> up to <paramref name="end"/>.</summary>
    public static VideoCutSpan Span(double start, double end, MediaProbe probe)
    {
        var first = FrameStep.FrameAt(start, probe.FrameRate, probe.Duration);
        var after = Math.Max(first + 1, FrameStep.EndFrame(end, probe.FrameRate, probe.Duration));
        return new VideoCutSpan(FrameStep.TimeOf(first, probe.FrameRate), FrameStep.TimeOf(after - first, probe.FrameRate), after - first);
    }

    /// <summary>FFmpeg's arguments to write <paramref name="span"/> of <paramref name="source"/> to <paramref name="output"/>.</summary>
    public static List<string> Arguments(string source, string output, VideoCutSpan span, MediaProbe probe, bool copy)
    {
        // Decoding seeks to just before the first frame and drops what comes before it, which starts the result on that very
        // frame. Copying cannot decode: it starts on the key frame at or before the time, so the time is nudged forward to keep
        // a key frame that sits exactly on the cut (and not the one a whole group of pictures earlier).
        var seek = copy ? (span.Start > 0 ? span.Start + FrameStep.SeekMargin : 0) : Math.Max(0, span.Start - FrameStep.SeekMargin);
        // Decoding keeps the frames from the seek time up to seek + length, and the clip that follows in a split seeks to ITS start
        // minus the same margin, so both agree on the instant between them. A clip that starts at 0 cannot seek before 0: its margin
        // comes off its length instead. (A frame lying in that margin, as one can in a variable-frame-rate video or when timestamps are
        // rounded, would otherwise be in both clips.)
        var length = copy ? span.Length : Math.Max(MinLength, span.Length - FrameStep.SeekMargin + (span.Start - seek));
        List<string> args =
        [
            "-ss", Seconds(seek), "-i", source, "-t", Seconds(length),
            "-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0",
        ];
        // Only the MP4 family takes these two options (and wants hvc1 for HEVC to play in Apple's players).
        var mp4 = Path.GetExtension(output).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";
        if (copy)
        {
            args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
        }
        else
        {
            if (string.Equals(probe.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(["-c:v", "libx265", "-crf", "22"]);
                if (mp4)
                    args.AddRange(["-tag:v", "hvc1"]);
            }
            else
            {
                args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"]);
            }
            // 4:2:0 cannot be written with an odd width or height: drop the odd last row or column.
            if (probe.Width % 2 != 0 || probe.Height % 2 != 0)
                args.AddRange(["-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2"]);
            if (probe.HasAudio)
                args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
            if (mp4)
                args.AddRange(["-movflags", "+faststart"]);
        }
        args.Add(output);
        return args;
    }

    /// <summary>
    /// Deletes what a cut that failed or was cancelled left behind. FFmpeg may still hold the file for a moment after it is
    /// killed, so a few tries are made before giving up.
    /// </summary>
    public static Task DeleteFileAsync(string path) => DeleteAsync(() => File.Delete(path));

    /// <summary>Deletes a folder (and the clips in it) the way <see cref="DeleteFileAsync"/> deletes a file.</summary>
    public static Task DeleteFolderAsync(string folder) => DeleteAsync(() =>
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    });

    private static async Task DeleteAsync(Action delete)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>Seconds to the microsecond (FFmpeg's own precision), whatever the culture.</summary>
    internal static string Seconds(double value) => Math.Max(0, value).ToString("0.######", CultureInfo.InvariantCulture);
}
