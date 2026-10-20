// Mandarina: COMPRESS for video (FFmpeg). The first video stream becomes H.264 (x264, 8-bit 4:2:0, which every
// player takes) and the audio AAC, in the source's own container with the tags kept. How much is lost comes from a quality
// (a constant-quality encode) or from a wanted size (a two-pass encode planned from the length); a maximum height can shrink
// the picture, never enlarge it.

using System.Globalization;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

public static partial class VideoCompressor
{
    /// <summary>Share of the wanted size the bitrate is planned for; the container and the encoder's drift take the rest.</summary>
    private const double Margin = 0.96;

    /// <summary>How far over the wanted size a result may be before the second pass is run again at a lower bitrate.</summary>
    private const double Tolerance = 1.08;

    /// <summary>Repeats of the second pass, each at 10 % less video bitrate.</summary>
    private const int MaxRetries = 2;

    /// <summary>The lowest video bitrate planned for a wanted size, in kbps.</summary>
    private const int MinVideoKbps = 100;

    /// <summary>The first pass takes this share of the progress bar and the second the rest.</summary>
    private const double FirstPassShare = 0.45;

    // Quality (1..100) → x264 CRF (lower keeps more), in straight lines between these points. The presets are knots
    // (Light 85, Balanced 70, Strong 50, Maximum 35); 10 is where the slider starts. CRF 18 is the usual "visually lossless".
    private static readonly (int Quality, double Value)[] CrfKnots = [(10, 40), (35, 34), (50, 30), (70, 26), (85, 22), (100, 18)];

    /// <summary>The audio of the result: copied when it is AAC already and no bigger than wanted, otherwise AAC again.</summary>
    /// <param name="Present">False for a video without sound: no track is made up.</param>
    /// <param name="CopyFirst">The first track is copied untouched.</param>
    /// <param name="EncodeKbps">Bitrate of every track that is encoded.</param>
    /// <param name="BudgetKbps">What the audio takes of a wanted size: the encoded bitrate, or the copied track's own.</param>
    private readonly record struct AudioPlan(bool Present, bool CopyFirst, int EncodeKbps, int BudgetKbps);

    public static async partial Task<string> CompressAsync(MediaTools media, string source, string output, CompressOptions options,
        IProgress<double>? progress, CancellationToken cancel)
    {
        CompressSupport.RequireFree(output);
        var probe = await media.ProbeAsync(source, cancel);
        if (!probe.HasVideo)
            throw new PanelException("This file has no video.") { Key = "panel.compress.error.no_video" };
        var targeted = options.Mode == CompressMode.TargetSize;
        if (targeted && options.TargetBytes <= 0)
            throw new PanelException("The wanted size must be more than zero.") { Key = "panel.compress.error.no_target" };
        if (targeted && probe.Duration <= 0)
            throw new PanelException("The length of this file is unknown, so its size cannot be planned.") { Key = "panel.compress.error.no_length" };

        // Bits per second the whole result may have, never more than the source has: a bigger wish cannot buy a better picture.
        var budgetKbps = 0.0;
        if (targeted)
        {
            budgetKbps = options.TargetBytes * 8.0 / probe.Duration / 1000 * Margin;
            budgetKbps = Math.Min(budgetKbps, new FileInfo(source).Length * 8.0 / probe.Duration / 1000);
        }
        var audio = await PlanAudioAsync(media, source, probe, options, budgetKbps, cancel);
        var passLog = targeted ? Path.ChangeExtension(OutputPaths.Temp(".pass"), null) : null;
        try
        {
            if (passLog is null)
                await media.RunAsync(OnePass(source, output, probe, options, audio), probe.Duration, progress, cancel);
            else
                await TwoPassesAsync(media, source, output, probe, options, audio, budgetKbps, passLog, progress, cancel);
        }
        catch
        {
            await CompressSupport.DeleteAsync(output);
            throw;
        }
        finally
        {
            // The statistics x264 keeps between the passes are ours alone (big, in the temp folder): they go whatever happened.
            if (passLog is not null)
                await CompressSupport.DeleteMatchingAsync(passLog);
        }
        return output;
    }

    /// <summary>The x264 CRF a quality (1..100) is encoded at; 18 for 100, 22 for 85, 26 for 70, 30 for 50, 34 for 35, 40 for 10.</summary>
    internal static double Crf(int quality) => Math.Round(CompressSupport.Curve(CrfKnots, Math.Clamp(quality, 1, 100)), 1);

    private static async Task<AudioPlan> PlanAudioAsync(MediaTools media, string source, MediaProbe probe, CompressOptions options,
        double budgetKbps, CancellationToken cancel)
    {
        if (!probe.HasAudio)
            return new AudioPlan(false, false, 0, 0);

        // 128 kbps (96 below quality 50). With a size to meet, a fifth of what the whole file may have, so that sound
        // does not eat a small budget, but between 48 and 128.
        var wanted = options.Mode == CompressMode.TargetSize ? (int)Math.Clamp(budgetKbps / 5, 48, 128) : (options.Quality < 50 ? 96 : 128);
        if (probe.AudioCodec == "aac" && await CompressSupport.AudioBitRateAsync(media, source, cancel) is var bits and > 0 && bits <= wanted * 1000L)
            return new AudioPlan(true, true, wanted, (int)Math.Ceiling(bits / 1000.0));
        return new AudioPlan(true, false, wanted, wanted);
    }

