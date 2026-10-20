// Mandarina: the FFmpeg arguments of the SPEED tool for video, the pure part (no files, no processes). The picture is
// sped up or slowed down with setpts; the sound with atempo, which only takes factors from 0.5 to 2, so a bigger change
// is a chain of those (4× is two steps of 2) and the pitch of the sound stays where it was.

using System.Globalization;

namespace Filee.App.Mandarina.Tools;

public static class SpeedFilter
{
    /// <summary>The slowest and the fastest speed the panel offers.</summary>
    public const double Slowest = 0.25;

    public const double Fastest = 8;

    /// <summary>Whether a speed changes the video at all: 1× keeps it as it is.</summary>
    public static bool Changes(double factor) => Math.Abs(factor - 1) > 1e-9;

    /// <summary>
    /// The atempo factors, each from 0.5 to 2, whose product is <paramref name="factor"/>: 4 gives 2, 2; 0.25 gives 0.5, 0.5;
    /// 3 gives 2, 1.5; 1 gives none.
    /// </summary>
    public static IReadOnlyList<double> TempoSteps(double factor)
    {
        // Positive and finite, or the loops below would never end.
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "A speed must be a positive number.");
        List<double> steps = [];
        var rest = factor;
        while (rest > 2)
        {
            steps.Add(2);
            rest /= 2;
        }
        while (rest < 0.5)
        {
            steps.Add(0.5);
            rest *= 2;
        }
        if (Math.Abs(rest - 1) > 1e-9)
            steps.Add(rest);
        return steps;
    }

    /// <summary>
    /// The FFmpeg arguments that apply <paramref name="factor"/>: the filter graph (the picture through setpts and, when the
    /// video has sound, the sound through the atempo chain) and the maps of its two outputs, [v] and [a].
    /// </summary>
    public static IReadOnlyList<string> Build(double factor, bool hasAudio)
    {
        if (!Changes(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), "A speed of 1 changes nothing.");
        var picture = $"[0:v:0]setpts=PTS/{Number(factor)}[v]";
        if (!hasAudio)
            return ["-filter_complex", picture, "-map", "[v]"];
        var tempo = string.Join(",", TempoSteps(factor).Select(step => $"atempo={Number(step)}"));
        return ["-filter_complex", $"{picture};[0:a:0]{tempo}[a]", "-map", "[v]", "-map", "[a]"];
    }

    /// <summary>A number as FFmpeg reads it: a dot whatever the culture ("0.75", never "0,75").</summary>
    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
