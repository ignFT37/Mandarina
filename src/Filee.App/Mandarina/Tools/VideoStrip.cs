// Mandarina: the film strip under the big frame of the video panels: a few small frames, evenly spread over the clip, as
// ONE picture. A single FFmpeg run decodes the video once and tiles the thumbnails side by side into a temporary JPEG,
// which is loaded as a bitmap and deleted. It is decoration, so any failure just means "no thumbnails".

using System.Globalization;
using Avalonia.Media.Imaging;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

public static class VideoStrip
{
    /// <summary>Clips longer than this (seconds) only have their key frames decoded: reading every frame of an hour would take minutes.</summary>
    private const double LongVideo = 180;

    /// <summary>The longest FFmpeg may take for the strip before it is given up (the timeline then simply has no thumbnails).</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many thumbnails make a strip of <paramref name="trackWidth"/> × <paramref name="trackHeight"/> for a picture of
    /// <paramref name="aspect"/> (width / height): enough that each one keeps its proportion when the strip is stretched to
    /// the track. Between 4 and 24.
    /// </summary>
    public static int CountFor(double aspect, double trackWidth, double trackHeight)
    {
        if (!(aspect > 0) || !(trackWidth > 0) || !(trackHeight > 0))
            return 12;
        return (int)Math.Clamp(Math.Round(trackWidth / (trackHeight * aspect)), 4, 24);
    }

    /// <summary>The video filter that picks <paramref name="count"/> frames evenly over <paramref name="duration"/> seconds and tiles them in a row.</summary>
    internal static string Filter(int count, double duration, int height)
    {
        var rate = (count / duration).ToString("0.########", CultureInfo.InvariantCulture);
        // tpad repeats the last frame if the sampling comes up one short, so the last cell is never black.
        return $"fps={rate},tpad=stop_mode=clone:stop=3,scale=-2:{height.ToString(CultureInfo.InvariantCulture)},tile={count.ToString(CultureInfo.InvariantCulture)}x1";
    }

    /// <summary>
    /// A bitmap of <paramref name="count"/> thumbnails of <paramref name="file"/>, <paramref name="height"/> pixels high, side
    /// by side; null when it cannot be made. <paramref name="duration"/> is the clip's length in seconds.
    /// </summary>
    public static async Task<Bitmap?> BuildAsync(MediaTools media, string file, double duration, int count = 12, int height = 64,
        CancellationToken cancel = default)
    {
        if (!(duration > 0) || count < 1 || height < 8)
            return null;
        var temp = OutputPaths.Temp(".jpg");
        try
        {
            List<string> args = [];
            if (duration > LongVideo)
                args.AddRange(["-skip_frame", "nokey"]);
            args.AddRange(["-i", file, "-an", "-vf", Filter(count, duration, height), "-frames:v", "1", "-q:v", "4", temp]);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(Patience);
            await media.RunAsync(args, cancel: limit.Token);
            if (!File.Exists(temp))
                return null;
            await using var stream = File.OpenRead(temp);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            MediaTools.TryDelete(temp);
        }
    }
}
