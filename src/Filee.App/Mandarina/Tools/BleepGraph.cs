// Mandarina: the FFmpeg graph of the BLEEP tool, apart from the panel so the tests can read it without running FFmpeg. The file is
// cut at the edges of every marked range and the pieces are joined again: the untouched pieces go through as they are, and each
// marked piece becomes a 1 kHz tone with the voice crossfaded in and out over a few milliseconds at its two ends. Cutting
// (atrim) is exact to the sample, unlike a volume filter with an enable window, which switches only between audio frames
// (up to 93 ms late for FLAC), so none of a covered word leaks out.

using System.Text;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

internal static class BleepGraph
{
    /// <summary>The pitch of the bleep, as on television.</summary>
    internal const double ToneHertz = 1000;

    /// <summary>The voice fades into the tone and out of it over this long (seconds): no click, and nothing of it left after the ramp.</summary>
    private const double Ramp = 0.004;

    /// <summary>Ranges closer than this (seconds) are one range; a range shorter than this is nothing; an edge this close to the file's is the file's.</summary>
    private const double Tolerance = 0.001;

    /// <summary>
    /// <paramref name="ranges"/> in order, with the handles put the right way round, kept inside the file
    /// (<paramref name="duration"/> 0 = unknown) and every range that overlaps or touches another joined to it.
    /// </summary>
    internal static List<WaveRange> Normalize(IEnumerable<WaveRange> ranges, double duration)
    {
        List<WaveRange> inside = [];
        foreach (var range in ranges)
        {
            var start = Math.Max(0, Math.Min(range.Start, range.End));
            var end = Math.Max(range.Start, range.End);
            if (duration > 0)
                end = Math.Min(duration, end);
            // The pieces must tile the file with no sliver left out between them: an edge this close to the file's is the file's.
            if (start < Tolerance)
                start = 0;
            if (duration > 0 && end > duration - Tolerance)
                end = duration;
            if (end - start >= Tolerance)
                inside.Add(new WaveRange(start, end));
        }

        List<WaveRange> merged = [];
        foreach (var range in inside.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + Tolerance)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
            else
                merged.Add(range);
        }
        return merged;
    }

    /// <summary>
    /// The filter graph from <c>[0:a]</c> to <c>[out]</c> that covers <paramref name="ranges"/> with a tone of
    /// <paramref name="volume"/> (0..1, of full scale); empty when no range lies inside the file. The audio keeps its channels and
    /// sample rate (<paramref name="sampleRate"/> is the file's: the timeline is counted in samples, so a file whose first frame
    /// does not start at zero, as MP3 often does, is cut where the waveform shows it).
    /// </summary>
    internal static string Build(IReadOnlyList<WaveRange> ranges, double duration, int sampleRate, double volume)
    {
        var covered = Normalize(ranges, duration);
        if (covered.Count == 0)
            return "";

        // The pieces in time order, from 0 to the end of the file with no gap: what lies between the ranges, and the ranges.
        List<(double Start, double? End, bool Tone)> pieces = [];
        var position = 0.0;
        foreach (var range in covered)
        {
            if (range.Start > position)
                pieces.Add((position, range.Start, false));
            pieces.Add((range.Start, range.End, true));
            position = range.End;
        }
        if (duration <= 0 || position < duration)
            pieces.Add((position, null, false));

        var graph = new StringBuilder("[0:a]");
        graph.Append(sampleRate > 0 ? "asetpts=N/SR/TB" : "asetpts=PTS-STARTPTS");
        graph.Append(",aformat=sample_fmts=fltp,asplit=").Append(pieces.Count);
        for (var i = 0; i < pieces.Count; i++)
            graph.Append("[o").Append(i).Append(']');
        graph.Append(';');

        var level = AudioEncoding.Number(Math.Clamp(volume, 0, 1));
        for (var i = 0; i < pieces.Count; i++)
        {
            var (start, end, tone) = pieces[i];
            graph.Append("[o").Append(i).Append("]atrim=start=").Append(AudioEncoding.Number(start));
            if (end is { } stop)
                graph.Append(":end=").Append(AudioEncoding.Number(stop));
            graph.Append(",asetpts=PTS-STARTPTS");
            if (tone && end is { } until)
            {
                // t counts from the start of this piece. g goes 0 → 1 over the ramp, stays at 1, and goes back to 0 at the end;
                // the output is the voice plus g times (tone minus voice).
                var length = until - start;
                var ramp = AudioEncoding.Number(Math.Min(Ramp, length / 4));
                graph.Append(",aeval=exprs='val(ch)+(").Append(level).Append("*sin(2*PI*").Append(AudioEncoding.Number(ToneHertz))
                    .Append("*t)-val(ch))*max(0,min(min(1,t/").Append(ramp).Append("),(").Append(AudioEncoding.Number(length))
                    .Append("-t)/").Append(ramp).Append("))':c=same,aformat=sample_fmts=fltp");
            }
            graph.Append("[s").Append(i).Append("];");
        }

        for (var i = 0; i < pieces.Count; i++)
            graph.Append("[s").Append(i).Append(']');
        graph.Append("concat=n=").Append(pieces.Count).Append(":v=0:a=1[out]");
        return graph.ToString();
    }
}
