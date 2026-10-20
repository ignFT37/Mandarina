// Mandarina: Trim and Split on a video of VARIABLE frame rate (phone videos, screen recordings). The test clip is 6 s at 30 fps in which
// frame n shows n in binary (eight black/white blocks, readable after H.264) and only the frames n % 7, n % 11 or n % 13 == 0
// are kept, so the gaps between frames go from 1 to 7 frames. A cut must keep a run of source frames with no gap and no
// duplicate, consecutive clips of a split must cover every frame exactly once, and the sound must stay in step.

using Avalonia.Headless.XUnit;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaVfrTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-vfr").FullName;

    /// <summary>The source frames the test clip keeps, by their number at 30 fps.</summary>
    private static readonly int[] Kept = [.. Enumerable.Range(0, 180).Where(n => n % 7 == 0 || n % 11 == 0 || n % 13 == 0)];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> MakeClipAsync(string name = "vfr.mp4")
    {
        var path = await MakeMediaAsync(Path.Combine(_root, name),
            "-f", "lavfi", "-i",
            "color=c=black:s=320x180:r=30:d=6,format=yuv420p,geq=lum='255*mod(floor(N/pow(2,floor(X/40))),2)':cb=128:cr=128,select='not(mod(n,7))+not(mod(n,11))+not(mod(n,13))'",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-fps_mode", "vfr", "-c:v", "libx264", "-crf", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart");
        if (path is null)
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
        return path!;
    }

    /// <summary>The source frame number each frame of <paramref name="video"/> shows, in order of appearance.</summary>
    private async Task<int[]> FrameNumbersAsync(string video)
    {
        var folder = Path.Combine(_root, "frames-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        await MediaTools.Locate()!.RunAsync(["-i", video, "-map", "0:v:0", "-fps_mode", "passthrough", Path.Combine(folder, "f%04d.png")]);
        var numbers = new List<int>();
        foreach (var file in Directory.GetFiles(folder, "f*.png").OrderBy(f => f, StringComparer.Ordinal))
        {
            using var image = new MagickImage(file);
            var pixels = image.GetPixels();
            var number = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                var value = pixels.GetPixel(40 * bit + 20, 90).GetChannel(0);
                if (value > Quantum.Max / 2)
                    number |= 1 << bit;
            }
            numbers.Add(number);
        }
        return [.. numbers];
    }

    private static async Task<(double Video, double Audio)> DurationsAsync(string video)
    {
        var probe = await MediaTools.Locate()!.ProbeAsync(video);
        // ffprobe's container duration covers both streams; the streams' own are read through a second probe of each.
        var result = await Filee.Engines.Infrastructure.ProcessRunner.RunAsync(MediaTools.Locate()!.Ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,duration", "-of", "csv=p=0", video], TimeSpan.FromMinutes(1), default);
        double v = 0, a = 0;
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length == 2 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                if (parts[0] == "video")
                    v = d;
                else if (parts[0] == "audio")
                    a = d;
            }
        }
        return (v > 0 ? v : probe.Duration, a);
    }

    [AvaloniaFact]
    public async Task The_test_clip_really_has_a_variable_frame_rate()
    {
        var clip = await MakeClipAsync();

        Assert.Equal(Kept, await FrameNumbersAsync(clip));
        var probe = await MediaTools.Locate()!.ProbeAsync(clip);
        Assert.InRange(probe.FrameRate, 6, 11);   // the average (51 frames in 6 s), not the 30 fps the frames come from
    }

    /// <summary>The numbers a cut of <paramref name="from"/>..<paramref name="to"/> seconds should keep, give or take one average frame at each end.</summary>
    private static (int[] Must, int[] Maybe) Expected(double from, double to, double slack)
    {
        int[] Between(double a, double b) => [.. Kept.Where(n => n / 30.0 >= a - 1e-9 && n / 30.0 < b - 1e-9)];
        return (Between(from + slack, to - slack), Between(from - slack, to + slack));
    }

    [AvaloniaTheory]
    [InlineData(0.0, 6.0)]
    [InlineData(2.0, 4.0)]
    [InlineData(1.234, 3.987)]
    [InlineData(4.5, 6.0)]
    [InlineData(0.0, 0.5)]
    public async Task Trimming_keeps_a_run_of_the_source_frames_and_the_sound_in_step(double from, double to)
    {
        var clip = await MakeClipAsync();
        using var panel = new TrimVideoPanel(Context(clip));
        await WaitAsync(() => panel.IsReady);
        panel.Start = from;
        panel.End = to;

        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);

        var numbers = await FrameNumbersAsync(panel.Outputs[0]);
        var slack = 1 / panel.FrameRate;   // the cut is placed on the grid of the average rate: up to one frame of it off
        var (must, maybe) = Expected(from, to, slack);
        Assert.NotEmpty(numbers);
        // No gap and no duplicate: the output is a run of consecutive frames of the source.
        var first = Array.IndexOf(Kept, numbers[0]);
        Assert.True(first >= 0, $"frame {numbers[0]} is not a frame of the source");
        Assert.Equal(Kept.Skip(first).Take(numbers.Length), numbers);
        // And it holds what was asked for, give or take a frame at each end.
        Assert.All(must, n => Assert.Contains(n, numbers));
        Assert.All(numbers, n => Assert.Contains(n, maybe));

        var (video, audio) = await DurationsAsync(panel.Outputs[0]);
        Assert.InRange(Math.Abs(audio - video), 0, 0.25);
    }

    [AvaloniaFact]
    public async Task Splitting_covers_every_frame_once_and_in_order()
    {
        var clip = await MakeClipAsync();
        using var panel = new SplitVideoPanel(Context(clip));
        await WaitAsync(() => panel.IsReady);
        Assert.True(panel.AddCut(1.3));
        Assert.True(panel.AddCut(3.0));
        Assert.True(panel.AddCut(4.71));

        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);

        var folder = panel.Outputs[0];
        var clips = Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(4, clips.Length);
        var all = new List<int>();
        foreach (var part in clips)
        {
            var numbers = await FrameNumbersAsync(part);
            Assert.NotEmpty(numbers);
            all.AddRange(numbers);
            var (video, audio) = await DurationsAsync(part);
            Assert.InRange(Math.Abs(audio - video), 0, 0.25);
        }
        Assert.Equal(Kept, all);
    }

    [AvaloniaFact]
    public async Task Splitting_evenly_covers_every_frame_once_and_in_order()
    {
        var clip = await MakeClipAsync();
        using var panel = new SplitVideoPanel(Context(clip));
        await WaitAsync(() => panel.IsReady);
        panel.SplitEvenly(3);

        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);

        var all = new List<int>();
        foreach (var part in Directory.GetFiles(panel.Outputs[0]).OrderBy(f => f, StringComparer.Ordinal))
            all.AddRange(await FrameNumbersAsync(part));
        Assert.Equal(Kept, all);
    }

    [AvaloniaFact]
    public async Task Cutting_without_re_encoding_starts_on_a_frame_of_the_source_and_stays_in_step()
    {
        var clip = await MakeClipAsync();
        using var panel = new TrimVideoPanel(Context(clip));
        await WaitAsync(() => panel.IsReady);
        panel.NoRecode = true;
        panel.Start = 2.0;
        panel.End = 4.0;

        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);

        var numbers = await FrameNumbersAsync(panel.Outputs[0]);
        Assert.NotEmpty(numbers);
        var first = Array.IndexOf(Kept, numbers[0]);
        Assert.True(first >= 0);
        Assert.Equal(Kept.Skip(first).Take(numbers.Length), numbers);   // a run of source frames, whatever key frame it starts on
        var (video, audio) = await DurationsAsync(panel.Outputs[0]);
        Assert.InRange(Math.Abs(audio - video), 0, 0.5);
    }
}
