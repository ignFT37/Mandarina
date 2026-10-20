// Mandarina: burns subtitles into a video: FFmpeg's `subtitles` filter (libass) draws the cues on every frame, the video is
// encoded again as H.264 (CRF 18, high quality) in an MP4 and the sound is kept as it is when MP4 can hold it.

using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>How big the burned-in text is: the font size on libass's 288-line script, which scales with the video.</summary>
public enum SubtitleSize
{
    Small = 14,
    Medium = 18,
    Large = 24,
}

public static class SubtitleBurner
{
    /// <summary>Audio codecs an MP4 takes without being encoded again.</summary>
    private static readonly HashSet<string> CopyableAudio = ["aac", "mp3", "ac3", "eac3", "alac"];

    /// <summary>
    /// The filter graph: the cue file and the style (white text with a black outline at the bottom, over a font Windows has).
    /// The style's commas sit inside the quotes, so the graph parser leaves them alone.
    /// </summary>
    internal static string Graph(string cueFile, SubtitleSize size)
    {
        var style = $"FontName=Arial,FontSize={(int)size},Outline=2,Shadow=0,MarginV=14";
        return $"subtitles=filename={WhisperEngine.Quote(cueFile)}:force_style={WhisperEngine.Quote(style)}";
    }

    /// <summary>
    /// Writes <paramref name="output"/> (an MP4) from <paramref name="video"/> with <paramref name="cues"/> drawn on it. The
    /// cues are cut to what fits the screen first. Throws <see cref="PanelException"/> with FFmpeg's message when it fails.
    /// </summary>
    public static async Task BurnAsync(MediaTools media, string video, IEnumerable<Cue> cues, string output, SubtitleSize size, MediaProbe probe,
        IProgress<double>? progress, CancellationToken cancel)
    {
        var cueFile = OutputPaths.Temp(".srt");
        var graphFile = OutputPaths.Temp(".txt");
        try
        {
            File.WriteAllText(cueFile, Subtitles.ToSrt(Subtitles.Fit(cues)), Subtitles.FileEncoding);
            File.WriteAllText(graphFile, Graph(cueFile, size)); // UTF-8 without a BOM: a user name with an accent is in the paths
            List<string> args = ["-i", video, "-map", "0:v:0", "-/vf", graphFile, "-c:v", "libx264", "-crf", "18", "-preset", "medium", "-pix_fmt", "yuv420p"];
            if (probe.HasAudio)
            {
                args.AddRange(["-map", "0:a:0"]);
                args.AddRange(probe.AudioCodec is { } codec && CopyableAudio.Contains(codec) ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"]);
            }
            args.AddRange(["-movflags", "+faststart", output]);
            await media.RunAsync(args, probe.Duration, progress, cancel);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        finally
        {
            MediaTools.TryDelete(cueFile);
            MediaTools.TryDelete(graphFile);
        }
    }
}