    private static List<string> OnePass(string source, string output, MediaProbe probe, CompressOptions options, AudioPlan audio)
    {
        var crf = Crf(options.Quality).ToString("0.#", CultureInfo.InvariantCulture);
        return Command(source, output, probe, options, audio, ["-crf", crf]);
    }

    /// <summary>
    /// Two passes at one video bitrate: the first only studies the picture (no sound, nothing written), the second
    /// spends the bits where they are needed. The result is checked: more than 8 % over the wanted size and the second
    /// pass is repeated at 10 % less, twice at most.
    /// </summary>
    private static async Task TwoPassesAsync(MediaTools media, string source, string output, MediaProbe probe, CompressOptions options,
        AudioPlan audio, double budgetKbps, string passLog, IProgress<double>? progress, CancellationToken cancel)
    {
        var videoKbps = (int)Math.Clamp(budgetKbps - audio.BudgetKbps, MinVideoKbps, 1_000_000);

        List<string> first = ["-i", source];
        first.AddRange(VideoArguments(probe, options));
        first.AddRange(["-b:v", $"{videoKbps}k", "-pass", "1", "-passlogfile", passLog, "-an", "-f", "null", OperatingSystem.IsWindows() ? "NUL" : "/dev/null"]);
        await media.RunAsync(first, probe.Duration, CompressSupport.Slice(progress, 0, FirstPassShare), cancel);

        for (var retry = 0; ; retry++)
        {
            var second = Command(source, output, probe, options, audio, ["-b:v", $"{videoKbps}k", "-pass", "2", "-passlogfile", passLog]);
            await media.RunAsync(second, probe.Duration, retry == 0 ? CompressSupport.Slice(progress, FirstPassShare, 1) : null, cancel);
            if (retry == MaxRetries || LowerBitrate(videoKbps, new FileInfo(output).Length, options.TargetBytes) is not { } lower)
                return;
            videoKbps = lower;
        }
    }

    /// <summary>
    /// The video bitrate to repeat the second pass at (10 % lower), or null when the result is good enough (no more than
    /// 8 % over the wanted size) or nothing can be gained (the bitrate is already at its floor).
    /// </summary>
    internal static int? LowerBitrate(int videoKbps, long size, long target)
    {
        if (size <= target * Tolerance)
            return null;
        var lower = Math.Max(MinVideoKbps, (int)(videoKbps * 0.9));
        return lower < videoKbps ? lower : null;
    }

    /// <summary>The whole command that writes the result; <paramref name="rate"/> says how the video is paced (a CRF, or a bitrate and its pass).</summary>
    private static List<string> Command(string source, string output, MediaProbe probe, CompressOptions options, AudioPlan audio, string[] rate)
    {
        List<string> args = ["-i", source];
        args.AddRange(VideoArguments(probe, options));
        args.AddRange(rate);
        if (audio.Present)
        {
            args.AddRange(["-map", "0:a?", "-c:a", "aac", "-b:a", $"{audio.EncodeKbps}k"]);
            if (audio.CopyFirst)
                args.AddRange(["-c:a:0", "copy"]);
        }
        args.AddRange(["-map_metadata", "0"]);
        if (Path.GetExtension(output).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v")
            args.AddRange(["-movflags", "+faststart"]);     // the index first, so it plays while it downloads
        args.Add(output);
        return args;
    }

    /// <summary>The picture half of the command: the first video stream, scaled when needed, as x264 4:2:0.</summary>
    private static List<string> VideoArguments(MediaProbe probe, CompressOptions options)
    {
        List<string> args = ["-map", "0:v:0"];
        if (ScaleFilter(probe, options) is { } scale)
            args.AddRange(["-vf", scale]);
        args.AddRange(["-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p"]);
        return args;
    }

    /// <summary>
    /// The scale filter, or null when the picture can go through as it is. FFmpeg turns a phone's rotation upright while
    /// decoding, so <c>ih</c> is the height as it is seen. "-2" keeps the proportions with an even width, and the height
    /// is made even too (4:2:0 needs both, and some sources are odd).
    /// </summary>
    private static string? ScaleFilter(MediaProbe probe, CompressOptions options)
    {
        var cap = Math.Max(0, options.MaxSize) / 2 * 2;
        var shrink = cap > 0 && (probe.Height == 0 || probe.Height > cap);
        var odd = probe.Width % 2 != 0 || probe.Height % 2 != 0;
        if (!shrink && !odd)
            return null;
        return shrink ? $"scale=-2:'min({cap},trunc(ih/2)*2)'" : "scale=-2:'trunc(ih/2)*2'";
    }
}
