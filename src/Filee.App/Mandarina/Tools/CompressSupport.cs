// Mandarina: what the audio and video compressors have in common: reading a quality off a curve, refusing to overwrite,
// deleting files FFmpeg may still be holding after it was killed, asking ffprobe for one number MediaProbe lacks, and
// reporting progress into one stretch of a longer job.

using System.Globalization;
using Filee.App.Mandarina.Panels;
using Filee.Engines.Infrastructure;

namespace Filee.App.Mandarina.Tools;

internal static class CompressSupport
{
    /// <summary>
    /// The value of a quality curve at <paramref name="quality"/>: straight lines between the knots (sorted by quality),
    /// flat beyond the first and the last one. Monotonic whenever the knots are.
    /// </summary>
    public static double Curve(ReadOnlySpan<(int Quality, double Value)> knots, int quality)
    {
        if (quality <= knots[0].Quality)
            return knots[0].Value;
        for (var i = 1; i < knots.Length; i++)
        {
            if (quality > knots[i].Quality)
                continue;
            var (from, start) = knots[i - 1];
            var (to, end) = knots[i];
            return start + (end - start) * (quality - from) / (to - from);
        }
        return knots[^1].Value;
    }

    /// <summary>FFmpeg always runs with -y, so a path that is taken is refused here instead of being written over.</summary>
    public static void RequireFree(string output)
    {
        if (File.Exists(output))
            throw new PanelException("The output file already exists.") { Key = "panel.compress.error.exists" };
    }

    /// <summary>
    /// Deletes <paramref name="path"/> (nothing happens when it is not there). After a cancel FFmpeg was killed an instant
    /// ago and Windows keeps its files locked until it is really gone, so a refused delete is retried for a couple of seconds.
    /// </summary>
    public static async Task DeleteAsync(string path)
    {
        for (var attempt = 0; attempt < 40 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }

    /// <summary>Deletes every file whose name starts with <paramref name="prefix"/> (the two-pass logs: "x-0.log", "x-0.log.mbtree", …).</summary>
    public static async Task DeleteMatchingAsync(string prefix)
    {
        var folder = Path.GetDirectoryName(prefix);
        if (folder is null || !Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, Path.GetFileName(prefix) + "*").ToList())
            await DeleteAsync(file);
    }

    /// <summary>The bitrate of the first audio stream in bits per second as ffprobe states it; 0 when the file does not say (Matroska often doesn't).</summary>
    public static async Task<long> AudioBitRateAsync(MediaTools media, string file, CancellationToken cancel)
    {
        var result = await ProcessRunner.RunAsync(media.Ffprobe,
            ["-hide_banner", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=bit_rate", "-of", "default=noprint_wrappers=1:nokey=1", file],
            TimeSpan.FromMinutes(1), cancel);
        return result.ExitCode == 0 && long.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)
            ? Math.Max(0, bits)
            : 0;
    }

    /// <summary>A progress that reports into the <paramref name="from"/>..<paramref name="to"/> stretch of <paramref name="parent"/>; null when nobody listens.</summary>
    public static IProgress<double>? Slice(IProgress<double>? parent, double from, double to) =>
        parent is null ? null : new SliceProgress(parent, from, to);

    private sealed class SliceProgress(IProgress<double> parent, double from, double to) : IProgress<double>
    {
        public void Report(double value) => parent.Report(from + (to - from) * Math.Clamp(value, 0, 1));
    }
}
