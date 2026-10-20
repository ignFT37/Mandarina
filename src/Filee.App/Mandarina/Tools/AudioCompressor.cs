// Mandarina: COMPRESS for audio (FFmpeg). Lossless files (wav, aiff, flac) are saved as FLAC at its highest
// compression level with the same channels, sample rate and bit depth, so nothing is lost; lossy ones (mp3, m4a, ogg, opus)
// are re-encoded in their own codec at a bitrate taken from the quality or from the wanted size, never above the source's.
// Tags travel with the sound and so does the cover art, in the containers that can hold one.

using System.Globalization;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

public static partial class AudioCompressor
{
    /// <summary>Tries to land a wanted size: the first encode and two corrections made from what it really weighed.</summary>
    private const int MaxAttempts = 3;

    /// <summary>Share of the wanted size the bitrate is planned for; tags, cover art and the encoder's drift take the rest.</summary>
    private const double Margin = 0.96;

    // Quality (1..100) → kbps for mp3, m4a and ogg, in straight lines between these points. The presets are knots
    // (Light 85, Balanced 70, Strong 50, Maximum 35); 10 is where the slider starts and 256 is as high as lossy audio pays off.
    private static readonly (int Quality, double Value)[] StandardKnots = [(10, 48), (35, 80), (50, 96), (70, 128), (85, 192), (100, 256)];

    // The same for Opus, which sounds as good at about two thirds of the bitrate.
    private static readonly (int Quality, double Value)[] OpusKnots = [(10, 24), (35, 40), (50, 56), (70, 80), (85, 112), (100, 160)];

    // MP3 only has these constant bitrates: MPEG-1 layer III from 32 kHz up, MPEG-2 (24 kHz and under) for the low ones.
    private static readonly int[] Mpeg1Rates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    private static readonly int[] Mpeg2Rates = [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    /// <summary>The extension the compressed file gets: the source's, except WAV, AIFF and FLAC which become ".flac".</summary>
    public static partial string OutputExtension(string source)
    {
        var extension = Path.GetExtension(source);
        return IsLossless(extension) ? ".flac" : extension;
    }

    public static async partial Task<string> CompressAsync(MediaTools media, string source, string output, CompressOptions options,
        IProgress<double>? progress, CancellationToken cancel)
    {
        CompressSupport.RequireFree(output);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (!IsLossless(extension) && !IsLossy(extension))
            throw new PanelException($"Audio files of type \"{extension}\" cannot be compressed.") { Key = "panel.compress.error.unsupported", Args = [extension] };
        var probe = await media.ProbeAsync(source, cancel);
        if (!probe.HasAudio)
            throw new PanelException("This file has no audio.") { Key = "panel.compress.error.no_audio" };
        try
        {
            if (IsLossless(extension))
                await CompressLosslessAsync(media, source, output, probe, progress, cancel);
            else
                await CompressLossyAsync(media, source, output, extension, probe, options, progress, cancel);
        }
        catch
        {
            await CompressSupport.DeleteAsync(output);
            throw;
        }
        return output;
    }

    /// <summary>The bitrate in kbps a lossy format gets at <paramref name="quality"/> (1..100), before it is held to the source's own.</summary>
    internal static int QualityKbps(string extension, int quality) =>
        (int)Math.Floor(CompressSupport.Curve(extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) ? OpusKnots : StandardKnots,
            Math.Clamp(quality, 1, 100)));

    /// <summary>
    /// What FFmpeg is asked for to get as near as the codec can to <paramref name="wanted"/> kbps: the bitrate, and the sample
    /// rate to resample to (0 = keep it). MP3 is snapped down to a real constant bitrate, and goes below 32 kbps only by
    /// halving the sample rate (MPEG-2); Vorbis cannot be steered below about 46 kbps for stereo.
    /// </summary>
    internal static (int Kbps, int Resample) Settle(string extension, double wanted, int channels, int sampleRate)
    {
        var kbps = (int)Math.Clamp(Math.Floor(wanted), 16, 320);
        return extension.ToLowerInvariant() switch
        {
            ".mp3" => Mp3Rate(kbps, sampleRate),
            ".ogg" => (Math.Max(kbps, channels > 1 ? 48 : 32), 0),
            _ => (kbps, 0),
        };
    }

    private static (int Kbps, int Resample) Mp3Rate(int kbps, int sampleRate)
    {
        var rate = sampleRate > 0 ? sampleRate : 44100;
        if (rate > 24000 && kbps >= 32)
            return (SnapDown(Mpeg1Rates, kbps), 0);
        return (SnapDown(Mpeg2Rates, kbps), rate > 24000 ? rate / 2 : 0);
    }

    private static int SnapDown(int[] rates, int kbps) => rates.LastOrDefault(rate => rate <= kbps, rates[0]);

