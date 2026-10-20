// Mandarina: the plan of the JOIN tool, the pure part (no files, no processes): the size, frame rate and length of the
// joined video, and the filter graph that brings every clip to that size and rate (picture scaled and padded, sound at one
// rate, a silent track where a clip has none) and then joins them one after another.

using System.Globalization;
using System.Text;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>The size the joined video is made at.</summary>
public enum JoinSize
{
    /// <summary>The size of the first clip (rounded down to even numbers, as H.264 needs).</summary>
    AsFirst,

    Hd1080,

    Hd720,
}

/// <summary>A clip to join: its file and what the probe found in it.</summary>
public sealed record JoinInput(string FilePath, MediaProbe Probe);

/// <summary>
/// What the join writes: the size, frame rate and length of the result, its inputs (one <c>-i</c> each, in the order joined)
/// and its filter graph. The graph ends with the labels [v] (picture) and [a] (sound), which the panel maps.
/// </summary>
public sealed record JoinPlan(int Width, int Height, double FrameRate, double Duration, IReadOnlyList<string> Inputs, string Graph)
{
    /// <summary>Every clip's sound is brought to this rate and to stereo, so the joins are seamless.</summary>
    private const int SampleRate = 48000;

    /// <summary>The frame rate when the first clip does not say its own.</summary>
    private const double DefaultFrameRate = 30;

    /// <summary>Two clips or more, in the order they are joined.</summary>
    public static JoinPlan Build(IReadOnlyList<JoinInput> clips, JoinSize size)
    {
        if (clips.Count < 2)
            throw new ArgumentException("A join needs at least two clips.", nameof(clips));
        var first = clips[0].Probe;
        var (width, height) = size switch
        {
            JoinSize.Hd1080 => (1920, 1080),
            JoinSize.Hd720 => (1280, 720),
            _ => (Even(first.Width), Even(first.Height)),
        };
        var rate = first.FrameRate > 0 ? first.FrameRate : DefaultFrameRate;

        var chains = new StringBuilder();
        var pairs = new StringBuilder();
        var duration = 0.0;
        for (var i = 0; i < clips.Count; i++)
        {
            var probe = clips[i].Probe;
            duration += probe.Duration;
            // Scaled to fit, padded to the size with black bars, and brought to one frame rate and pixel format.
            chains.Append($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={Number(rate)},format=yuv420p[v_{i}];");
            if (probe.HasAudio)
                chains.Append($"[{i}:a:0]aresample={SampleRate},aformat=sample_fmts=fltp:channel_layouts=stereo[a_{i}];");
            else
                chains.Append($"anullsrc=r={SampleRate}:cl=stereo,atrim=duration={Number(probe.Duration)}[a_{i}];");
            pairs.Append($"[v_{i}][a_{i}]");
        }
        chains.Append($"{pairs}concat=n={clips.Count}:v=1:a=1[v][a]");
        return new JoinPlan(width, height, rate, duration, clips.Select(clip => clip.FilePath).ToList(), chains.ToString());
    }

    /// <summary>The largest even number not above <paramref name="value"/> (at least 2): H.264 cannot take odd sizes.</summary>
    private static int Even(int value) => Math.Max(2, value - value % 2);

    /// <summary>A number as FFmpeg reads it: a dot whatever the culture.</summary>
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
