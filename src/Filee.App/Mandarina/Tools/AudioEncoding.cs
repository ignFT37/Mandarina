// Mandarina: what the audio tools that run a filter (CHANNELS, BLEEP) share when they write their result: how a number goes into
// an FFmpeg filter, and the encoder options that keep the file's format and quality.

using System.Globalization;
using Filee.App.Mandarina.Panels;
using Filee.Engines.Infrastructure;

namespace Filee.App.Mandarina.Tools;

internal static class AudioEncoding
{
    /// <summary>A number as a filter reads it: a decimal point whatever the culture, six decimals at most, never an exponent.</summary>
    internal static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// The encoder options that write <paramref name="source"/>'s own format again at high quality (the ones of Trim audio), plus what a
    /// filter would otherwise cost the file: after one the samples are floating point, and FLAC then picks its bit depth on its own
    /// (a 16-bit file can come out as 24-bit, a bigger file for nothing). FLAC is told the depth of the original.
    /// </summary>
    internal static async Task<string[]> ArgumentsAsync(MediaTools media, string source, MediaProbe probe, CancellationToken cancel)
    {
        var extension = Path.GetExtension(source);
        var encoder = TrimAudioPanel.EncoderFor(extension, probe.AudioCodec);
        if (!extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
            return encoder;
        return await BitsPerSampleAsync(media, source, cancel) switch
        {
            > 16 => [.. encoder, "-sample_fmt", "s32"],      // FLAC writes 24 bits from 32-bit samples
            > 0 => [.. encoder, "-sample_fmt", "s16"],
            _ => encoder,
        };
    }

    /// <summary>The bit depth of the first audio stream (16, 24, …); 0 when ffprobe does not say.</summary>
    private static async Task<int> BitsPerSampleAsync(MediaTools media, string source, CancellationToken cancel)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(media.Ffprobe,
                ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=bits_per_raw_sample", "-of", "csv=p=0", source],
                TimeSpan.FromMinutes(1), cancel);
            return result.ExitCode == 0 && int.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) ? bits : 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return 0;
        }
    }
}