    private static async Task CompressLosslessAsync(MediaTools media, string source, string output, MediaProbe probe,
        IProgress<double>? progress, CancellationToken cancel)
    {
        List<string> encoder = ["-c:a", "flac", "-compression_level", "12"];

        // FLAC keeps up to 24 bits: with more than 16 in the source, 32-bit samples are asked for so that no FFmpeg drops them to 16.
        if (probe.AudioCodec is { } codec && codec.StartsWith("pcm_", StringComparison.Ordinal)
            && (codec.Contains("24", StringComparison.Ordinal) || codec.Contains("32", StringComparison.Ordinal) || codec.Contains("64", StringComparison.Ordinal)))
            encoder.AddRange(["-sample_fmt", "s32"]);
        await EncodeAsync(media, source, output, encoder, probe.Duration, progress, cancel);
    }

    private static async Task CompressLossyAsync(MediaTools media, string source, string output, string extension, MediaProbe probe,
        CompressOptions options, IProgress<double>? progress, CancellationToken cancel)
    {
        var targeted = options.Mode == CompressMode.TargetSize;
        if (targeted && options.TargetBytes <= 0)
            throw new PanelException("The wanted size must be more than zero.") { Key = "panel.compress.error.no_target" };
        if (targeted && probe.Duration <= 0)
            throw new PanelException("The length of this file is unknown, so its size cannot be planned.") { Key = "panel.compress.error.no_length" };

        // The whole file's average, cover art included: a safe ceiling, since re-encoding must not raise the bitrate.
        var sourceKbps = probe.Duration > 0 ? new FileInfo(source).Length * 8.0 / probe.Duration / 1000 : 0;
        var wanted = targeted ? options.TargetBytes * 8.0 / probe.Duration / 1000 * Margin : QualityKbps(extension, options.Quality);
        if (sourceKbps > 0)
            wanted = Math.Min(wanted, sourceKbps);

        var (kbps, resample) = Settle(extension, wanted, probe.Channels, probe.SampleRate);
        for (var attempt = 1; ; attempt++)
        {
            // Only the first try reports progress: a correction would take the bar back to the start.
            await EncodeAsync(media, source, output, Encoder(extension, kbps, resample), probe.Duration, attempt == 1 ? progress : null, cancel);
            if (!targeted || attempt == MaxAttempts)
                return;
            var size = new FileInfo(output).Length;
            if (size <= options.TargetBytes)
                return;

            // Too big: plan again from what this try really weighed (a little under, to land below the target).
            var (lower, lowerResample) = Settle(extension, kbps * (double)options.TargetBytes / size * 0.97, probe.Channels, probe.SampleRate);
            if (lower >= kbps)
                return;     // already as low as the codec goes: this is the best it can do
            (kbps, resample) = (lower, lowerResample);
        }
    }

    private static List<string> Encoder(string extension, int kbps, int resample)
    {
        List<string> encoder = extension switch
        {
            ".mp3" => ["-c:a", "libmp3lame"],
            ".m4a" => ["-c:a", "aac"],
            ".ogg" => ["-c:a", "libvorbis"],
            _ => ["-c:a", "libopus"],
        };
        encoder.AddRange(["-b:a", $"{kbps}k"]);
        if (resample > 0)
            encoder.AddRange(["-ar", resample.ToString(CultureInfo.InvariantCulture)]);
        return encoder;
    }

    /// <summary>Runs FFmpeg with the first audio stream, the tags and (when the output holds one) the cover art.</summary>
    private static async Task EncodeAsync(MediaTools media, string source, string output, List<string> encoder, double duration,
        IProgress<double>? progress, CancellationToken cancel)
    {
        if (HoldsCover(Path.GetExtension(output)))
        {
            try
            {
                await media.RunAsync(Arguments(source, output, encoder, cover: true), duration, progress, cancel);
                return;
            }
            catch (PanelException)
            {
                // A cover FFmpeg cannot carry over (an odd image format, say) is not worth failing for: the sound alone is
                // saved, over whatever the first try wrote.
            }
        }
        await media.RunAsync(Arguments(source, output, encoder, cover: false), duration, progress, cancel);
    }

    private static List<string> Arguments(string source, string output, List<string> encoder, bool cover)
    {
        List<string> args = ["-i", source, "-map", "0:a:0"];
        if (cover)
            args.AddRange(["-map", "0:v?", "-c:v", "copy"]);    // a cover is a video stream with the "attached picture" flag
        args.AddRange(["-map_metadata", "0"]);
        args.AddRange(encoder);
        args.Add(output);
        return args;
    }

    private static bool IsLossless(string extension) => extension.ToLowerInvariant() is ".wav" or ".aiff" or ".aif" or ".flac";

    private static bool IsLossy(string extension) => extension.ToLowerInvariant() is ".mp3" or ".m4a" or ".ogg" or ".opus";

    /// <summary>The containers that carry a picture as a stream. Ogg and Opus keep theirs in a tag FFmpeg's muxer won't write.</summary>
    private static bool HoldsCover(string extension) => extension.ToLowerInvariant() is ".mp3" or ".m4a" or ".flac";
}
