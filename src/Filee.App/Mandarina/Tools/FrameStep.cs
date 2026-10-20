// Mandarina: where the frames of a video are. The three video panels (trim, split, snapshots) step through a clip one
// frame at a time and ask FFmpeg for "the frame at this time"; both need the same arithmetic, so it lives here, as plain
// functions checked with plain numbers. Times are seconds; frame n starts at n / fps.

namespace Filee.App.Mandarina.Tools;

public static class FrameStep
{
    /// <summary>The rate assumed when the file does not say (ffprobe reports 0 for a few odd files).</summary>
    public const double DefaultRate = 25;

    /// <summary>Slack that makes 26 × (1 / 25) = 1.0400000000000003 still count as "exactly on frame 26".</summary>
    private const double Slack = 1e-6;

    /// <summary>
    /// How far from a frame's own time FFmpeg is asked to seek, in seconds. Timestamps are stored rounded (to the millisecond
    /// in MKV), so asking for exactly 1.04 s can land on the frame after the one that starts at 1.04 s: decoding seeks a hair
    /// before the frame, copying (which goes back to the key frame at or before the time) a hair after it.
    /// </summary>
    public const double SeekMargin = 0.002;

    /// <summary><paramref name="fps"/> when it is a real rate, <see cref="DefaultRate"/> otherwise.</summary>
    public static double Rate(double fps) => double.IsFinite(fps) && fps > 0 ? fps : DefaultRate;

    /// <summary>
    /// The time one frame after (<paramref name="direction"/> above 0) or before (below 0) <paramref name="position"/>, on the
    /// frame grid and kept inside 0..<paramref name="duration"/>. A position between two frames steps to the frame boundary
    /// in that direction; 0 keeps the position (only clamped). Without a duration there is nowhere to step: 0.
    /// </summary>
    public static double Next(double position, double fps, int direction, double duration)
    {
        if (!(duration > 0))
            return 0;
        fps = Rate(fps);
        var frames = double.IsFinite(position) ? position * fps : 0;
        var target = direction switch
        {
            > 0 => Math.Floor(frames + Slack) + 1,
            < 0 => Math.Ceiling(frames - Slack) - 1,
            _ => frames,
        };
        return Math.Clamp(target / fps, 0, duration);
    }

    /// <summary>Index of the last frame of a video that lasts <paramref name="duration"/> seconds (its length in whole frames, minus one).</summary>
    public static int LastFrame(double duration, double fps) =>
        Math.Max(0, (int)Math.Round(Math.Max(0, duration) * Rate(fps)) - 1);

    /// <summary>
    /// The first frame that starts at or after <paramref name="seconds"/>, which is the frame FFmpeg's <c>-ss</c> shows;
    /// never past the last frame (the very end of a video has no frame of its own).
    /// </summary>
    public static int FrameAt(double seconds, double fps, double duration) =>
        (int)Math.Clamp(Math.Ceiling(seconds * Rate(fps) - Slack), 0, LastFrame(duration, fps));

    /// <summary>
    /// The frame just after a range that ends at <paramref name="seconds"/>: every frame that starts before it is inside.
    /// Between 1 and the number of frames, so a range always holds at least one.
    /// </summary>
    public static int EndFrame(double seconds, double fps, double duration) =>
        (int)Math.Clamp(Math.Ceiling(seconds * Rate(fps) - Slack), 1, LastFrame(duration, fps) + 1);

    /// <summary>The time frame <paramref name="frame"/> starts at.</summary>
    public static double TimeOf(int frame, double fps) => frame / Rate(fps);

    /// <summary>
    /// The value for FFmpeg's <c>-ss</c> that lands on <see cref="FrameAt"/> of <paramref name="seconds"/>: a hair before the
    /// frame's own time, so rounded timestamps never skip it. Previews, cuts and snapshots all seek with it, which makes the
    /// frame shown in a preview the very frame that is cut or saved.
    /// </summary>
    public static double SeekTime(double seconds, double fps, double duration) =>
        Math.Max(0, TimeOf(FrameAt(seconds, fps, duration), fps) - SeekMargin);
}
