// Mandarina: three VIDEO tools (trim, split, snapshots) and what they share: the frame arithmetic, the cut command,
// the film strip and the timeline control. The numbers are checked with plain values; the panels run against the real FFmpeg
// (engines\ffmpeg next to the test binaries) and the results are inspected as a viewer would: how long the file is, how many
// frames it holds, which frame it starts on (compared with the same frame of the original), and whether the original is
// untouched. Renders go to the screenshot folder as mandarina-panel-video*.png for review.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.Engines.Infrastructure;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaVideoTests : IDisposable
{
    private const string NoFfmpeg = "FFmpeg is not installed next to the tests (engines\\ffmpeg).";

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-video").FullName;

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

    // ───────── Helpers ─────────

    /// <summary>1280×720, 25 fps, 6 s, with a 440 Hz tone (or without). testsrc2 has a running counter: no two frames look alike.</summary>
    private Task<string?> MakeVideoAsync(string name, bool withAudio = true, params string[] encoderOptions) =>
        withAudio
            ? MakeMediaAsync(Path.Combine(_root, name), ["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25:duration=6", "-f", "lavfi", "-i",
                "sine=frequency=440:duration=6", "-c:v", "libx264", "-pix_fmt", "yuv420p", .. encoderOptions, "-c:a", "aac", "-shortest"])
            : MakeMediaAsync(Path.Combine(_root, name), ["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25:duration=6",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", .. encoderOptions]);

    /// <summary>The same, with a key frame every second (the default makes one at the start and none after it), as real footage has.</summary>
    private Task<string?> MakeVideoWithKeyFramesAsync(string name) =>
        MakeVideoAsync(name, true, "-g", "25", "-keyint_min", "25", "-sc_threshold", "0");

    private async Task<TrimVideoPanel> OpenTrimAsync(string file, string language = "es")
    {
        var panel = new TrimVideoPanel(ContextIn(language, file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    private async Task<SplitVideoPanel> OpenSplitAsync(string file, string language = "es")
    {
        var panel = new SplitVideoPanel(ContextIn(language, file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    private async Task<VideoSnapshotsPanel> OpenSnapshotsAsync(string file, string language = "es")
    {
        var panel = new VideoSnapshotsPanel(ContextIn(language, file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    private static MediaTools Media => MediaTools.Locate()!;

    private static async Task<string> FfprobeAsync(string file, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(Media.Ffprobe, [.. arguments, file], TimeSpan.FromMinutes(1), CancellationToken.None);
        return result.StandardOutput.Trim();
    }

    /// <summary>How many frames the first video stream really holds (ffprobe decodes and counts them).</summary>
    private static async Task<int> FrameCountAsync(string file) =>
        int.Parse(await FfprobeAsync(file, "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0"),
            CultureInfo.InvariantCulture);

    /// <summary>When the first video frame is shown, in seconds: 0 for a clean cut, a frame's length for one with a gap in front.</summary>
    private static async Task<double> VideoStartAsync(string file) =>
        double.Parse(await FfprobeAsync(file, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_time", "-of", "csv=p=0"),
            CultureInfo.InvariantCulture);

    /// <summary>When the first audio stream starts and how long it lasts, in seconds.</summary>
    private static async Task<(double Start, double Duration)> AudioAsync(string file)
    {
        var fields = (await FfprobeAsync(file, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=start_time,duration", "-of", "csv=p=0")).Split(',');
        return (double.Parse(fields[0], CultureInfo.InvariantCulture), double.Parse(fields[1], CultureInfo.InvariantCulture));
    }

    /// <summary>The frame of <paramref name="file"/> that FFmpeg's -ss shows at <paramref name="seconds"/>, as a picture.</summary>
    private async Task<MagickImage> FrameOfAsync(string file, double seconds)
    {
        var png = await MakeMediaAsync(Path.Combine(_root, $"frame-{Guid.NewGuid():N}.png"), ["-ss", MediaTools.Seconds(seconds), "-i", file, "-frames:v", "1"]);
        return new MagickImage(png!);
    }

    /// <summary>Frame number <paramref name="frame"/> (from 0) of a 25 fps clip: asked for a hair before its own time, so it is that frame and no other.</summary>
    private Task<MagickImage> FrameNumberAsync(string file, int frame) => FrameOfAsync(file, Math.Max(0, frame / 25.0 - 0.005));

    /// <summary>Frame number <paramref name="frame"/> of a clip of <paramref name="fps"/> frames per second, found the same way.</summary>
    private Task<MagickImage> FrameNumberAsync(string file, int frame, double fps) => FrameOfAsync(file, Math.Max(0, frame / fps - 0.005));

    private static double Difference(MagickImage a, MagickImage b) => a.Compare(b, ErrorMetric.MeanAbsolute);

    /// <summary>The picture is the one <paramref name="expected"/> shows (a re-encode blurs it a little) and not its neighbour.</summary>
    private static void AssertSameFrame(MagickImage expected, MagickImage actual, MagickImage neighbour, string what)
    {
        var right = Difference(expected, actual);
        var wrong = Difference(neighbour, actual);
        Assert.True(right < 0.02, $"{what}: it is not the expected frame (error {right:0.0000})");
        Assert.True(wrong > right * 3, $"{what}: the neighbouring frame looks as much like it ({wrong:0.0000}) as the expected one ({right:0.0000})");
    }

    /// <summary>The picture at 320 × 180, so pictures that went through different scalers can be compared.</summary>
    private static MagickImage Small(MagickImage picture)
    {
        picture.Resize(320, 180);
        return picture;
    }

    /// <summary>The names of the files in a folder, in order.</summary>
    private static string[] Names(string folder) => Directory.GetFiles(folder).Select(file => Path.GetFileName(file)!).Order().ToArray();

    private static (uint Width, uint Height) SizeOf(string path)
    {
        var info = new MagickImageInfo(path);
        return (info.Width, info.Height);
    }

    private static string SavePicture(string path, IImage picture)
    {
        using var stream = File.Create(path);
        ((Bitmap)picture).Save(stream, new PngBitmapEncoderOptions());
        return path;
    }

    private sealed class Recorder : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    /// <summary>Cancels the run as soon as the progress reaches a mark.</summary>
    private sealed class CancelOnProgress(CancellationTokenSource source, double mark) : IProgress<double>
    {
        public void Report(double value)
        {
            if (value >= mark)
                source.Cancel();
        }
    }

    private static MediaProbe Probe(double duration = 6, double fps = 25, int width = 1280, int height = 720, bool audio = true, string codec = "h264") =>
        new(duration, true, audio, width, height, fps, audio ? 2 : 0, audio ? 44100 : 0, new Dictionary<string, string>(), audio ? "aac" : null, codec);

    private static async Task PumpForAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>Takes the clip away once nothing reads it any more: Windows will not delete a file FFmpeg still has open.</summary>
    private static async Task DeleteWhenIdleAsync(VideoPanelBase panel, string file)
    {
        for (var i = 0; i < 100 && panel.Strip is null; i++)
            await PumpForAsync(100);
        await PumpForAsync(700);
        File.Delete(file);
    }

    // ───────── Frame arithmetic ─────────

    [Theory]
    [InlineData(1.0, 25, 1, 6, 1.04)]
    [InlineData(1.0, 25, -1, 6, 0.96)]
    [InlineData(1.013, 25, 1, 6, 1.04)]            // between frames: on to the next boundary
    [InlineData(1.013, 25, -1, 6, 1.0)]            // and back to the one it is past
    [InlineData(1.04, 25, 1, 6, 1.08)]             // 26 / 25 is not exactly 1.04 in floating point; still frame 26
    [InlineData(1.04, 25, -1, 6, 1.0)]
    [InlineData(0, 25, -1, 6, 0)]                  // never before the start
    [InlineData(6, 25, 1, 6, 6)]                   // nor after the end
    [InlineData(5.99, 25, 1, 6, 6)]
    [InlineData(2.5, 25, 0, 6, 2.5)]               // no direction: where it is
    [InlineData(9, 25, 0, 6, 6)]
    [InlineData(1.0, 0, 1, 6, 1.04)]               // no rate in the file: 25 fps
    [InlineData(1.0, 30000.0 / 1001, 1, 10, 30 * 1001.0 / 30000)]          // 1.0 s is between frames 29 and 30 at 29.97 fps
    [InlineData(30 * 1001.0 / 30000, 30000.0 / 1001, 1, 10, 31 * 1001.0 / 30000)]
    [InlineData(30 * 1001.0 / 30000, 30000.0 / 1001, -1, 10, 29 * 1001.0 / 30000)]
    [InlineData(1.0, 25, 1, 0, 0)]                 // no length, nowhere to go
    public void A_step_moves_one_frame_on_the_frame_grid_and_stays_inside_the_clip(double position, double fps, int direction, double duration, double expected) =>
        Assert.Equal(expected, FrameStep.Next(position, fps, direction, duration), 6);

    [Fact]
    public void Frames_are_found_the_way_FFmpegs_seek_finds_them()
    {
        // The first frame at or after the time; the last frame is as far as it goes (the very end has no frame of its own).
        Assert.Equal(25, FrameStep.FrameAt(1.0, 25, 6));
        Assert.Equal(26, FrameStep.FrameAt(1.001, 25, 6));
        Assert.Equal(26, FrameStep.FrameAt(1.013, 25, 6));
        Assert.Equal(26, FrameStep.FrameAt(1.04, 25, 6));
        Assert.Equal(0, FrameStep.FrameAt(-1, 25, 6));
        Assert.Equal(149, FrameStep.LastFrame(6, 25));
        Assert.Equal(149, FrameStep.FrameAt(6, 25, 6));
        Assert.Equal(149, FrameStep.FrameAt(99, 25, 6));
        Assert.Equal(1.0, FrameStep.TimeOf(25, 25));

        // A range ends before the first frame that starts at its end: [1.0, 3.0) is frames 25 to 74.
        Assert.Equal(75, FrameStep.EndFrame(3.0, 25, 6));
        Assert.Equal(76, FrameStep.EndFrame(3.007, 25, 6));
        Assert.Equal(150, FrameStep.EndFrame(6, 25, 6));
        Assert.Equal(150, FrameStep.EndFrame(99, 25, 6));
        Assert.Equal(1, FrameStep.EndFrame(0, 25, 6));        // never an empty range
    }

    [Theory]
    [InlineData(25.0)]
    [InlineData(30000.0 / 1001)]
    [InlineData(60.0)]
    [InlineData(23.976)]
    public void The_seek_time_always_lands_between_the_previous_frame_and_the_wanted_one(double fps)
    {
        for (var frame = 1; frame < 140; frame++)
        {
            var seek = FrameStep.SeekTime(FrameStep.TimeOf(frame, fps), fps, 6);
            Assert.True(seek > FrameStep.TimeOf(frame - 1, fps) + 0.001, $"frame {frame} at {fps}: the seek {seek} reaches back to the frame before");
            Assert.True(seek <= FrameStep.TimeOf(frame, fps), $"frame {frame} at {fps}: the seek {seek} is past the frame");
        }
        Assert.Equal(0, FrameStep.SeekTime(0, fps, 6));
    }

    [Fact]
    public void A_stretch_is_snapped_to_whole_frames_and_never_to_nothing()
    {
        var clip = Probe();
        var exact = VideoCut.Span(1, 3, clip);
        Assert.Equal(50, exact.Frames);
        Assert.Equal(2.0, exact.Length, 6);
        Assert.Equal(1.0, exact.Start, 6);                     // frame 25

        var between = VideoCut.Span(1.013, 3.007, clip);       // frame 26 (1.04 s) up to frame 75
        Assert.Equal(50, between.Frames);
        Assert.Equal(2.0, between.Length, 6);
        Assert.Equal(1.04, between.Start, 6);

        var whole = VideoCut.Span(0, 6, clip);
        Assert.Equal((150, 6.0, 0.0), (whole.Frames, whole.Length, whole.Start));

        Assert.Equal(1, VideoCut.Span(5.99, 6, clip).Frames);  // inside the last frame
        Assert.Equal(1, VideoCut.Span(2, 2, clip).Frames);     // a range holds a frame at least

        // Two clips that meet at a cut share the cut's frame: none is lost, none is shown twice.
        Assert.Equal(150, VideoCut.Span(0, 2.345, clip).Frames + VideoCut.Span(2.345, 6, clip).Frames);
    }

    // ───────── The cut command ─────────

    [Fact]
    public void A_precise_cut_re_encodes_with_the_chosen_stretch_and_keeps_sound_and_tags()
    {
        var args = VideoCut.Arguments("in.mp4", "out.mp4", VideoCut.Span(1, 3, Probe()), Probe(), copy: false);
        Assert.Equal(["-ss", "0.998", "-i", "in.mp4", "-t", "2", "-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0"], args.Take(12));
        Assert.Equal(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"], args.SkipWhile(a => a != "-c:v").Take(8));
        Assert.Equal(["-c:a", "aac", "-b:a", "192k"], args.SkipWhile(a => a != "-c:a").Take(4));
        Assert.Equal(["-movflags", "+faststart", "out.mp4"], args.TakeLast(3));
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void A_cut_of_a_clip_without_sound_has_no_audio_options()
    {
        var silent = Probe(audio: false);
        var args = VideoCut.Arguments("in.mp4", "out.mp4", VideoCut.Span(1, 3, silent), silent, copy: false);
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("aac", args);
    }

    [Fact]
    public void A_fast_cut_copies_the_streams_and_moves_the_timestamps_to_zero()
    {
        var args = VideoCut.Arguments("in.mkv", "out.mkv", VideoCut.Span(1, 3, Probe()), Probe(), copy: true);
        Assert.Equal(["-c", "copy", "-avoid_negative_ts", "make_zero", "out.mkv"], args.TakeLast(5));
        Assert.Equal("1.002", args[args.IndexOf("-ss") + 1]);                  // a hair after the cut, so a key frame on it is the one taken
        Assert.Equal("0", VideoCut.Arguments("in.mkv", "out.mkv", VideoCut.Span(0, 3, Probe()), Probe(), copy: true)[1]);   // from the start: no seek
        Assert.DoesNotContain("libx264", args);
        Assert.DoesNotContain("-movflags", args);
    }

    [Fact]
    public void HEVC_stays_HEVC_and_only_the_MP4_family_gets_the_options_of_that_family()
    {
        var hevc = Probe(codec: "hevc");
        var mp4 = VideoCut.Arguments("in.mp4", "out.mp4", VideoCut.Span(1, 3, hevc), hevc, copy: false);
        Assert.Equal(["-c:v", "libx265", "-crf", "22", "-tag:v", "hvc1"], mp4.SkipWhile(a => a != "-c:v").Take(6));
        Assert.Contains("+faststart", mp4);
        Assert.DoesNotContain("-pix_fmt", mp4);                // the source's own pixel format, 10-bit included

        var mkv = VideoCut.Arguments("in.mkv", "out.mkv", VideoCut.Span(1, 3, hevc), hevc, copy: false);
        Assert.Contains("libx265", mkv);
        Assert.DoesNotContain("-tag:v", mkv);
        Assert.DoesNotContain("-movflags", mkv);

        var mov = VideoCut.Arguments("in.mov", "out.MOV", VideoCut.Span(1, 3, hevc), hevc, copy: false);
        Assert.Contains("hvc1", mov);
    }

    [Fact]
    public void A_clip_with_an_odd_size_loses_its_odd_row_and_column_so_4_2_0_can_be_written()
    {
        var odd = Probe(width: 1281, height: 721);
        var args = VideoCut.Arguments("in.mp4", "out.mp4", VideoCut.Span(1, 3, odd), odd, copy: false);
        Assert.Equal("crop=trunc(iw/2)*2:trunc(ih/2)*2", args[args.IndexOf("-vf") + 1]);
        Assert.DoesNotContain("-vf", VideoCut.Arguments("in.mp4", "out.mp4", VideoCut.Span(1, 3, odd), odd, copy: true));
    }

    // ───────── The timeline control ─────────

    /// <summary>A 424 px wide timeline: the track is 400 px, so 10 s are 40 px per second.</summary>
    private static VideoTimeline Timeline(double duration = 10, double start = 2, double end = 8, double fps = 25)
    {
        var timeline = new VideoTimeline { Duration = duration, Start = start, End = end, FrameRate = fps };
        timeline.Measure(new Size(424, 68));
        timeline.Arrange(new Rect(0, 0, 424, 68));
        return timeline;
    }

    [AvaloniaFact]
    public void Dragging_a_handle_moves_it_and_stops_one_frame_short_of_the_other_one_and_at_the_ends()
    {
        var timeline = Timeline();
        const int y = 34;
        Assert.Equal(92, timeline.XOf(2), 6);

        Assert.True(timeline.BeginDrag(new Point(timeline.XOf(2), y)));
        timeline.DragTo(new Point(timeline.XOf(1), y));
        Assert.Equal(1, timeline.Start, 3);
        Assert.Equal(8, timeline.End);
        timeline.DragTo(new Point(timeline.XOf(9.5), y));       // beyond the end handle
        Assert.Equal(8 - 0.04, timeline.Start, 3);              // one frame short of it
        timeline.DragTo(new Point(-500, y));                    // far to the left
        Assert.Equal(0, timeline.Start);
        timeline.EndDrag();
        timeline.DragTo(new Point(timeline.XOf(5), y));         // released: the pointer no longer moves it
        Assert.Equal(0, timeline.Start);

        Assert.True(timeline.BeginDrag(new Point(timeline.XOf(8), y)));
        timeline.DragTo(new Point(timeline.XOf(0.5), y));
        Assert.Equal(0.5, timeline.End, 3);
        timeline.DragTo(new Point(-500, y));                    // far to the left, past the start handle at 0
        Assert.Equal(0.04, timeline.End, 3);                    // one frame after it
        timeline.DragTo(new Point(2000, y));                    // far to the right
        Assert.Equal(10, timeline.End);
        timeline.EndDrag();
    }

    [AvaloniaFact]
    public void The_least_gap_between_the_handles_is_one_frame_of_the_clips_own_rate()
    {
        var timeline = Timeline(start: 2, end: 3, fps: 10);
        timeline.BeginDrag(new Point(timeline.XOf(2), 34));
        timeline.DragTo(new Point(timeline.XOf(5), 34));
        Assert.Equal(2.9, timeline.Start, 3);                   // 0.1 s at 10 fps
        timeline.EndDrag();

        var unknown = Timeline(start: 2, end: 3, fps: 0);       // no rate: 25 fps
        unknown.BeginDrag(new Point(unknown.XOf(2), 34));
        unknown.DragTo(new Point(unknown.XOf(5), 34));
        Assert.Equal(2.96, unknown.Start, 3);
    }

    [AvaloniaFact]
    public void A_handle_is_taken_where_it_stands_and_does_not_jump_to_the_pointer()
    {
        var timeline = Timeline();
        Assert.True(timeline.BeginDrag(new Point(timeline.XOf(2) + 6, 34)));    // 6 px beside the handle
        Assert.Equal(2, timeline.Start);                                         // nothing moved yet
        timeline.DragTo(new Point(timeline.XOf(2) + 6 + 40, 34));               // one second to the right
        Assert.Equal(3, timeline.Start, 3);                                      // by the distance dragged
        timeline.EndDrag();
    }

    [AvaloniaFact]
    public void Pressing_on_the_track_moves_the_cursor_and_says_so_and_dragging_follows_it()
    {
        var timeline = Timeline();
        var seen = new List<double>();
        timeline.Seeked += (_, seconds) => seen.Add(seconds);

        Assert.True(timeline.BeginDrag(new Point(timeline.XOf(5), 34)));         // far from both handles
        Assert.Equal(5, timeline.Position, 3);
        Assert.Equal([timeline.Position], seen);
        timeline.DragTo(new Point(timeline.XOf(6.5), 34));
        Assert.Equal(6.5, timeline.Position, 3);
        timeline.DragTo(new Point(-80, 34));
        Assert.Equal(0, timeline.Position);                                      // inside the clip
        timeline.DragTo(new Point(9999, 34));
        Assert.Equal(10, timeline.Position);
        Assert.Equal(4, seen.Count);
        Assert.Equal((2, 8), (timeline.Start, timeline.End));                    // the handles did not move
        timeline.EndDrag();
    }

    [AvaloniaFact]
    public void Without_handles_the_whole_track_is_for_the_cursor_and_without_a_clip_nothing_can_be_taken()
    {
        var timeline = Timeline();
        timeline.ShowRange = false;
        var seen = 0;
        timeline.Seeked += (_, _) => seen++;
        Assert.True(timeline.BeginDrag(new Point(timeline.XOf(2), 34)));        // exactly on where a handle would be
        Assert.Equal(2, timeline.Start);
        Assert.Equal(2, timeline.Position, 3);
        Assert.Equal(1, seen);
        timeline.EndDrag();

        var empty = new VideoTimeline();
        empty.Measure(new Size(424, 68));
        empty.Arrange(new Rect(0, 0, 424, 68));
        Assert.False(empty.BeginDrag(new Point(100, 34)));
    }

    [AvaloniaFact]
    public void The_arrow_keys_move_the_cursor_one_frame_and_say_so()
    {
        var timeline = Timeline(fps: 25);
        timeline.Position = 1.0;
        var seen = new List<double>();
        timeline.Seeked += (_, seconds) => seen.Add(seconds);
        void Press(Key key) => timeline.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

        Press(Key.Right);
        Assert.Equal(1.04, timeline.Position, 6);
        Press(Key.Left);
        Press(Key.Left);
        Assert.Equal(0.96, timeline.Position, 6);
        Assert.Equal(3, seen.Count);

        timeline.Position = 0;
        Press(Key.Left);
        Assert.Equal(0, timeline.Position);                                      // not before the start
        Press(Key.Up);
        Assert.Equal(0, timeline.Position);                                      // other keys are not ours
    }

    [AvaloniaFact]
    public void The_stepper_counts_inside_its_limits_and_says_when_it_cannot_go_further()
    {
        var stepper = new VideoStepper { Minimum = 2, Maximum = 5, Value = 3 };
        string Shown() => stepper.FindControl<TextBlock>("Number")!.Text!;
        bool CanGo(string name) => stepper.FindControl<RepeatButton>(name)!.IsEnabled;

        Assert.Equal("3", Shown());
        Assert.True(CanGo("Less") && CanGo("More"));
        stepper.Step(1);
        Assert.Equal(("4", 4), (Shown(), stepper.Value));
        stepper.Step(5);                                        // stops at the top
        Assert.Equal(("5", 5), (Shown(), stepper.Value));
        Assert.False(CanGo("More"));
        Assert.True(CanGo("Less"));
        stepper.Step(-9);                                       // and at the bottom
        Assert.Equal(("2", 2), (Shown(), stepper.Value));
        Assert.False(CanGo("Less"));
        stepper.Value = 99;                                     // a value that is set from outside is held to the limits too
        Assert.Equal(5, stepper.Value);
        stepper.Maximum = 4;
        Assert.Equal(("4", 4), (Shown(), stepper.Value));
    }

    // ───────── The film strip ─────────

    [Fact]
    public void The_number_of_thumbnails_keeps_each_one_in_proportion_when_the_strip_is_stretched_over_the_track()
    {
        Assert.Equal(6, VideoStrip.CountFor(16.0 / 9, 540, 52));          // 52 px high, 92 px wide each
        Assert.Equal(8, VideoStrip.CountFor(4.0 / 3, 540, 52));
        Assert.Equal(18, VideoStrip.CountFor(9.0 / 16, 540, 52));         // a phone held upright: narrow slivers
        Assert.Equal(4, VideoStrip.CountFor(5, 540, 52));                 // never fewer than 4
        Assert.Equal(24, VideoStrip.CountFor(0.1, 540, 52));              // nor more than 24
        Assert.Equal(12, VideoStrip.CountFor(0, 540, 52));                // unknown proportion: the default
    }

    [Fact]
    public void The_strip_filter_samples_the_whole_clip_evenly_and_never_leaves_a_black_cell()
    {
        Assert.Equal("fps=2,tpad=stop_mode=clone:stop=3,scale=-2:64,tile=12x1", VideoStrip.Filter(12, 6, 64));
        Assert.Equal("fps=1.16666667,tpad=stop_mode=clone:stop=3,scale=-2:56,tile=7x1", VideoStrip.Filter(7, 6, 56));
    }

    [AvaloniaFact]
    public async Task The_strip_is_one_bitmap_as_wide_as_its_thumbnails_together()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var strip = await VideoStrip.BuildAsync(Media, file, 6, count: 12, height: 64);
        Assert.NotNull(strip);
        Assert.Equal(64, strip.PixelSize.Height);
        // 64 px high from a 16:9 picture: each thumbnail is about 114 px wide (FFmpeg keeps it even).
        Assert.InRange(strip.PixelSize.Width, 12 * 112, 12 * 116);

        using var seven = await VideoStrip.BuildAsync(Media, file, 6, count: 7, height: 56);
        Assert.NotNull(seven);
        Assert.Equal(56, seven.PixelSize.Height);
        Assert.InRange(seven.PixelSize.Width, 7 * 98, 7 * 102);

        // The strip is a picture of the clip: every cell holds a picture, the last one too.
        var path = SavePicture(Path.Combine(_root, "strip.png"), strip);
        using var picture = new MagickImage(path);
        var cell = (int)picture.Width / 12;
        for (var i = 0; i < 12; i++)
        {
            using var part = new MagickImage(picture);
            part.Crop(new MagickGeometry(i * cell, 0, (uint)cell, 64));
            using var black = new MagickImage(MagickColors.Black, (uint)cell, 64);
            Assert.True(Difference(part, black) > 0.05, $"cell {i} of the strip is empty");
        }
    }

    [AvaloniaFact]
    public async Task A_clip_of_more_than_three_minutes_gets_its_strip_from_the_key_frames_alone()
    {
        // 200 s of small frames, a key frame every 10 s: long enough for the strip to be built without decoding every frame.
        var file = await MakeMediaAsync(Path.Combine(_root, "largo.mp4"), ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=5:duration=200",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0"]);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var strip = await VideoStrip.BuildAsync(Media, file, 200, count: 8, height: 48);
        Assert.NotNull(strip);
        Assert.Equal(48, strip.PixelSize.Height);
        Assert.InRange(strip.PixelSize.Width, 8 * 84, 8 * 88);        // 320 × 180 at 48 px high: 86 px each

        using var panel = await OpenTrimAsync(file);                  // and the panel gets it the same way
        await WaitAsync(() => panel.Strip is not null);
        Assert.Equal(64, panel.Strip!.Size.Height);
    }

    [AvaloniaFact]
    public async Task A_strip_that_cannot_be_made_is_just_null_and_cancelling_is_not_swallowed()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var missing = Path.Combine(_root, "no existe.mp4");
        Assert.Null(await VideoStrip.BuildAsync(Media, missing, 6));
        File.WriteAllText(missing, "this is not a video");
        Assert.Null(await VideoStrip.BuildAsync(Media, missing, 6));
        Assert.Null(await VideoStrip.BuildAsync(Media, file, 0));              // no length, no strip

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoStrip.BuildAsync(Media, file, 6, cancel: cancelled.Token));
    }

    // ───────── The registry ─────────

    [AvaloniaFact]
    public void The_registry_builds_the_three_panels_and_they_all_need_ffmpeg()
    {
        var context = Context(Path.Combine(_root, "clip.mp4"));
        foreach (var id in new[] { "trimVideo", "splitVideo", "videoSnapshots" })
        {
            Assert.True(PanelRegistry.Has(id));
            Assert.Equal(MediaTools.Locate() is null, PanelRegistry.Unavailable(id, context.Loc) is not null);
        }
        using var trim = (TrimVideoPanel)PanelRegistry.Create("trimVideo", context.Files, context.Loc, context.Platform);
        using var split = (SplitVideoPanel)PanelRegistry.Create("splitVideo", context.Files, context.Loc, context.Platform);
        using var snapshots = (VideoSnapshotsPanel)PanelRegistry.Create("videoSnapshots", context.Files, context.Loc, context.Platform);
        Assert.Equal(("trimVideo", "splitVideo", "videoSnapshots"), (trim.Id, split.Id, snapshots.Id));
        Assert.Equal(("Recortar", "Dividir", "Capturas"), (trim.Title, split.Title, snapshots.Title));
    }

    [AvaloniaFact]
    public async Task A_file_without_video_says_so_in_all_three_panels_and_cannot_be_applied()
    {
        var file = await MakeMediaAsync(Path.Combine(_root, "solo audio.mp3"), ["-f", "lavfi", "-i", "sine=frequency=440:duration=1"]);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        VideoPanelBase[] panels = [new TrimVideoPanel(Context(file)), new SplitVideoPanel(Context(file)), new VideoSnapshotsPanel(Context(file))];
        foreach (var panel in panels)
        {
            using (panel)
            {
                await WaitAsync(() => !panel.IsLoading);
                Assert.Equal("Este archivo no tiene vídeo.", panel.LoadError);
                Assert.False(panel.IsReady);
                Assert.False(panel.CanApply);
                Assert.Null(panel.Frame);
            }
        }
    }

    // ───────── Reading the clip ─────────

    [AvaloniaFact]
    public async Task A_clip_is_read_once_and_shows_its_first_frame_then_its_film_strip()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        Assert.InRange(panel.Duration, 5.95, 6.05);
        Assert.Equal(25, panel.FrameRate, 3);
        Assert.Equal((1280, 720), (panel.Probe!.Width, panel.Probe.Height));
        Assert.Equal(0, panel.Start);
        Assert.Equal(panel.Duration, panel.End);
        Assert.Equal(0, panel.Position);
        Assert.NotNull(panel.Frame);
        Assert.Equal(960, panel.Frame!.Size.Width);                           // no wider than the picture needs
        Assert.True(panel.IsReady);
        Assert.True(panel.CanApply);
        Assert.Equal("clip.mp4", panel.FileName);
        Assert.Equal("0:06.00", panel.DurationText);
        Assert.Equal(("0:00.00", "0:06.00", "0:06.00"), (panel.StartText, panel.EndText, panel.SelectionText));

        await WaitAsync(() => panel.Strip is not null);
        Assert.Equal(64, panel.Strip!.Size.Height);
    }

    [AvaloniaFact]
    public async Task The_cursor_shows_the_frame_it_stands_on_and_a_burst_of_moves_fetches_the_last_one()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        var first = panel.Frame;
        var changes = 0;
        panel.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(VideoPanelBase.Frame) ? 1 : 0;

        panel.Position = 0.5;
        panel.Position = 1.0;
        panel.Position = 2.0;
        Assert.Equal("0:02.00", panel.PositionText);
        await WaitAsync(() => !ReferenceEquals(panel.Frame, first));
        await PumpForAsync(400);                                              // nothing else follows
        Assert.Equal(1, changes);                                             // only the last move was fetched
    }

    [AvaloniaFact]
    public async Task A_cursor_that_keeps_moving_drags_the_picture_along_instead_of_leaving_it_behind()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        var changes = 0;
        panel.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(VideoPanelBase.Frame) ? 1 : 0;

        // 1.6 s of steady dragging, a new position every 40 ms: the picture must be renewed while it goes, not once at the end.
        for (var i = 1; i <= 40; i++)
        {
            panel.Position = i * 0.14;
            await PumpForAsync(40);
        }
        var whileMoving = changes;
        Assert.True(whileMoving >= 2, $"the picture changed {whileMoving} times in 1.6 s of dragging");
        Assert.True(whileMoving < 30, "a frame per move: FFmpeg is being started for every pixel");

        // Once the cursor rests, the picture catches up with it and stops.
        await PumpForAsync(1200);
        var settled = changes;
        await PumpForAsync(500);
        Assert.Equal(settled, changes);
    }

    [AvaloniaFact]
    public async Task The_very_end_of_the_clip_still_shows_a_frame_and_stepping_stays_inside_it()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        var first = panel.Frame;
        panel.Position = panel.Duration;                                      // the end has no frame of its own: the last one is shown
        await WaitAsync(() => !ReferenceEquals(panel.Frame, first));
        Assert.Null(panel.LoadError);

        panel.StepFrame(1);
        Assert.Equal(panel.Duration, panel.Position, 6);                      // not past the end
        panel.Position = 0;
        panel.StepFrame(-1);
        Assert.Equal(0, panel.Position);
        panel.StepFrame(1);
        Assert.Equal(0.04, panel.Position, 6);
        panel.StepFrame(1);
        panel.StepFrame(1);
        panel.StepFrame(-1);
        Assert.Equal(0.08, panel.Position, 6);
    }

    // ───────── Trim: the range ─────────

    [AvaloniaFact]
    public async Task The_handles_the_typed_times_and_the_mark_buttons_all_keep_the_range_at_least_one_frame_wide()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);

        panel.CommitStart("0:01.5");
        panel.CommitEnd("0:03");
        Assert.Equal((1.5, 3.0), (panel.Start, panel.End));
        Assert.Equal(("0:01.50", "0:03.00", "0:01.50"), (panel.StartText, panel.EndText, panel.SelectionText));
        panel.CommitEnd("99");                                                // beyond the end of the clip
        Assert.Equal(panel.Duration, panel.End, 6);
        panel.CommitStart("not a time");                                      // unreadable: the old one stays
        Assert.Equal(1.5, panel.Start);
        panel.CommitStart("0:07");                                            // after the end: one frame short of it
        Assert.Equal(panel.End - 0.04, panel.Start, 6);
        panel.CommitEnd("0:01");                                              // before the start: one frame after it
        Assert.Equal(panel.Start + 0.04, panel.End, 6);

        // "Mark here" takes the cursor's time, inside the same limits.
        panel.CommitStart("0");
        panel.CommitEnd("0:06");
        panel.Position = 2.4;
        panel.MarkStart();
        Assert.Equal(2.4, panel.Start);
        panel.Position = 4.2;
        panel.MarkEnd();
        Assert.Equal(4.2, panel.End);
        panel.Position = 5.5;
        panel.MarkStart();                                                    // past the end: it stops a frame before it
        Assert.Equal(4.2 - 0.04, panel.Start, 6);
        panel.Position = 0.5;
        panel.MarkEnd();
        Assert.Equal(panel.Start + 0.04, panel.End, 6);
    }

    [AvaloniaFact]
    public async Task Apply_needs_at_least_one_frame_between_the_handles_and_the_frame_follows_the_handle()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        Assert.True(panel.CanApply);
        panel.Start = 2;
        panel.End = 2.02;                                                     // half a frame
        Assert.False(panel.CanApply);
        Assert.False(panel.CanRun);
        panel.End = 2.04;                                                     // exactly one
        Assert.True(panel.CanApply);

        // The cursor follows the handle that was moved, so the frame shown is the first or last one chosen.
        var first = panel.Frame;
        panel.Start = 3;
        Assert.Equal(3, panel.Position);
        panel.End = 5;
        Assert.Equal(5, panel.Position);
        await WaitAsync(() => !ReferenceEquals(panel.Frame, first));
    }

    // ───────── Trim: Apply ─────────

    [AvaloniaFact]
    public async Task Trim_cuts_exactly_the_frames_between_the_handles_and_keeps_the_sound()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var original = await File.ReadAllBytesAsync(file);
        using var panel = await OpenTrimAsync(file);
        panel.Start = 1;
        panel.End = 3;

        var progress = new Recorder();
        var written = await panel.ApplyAsync(progress, CancellationToken.None);

        var output = Assert.Single(written);
        Assert.Equal(Path.Combine(_root, "clip recortado.mp4"), output);
        var probe = await Media.ProbeAsync(output);
        Assert.InRange(probe.Duration, 1.99, 2.01);                           // 2.0 s, not a frame more or less
        Assert.Equal(50, await FrameCountAsync(output));
        Assert.InRange(await VideoStartAsync(output), 0, 0.005);              // starts on its first frame, no gap in front
        Assert.Equal((1280, 720), (probe.Width, probe.Height));
        Assert.True(probe.HasAudio);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.Equal("h264", probe.VideoCodec);
        var (audioStart, audioLength) = await AudioAsync(output);
        Assert.InRange(audioStart, 0, 0.005);                                 // sound and picture start together and end together
        Assert.InRange(audioLength, 1.98, 2.02);
        Assert.Equal(original, await File.ReadAllBytesAsync(file));           // the original is untouched
        Assert.NotEmpty(progress.Values);
        Assert.All(progress.Values, value => Assert.InRange(value, 0, 1));
        Assert.Equal(progress.Values.Order(), progress.Values);               // never goes back

        // The first frame is the original's frame 25 (1.00 s) and the last its frame 74 (2.96 s): not the ones next to them.
        using (var expected = await FrameNumberAsync(file, 25))
        using (var before = await FrameNumberAsync(file, 24))
        using (var actual = await FrameNumberAsync(output, 0))
            AssertSameFrame(expected, actual, before, "first frame");
        using (var expected = await FrameNumberAsync(file, 74))
        using (var after = await FrameNumberAsync(file, 75))
        using (var actual = await FrameNumberAsync(output, 49))
            AssertSameFrame(expected, actual, after, "last frame");
    }

    [AvaloniaFact]
    public async Task A_range_that_falls_between_frames_is_cut_on_whole_frames_with_no_gap_at_the_start()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        panel.Start = 1.013;                                                  // the first frame at or after it is frame 26, at 1.04 s
        panel.End = 3.007;                                                    // the last frame kept is frame 75

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(50, await FrameCountAsync(output));
        Assert.InRange((await Media.ProbeAsync(output)).Duration, 1.99, 2.01);
        Assert.InRange(await VideoStartAsync(output), 0, 0.005);
        using var expected = await FrameNumberAsync(file, 26);
        using var before = await FrameNumberAsync(file, 25);
        using var first = await FrameNumberAsync(output, 0);
        AssertSameFrame(expected, first, before, "first frame");
        using var same = await FrameOfAsync(file, 1.013);                     // and it is what FFmpeg shows at that time
        Assert.True(Difference(same, expected) < 0.0005);
    }

    [AvaloniaFact]
    public async Task The_frame_the_preview_shows_at_the_start_handle_is_the_first_frame_of_the_result()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        var shown = panel.Frame;
        panel.Start = 2.3456;                                                 // between frames, as a dragged handle is: frame 59
        await WaitAsync(() => !ReferenceEquals(panel.Frame, shown));
        await PumpForAsync(300);
        var preview = SavePicture(Path.Combine(_root, "preview.png"), panel.Frame!);

        panel.End = 4;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var seen = Small(new MagickImage(preview));
        using var cutFirst = Small(await FrameNumberAsync(Assert.Single(panel.Outputs), 0));
        using var neighbour = Small(await FrameNumberAsync(file, 58));
        AssertSameFrame(seen, cutFirst, neighbour, "preview against the first frame of the result");
    }

    [AvaloniaFact]
    public async Task Trim_without_re_encoding_copies_the_streams_and_starts_on_the_key_frame_before_the_start()
    {
        var file = await MakeVideoWithKeyFramesAsync("clip.mp4");            // key frames at 0, 1, 2, … seconds
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        panel.Start = 1.4;
        panel.End = 3;
        panel.NoRecode = true;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal("clip recortado.mp4", Path.GetFileName(output));
        var probe = await Media.ProbeAsync(output);
        Assert.InRange(probe.Duration, 1.9, 2.5);                             // from the key frame at 1.0 s to 3.0 s
        Assert.True(probe.HasAudio);
        Assert.Equal("h264", probe.VideoCodec);
        using var keyFrame = await FrameNumberAsync(file, 25);
        using var wanted = await FrameNumberAsync(file, 35);
        using var first = await FrameNumberAsync(output, 0);
        Assert.True(Difference(keyFrame, first) < 0.001, "the copy does not start on the key frame");        // the very same pixels
        Assert.True(Difference(wanted, first) > 0.02, "the copy starts on the exact frame, which a copy cannot do");

        // The default clip has one key frame, at the very start: whatever is asked for, the copy begins there.
        var plain = await MakeVideoAsync("sin claves.mp4");
        using var other = await OpenTrimAsync(plain!);
        other.Start = 1;
        other.End = 3;
        other.NoRecode = true;
        await other.RunAsync();
        Assert.True(other.IsDone, other.Status);
        Assert.InRange((await Media.ProbeAsync(Assert.Single(other.Outputs))).Duration, 2.9, 3.4);
    }

    [AvaloniaFact]
    public async Task A_second_trim_does_not_overwrite_the_first()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var first = await OpenTrimAsync(file);
        first.Start = 0;
        first.End = 1;
        await first.RunAsync();
        using var second = await OpenTrimAsync(file);
        second.Start = 2;
        second.End = 3;
        await second.RunAsync();

        Assert.Equal(Path.Combine(_root, "clip recortado.mp4"), Assert.Single(first.Outputs));
        var output = Assert.Single(second.Outputs);
        Assert.Equal(Path.Combine(_root, "clip recortado 2.mp4"), output);
        Assert.Equal(25, await FrameCountAsync(first.Outputs[0]));
        Assert.Equal(25, await FrameCountAsync(output));
    }

    [AvaloniaFact]
    public async Task A_clip_without_sound_is_cut_without_sound()
    {
        var file = await MakeVideoAsync("mudo.mp4", withAudio: false);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        Assert.False(panel.Probe!.HasAudio);
        panel.Start = 1;
        panel.End = 3;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal("mudo recortado.mp4", Path.GetFileName(output));
        var probe = await Media.ProbeAsync(output);
        Assert.False(probe.HasAudio);
        Assert.InRange(probe.Duration, 1.99, 2.01);
        Assert.Equal(50, await FrameCountAsync(output));
    }

    [AvaloniaFact]
    public async Task Trim_keeps_the_container_of_the_original_mkv_mov_and_m4v_as_well_as_mp4()
    {
        var mkv = await MakeVideoAsync("clip.mkv");
        var mov = await MakeVideoAsync("clip.mov");
        var m4v = await MakeVideoAsync("clip.m4v");
        if (mkv is null || mov is null || m4v is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        foreach (var (file, expected) in new[] { (mkv, "clip recortado.mkv"), (mov, "clip recortado.mov"), (m4v, "clip recortado.m4v") })
        {
            using var panel = await OpenTrimAsync(file);
            panel.Start = 1;
            panel.End = 3;
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            var output = Assert.Single(panel.Outputs);
            Assert.Equal(expected, Path.GetFileName(output));
            var probe = await Media.ProbeAsync(output);
            Assert.InRange(probe.Duration, 1.98, 2.03);
            Assert.True(probe.HasAudio);
            Assert.Equal(50, await FrameCountAsync(output));
        }
    }

    [AvaloniaFact]
    public async Task An_HEVC_clip_stays_HEVC_and_plays_in_Apples_players()
    {
        var file = await MakeMediaAsync(Path.Combine(_root, "hevc.mp4"), ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=4", "-f", "lavfi",
            "-i", "sine=frequency=440:duration=4", "-c:v", "libx265", "-crf", "28", "-pix_fmt", "yuv420p", "-tag:v", "hvc1", "-c:a", "aac", "-shortest"]);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        Assert.Equal("hevc", panel.Probe!.VideoCodec);
        panel.Start = 1;
        panel.End = 3;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        var probe = await Media.ProbeAsync(output);
        Assert.Equal("hevc", probe.VideoCodec);
        Assert.Equal("hvc1", await FfprobeAsync(output, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_tag_string", "-of", "csv=p=0"));
        Assert.InRange(probe.Duration, 1.98, 2.03);
        Assert.Equal(50, await FrameCountAsync(output));

        // In MKV the same clip is cut as HEVC too, without the MP4-only options.
        var mkv = await MakeMediaAsync(Path.Combine(_root, "hevc.mkv"), ["-i", file, "-c", "copy"]);
        using var inMkv = await OpenTrimAsync(mkv!);
        inMkv.Start = 1;
        inMkv.End = 3;
        await inMkv.RunAsync();
        Assert.True(inMkv.IsDone, inMkv.Status);
        var cut = Assert.Single(inMkv.Outputs);
        Assert.Equal("hevc", (await Media.ProbeAsync(cut)).VideoCodec);
        Assert.Equal(50, await FrameCountAsync(cut));
    }

    [AvaloniaFact]
    public async Task A_clip_at_29_97_frames_a_second_in_mkv_is_cut_on_exactly_the_frames_asked_for()
    {
        // 30000/1001 fps and MKV's millisecond timestamps: the frames do not fall on round numbers, and rounding must not move the cut.
        var file = await MakeMediaAsync(Path.Combine(_root, "ntsc.mkv"), ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30000/1001:duration=4", "-f", "lavfi",
            "-i", "sine=frequency=440:duration=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"]);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        Assert.Equal(30000.0 / 1001, panel.FrameRate, 3);
        panel.Start = 1.0;                                                    // between frames 29 (0.968 s) and 30 (1.001 s)
        panel.End = 2.5;                                                      // frames 30 to 74 are inside (75 starts at 2.502 s)

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(45, await FrameCountAsync(output));
        Assert.InRange(await VideoStartAsync(output), 0, 0.005);
        const double fps = 30000.0 / 1001;
        using (var expected = await FrameNumberAsync(file, 30, fps))
        using (var before = await FrameNumberAsync(file, 29, fps))
        using (var actual = await FrameNumberAsync(output, 0, fps))
            AssertSameFrame(expected, actual, before, "first frame");
        using (var expected = await FrameNumberAsync(file, 74, fps))
        using (var after = await FrameNumberAsync(file, 75, fps))
        using (var actual = await FrameNumberAsync(output, 44, fps))
            AssertSameFrame(expected, actual, after, "last frame");
    }

    [AvaloniaFact]
    public async Task A_clip_with_an_odd_size_is_cut_by_dropping_its_odd_row_and_column()
    {
        // 4:4:4 can be written at any size, 4:2:0 only at even ones.
        var file = await MakeMediaAsync(Path.Combine(_root, "impar.mp4"), ["-f", "lavfi", "-i", "color=c=orange:s=641x361:r=25:d=3",
            "-c:v", "libx264", "-pix_fmt", "yuv444p"]);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        Assert.Equal((641, 361), (panel.Probe!.Width, panel.Probe.Height));
        panel.Start = 1;
        panel.End = 2;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        var probe = await Media.ProbeAsync(output);
        Assert.Equal((640, 360), (probe.Width, probe.Height));
        Assert.Equal(25, await FrameCountAsync(output));
    }

    [AvaloniaFact]
    public async Task A_cut_that_fails_leaves_nothing_behind_and_says_why()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        panel.Start = 1;
        panel.End = 3;
        await DeleteWhenIdleAsync(panel, file);                               // the clip goes away between opening the panel and Apply

        await panel.RunAsync();

        Assert.True(panel.HasError);
        Assert.False(panel.IsDone);
        Assert.Empty(panel.Outputs);
        Assert.Empty(Directory.GetFiles(_root, "*recortado*"));
        Assert.StartsWith("No se pudo aplicar:", panel.Status);
    }

    [AvaloniaFact]
    public async Task A_cut_that_is_cancelled_removes_the_half_written_file()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => panel.ApplyAsync(new CancelOnProgress(stop, 0.0001), stop.Token));
        Assert.Empty(Directory.GetFiles(_root, "*recortado*"));
    }

    // ───────── Trim: playing ─────────

    [AvaloniaFact]
    public async Task The_kept_stretch_is_played_in_ffplay_and_stopping_closes_it()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        if (!panel.CanPlay)
        {
            Assert.Skip("ffplay is not next to FFmpeg.");
            return;
        }
        var launched = new List<(string Player, IReadOnlyList<string> Arguments)>();
        panel.LaunchPlayer = async (player, arguments, cancel) =>
        {
            launched.Add((player, arguments));
            await Task.Delay(Timeout.Infinite, cancel);                      // "playing" until it is stopped
        };
        panel.Start = 1;
        panel.End = 3.5;

        var playing = panel.PlayAsync();
        Assert.True(panel.IsPlaying);
        var (player, arguments) = Assert.Single(launched);
        var list = arguments.ToList();
        Assert.EndsWith("ffplay.exe", player);
        Assert.Equal(file, list[^1]);
        Assert.Equal("1", list[list.IndexOf("-ss") + 1]);
        Assert.Equal("2.5", list[list.IndexOf("-t") + 1]);
        Assert.Contains("-autoexit", list);
        Assert.Equal("clip.mp4 · Recortar", list[list.IndexOf("-window_title") + 1]);

        await panel.PlayAsync();                                              // already playing: not started twice
        Assert.Single(launched);

        panel.StopPlayback();
        await playing;
        Assert.False(panel.IsPlaying);

        // Closing the panel stops it too.
        var again = panel.PlayAsync();
        Assert.True(panel.IsPlaying);
        panel.Dispose();
        await again;
        Assert.False(panel.IsPlaying);
        Assert.Equal(2, launched.Count);
    }

    [AvaloniaFact]
    public async Task A_player_that_does_not_start_just_leaves_the_button_as_it_was()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenTrimAsync(file);
        if (!panel.CanPlay)
        {
            Assert.Skip("ffplay is not next to FFmpeg.");
            return;
        }
        panel.LaunchPlayer = (_, _, _) => throw new InvalidOperationException("could not start");
        await panel.PlayAsync();
        Assert.False(panel.IsPlaying);
    }

    // ───────── Split ─────────

    [AvaloniaFact]
    public async Task Cuts_are_added_at_the_cursor_listed_in_time_order_and_flagged_on_the_timeline()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        Assert.False(panel.CanApply);                                         // nothing to split yet
        Assert.False(panel.ShowRange);
        Assert.False(panel.HasCuts);
        Assert.Empty(panel.Clips);

        panel.Position = 4;
        Assert.True(panel.AddCutHere());
        panel.Position = 2;
        Assert.True(panel.AddCutHere());
        Assert.True(panel.CanApply);
        Assert.Equal(["0:02.00", "0:04.00"], panel.Cuts.Select(cut => cut.Text));
        Assert.Equal([2.0, 4.0], panel.Markers);
        Assert.Equal(["0:00.00 – 0:02.00", "0:02.00 – 0:04.00", "0:04.00 – 0:06.00"], panel.Clips.Select(clip => clip.Range));
        Assert.Equal(["0:02.00", "0:02.00", "0:02.00"], panel.Clips.Select(clip => clip.Length));
        Assert.Equal([1, 2, 3], panel.Clips.Select(clip => clip.Number));

        panel.RemoveCut(panel.Cuts[0]);
        Assert.Equal([4.0], panel.Markers);
        Assert.Equal(2, panel.Clips.Count);
        panel.RemoveCut(panel.Cuts[0]);
        Assert.False(panel.CanApply);
        Assert.False(panel.HasCuts);
        Assert.Empty(panel.Markers!);
        Assert.Empty(panel.Clips);
    }

    [AvaloniaFact]
    public async Task A_cut_is_refused_when_it_would_leave_a_clip_of_under_a_fifth_of_a_second()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        panel.Position = 0;
        Assert.False(panel.AddCutHere());                                     // at the start
        panel.Position = 0.1;
        Assert.False(panel.AddCutHere());                                     // too near the start
        panel.Position = panel.Duration;
        Assert.False(panel.AddCutHere());                                     // at the end
        panel.Position = 5.9;
        Assert.False(panel.AddCutHere());                                     // too near the end
        Assert.False(panel.HasCuts);

        panel.Position = 3;
        Assert.True(panel.AddCutHere());
        Assert.False(panel.AddCutHere());                                     // the same place again
        panel.Position = 3.1;
        Assert.False(panel.AddCutHere());                                     // within 0.2 s of it
        panel.Position = 2.85;
        Assert.False(panel.AddCutHere());
        panel.Position = 3.4;
        Assert.True(panel.AddCutHere());
        panel.Position = 0.4;
        Assert.True(panel.AddCutHere());
        Assert.Equal(["0:00.40", "0:03.00", "0:03.40"], panel.Cuts.Select(cut => cut.Text));
    }

    [AvaloniaFact]
    public async Task A_cut_between_frames_is_kept_on_the_frame_it_lands_on()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        panel.Position = 2.3456;
        Assert.True(panel.AddCutHere());
        Assert.Equal("0:02.36", Assert.Single(panel.Cuts).Text);              // frame 59, the first one at or after it
        Assert.Equal(2.36, Assert.Single(panel.Markers!), 6);
    }

    [AvaloniaFact]
    public async Task Splitting_evenly_replaces_the_cuts_by_the_ones_of_N_equal_parts()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        panel.Position = 1;
        panel.AddCutHere();

        panel.SplitEvenly(3);
        Assert.Equal([2.0, 4.0], panel.Markers);                              // the cut at 1 s is gone
        Assert.Equal(3, panel.Clips.Count);

        panel.EvenParts = 4;
        Assert.Equal("Dividir en 4 partes iguales", panel.EvenPartsText);
        panel.SplitEvenly();
        Assert.Equal(4, panel.Clips.Count);
        Assert.Equal(["0:01.52", "0:03.00", "0:04.52"], panel.Cuts.Select(cut => cut.Text));    // 1.5 s is frame 37.5: the next frame

        panel.SplitEvenly(1);                                                 // at least two parts
        Assert.Equal(2, panel.Clips.Count);
        panel.SplitEvenly(99);                                                // at most twenty
        Assert.Equal(20, panel.Clips.Count);
        panel.EvenParts = 500;
        Assert.Equal("Dividir en 20 partes iguales", panel.EvenPartsText);
        panel.EvenParts = 0;
        Assert.Equal("Dividir en 2 partes iguales", panel.EvenPartsText);
    }

    [AvaloniaFact]
    public async Task Split_writes_one_clip_per_stretch_that_together_hold_every_frame_once()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var original = await File.ReadAllBytesAsync(file);
        using var panel = await OpenSplitAsync(file);
        panel.Position = 2;
        panel.AddCutHere();
        panel.Position = 4;
        panel.AddCutHere();

        var progress = new Recorder();
        var written = await panel.ApplyAsync(progress, CancellationToken.None);

        var folder = Assert.Single(written);
        Assert.Equal(Path.Combine(_root, "clip clips"), folder);
        Assert.Equal(["clip 1.mp4", "clip 2.mp4", "clip 3.mp4"], Names(folder));
        var clips = new[] { "clip 1.mp4", "clip 2.mp4", "clip 3.mp4" }.Select(name => Path.Combine(folder, name)).ToArray();
        var lengths = new List<double>();
        var frames = 0;
        foreach (var clip in clips)
        {
            var probe = await Media.ProbeAsync(clip);
            lengths.Add(probe.Duration);
            frames += await FrameCountAsync(clip);
            Assert.True(probe.HasAudio);
            Assert.Equal((1280, 720), (probe.Width, probe.Height));
            Assert.InRange(await VideoStartAsync(clip), 0, 0.005);
        }
        Assert.All(lengths, length => Assert.InRange(length, 1.99, 2.01));
        Assert.InRange(lengths.Sum(), 5.97, 6.03);                            // the clips add up to the clip
        Assert.Equal(150, frames);                                            // and not a frame is missing or repeated
        Assert.Equal(original, await File.ReadAllBytesAsync(file));

        // Each clip starts on the frame of its cut and ends on the frame before the next cut.
        using (var expected = await FrameNumberAsync(file, 50))
        using (var before = await FrameNumberAsync(file, 49))
        using (var actual = await FrameNumberAsync(clips[1], 0))
            AssertSameFrame(expected, actual, before, "second clip, first frame");
        using (var expected = await FrameNumberAsync(file, 100))
        using (var before = await FrameNumberAsync(file, 99))
        using (var actual = await FrameNumberAsync(clips[2], 0))
            AssertSameFrame(expected, actual, before, "third clip, first frame");
        using (var expected = await FrameNumberAsync(file, 99))
        using (var after = await FrameNumberAsync(file, 100))
        using (var actual = await FrameNumberAsync(clips[1], 49))
            AssertSameFrame(expected, actual, after, "second clip, last frame");

        // The progress runs from the start to the end over the sum of the clips.
        Assert.Equal(progress.Values.Order(), progress.Values);
        Assert.Equal(1.0, progress.Values[^1], 6);
        Assert.Contains(progress.Values, value => value is > 0.2 and < 0.8);
    }

    [AvaloniaFact]
    public async Task Split_into_equal_parts_numbers_the_clips_with_a_zero_in_front_from_ten_on()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        panel.SplitEvenly(4);
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var folder = Assert.Single(panel.Outputs);
        Assert.Equal(["clip 1.mp4", "clip 2.mp4", "clip 3.mp4", "clip 4.mp4"], Names(folder));
        var frames = 0;
        foreach (var clip in Directory.GetFiles(folder))
            frames += await FrameCountAsync(clip);
        Assert.Equal(150, frames);

        using var many = await OpenSplitAsync(file);
        many.SplitEvenly(12);
        many.NoRecode = true;                                                 // only the names are looked at here: copying is quick
        await many.RunAsync();
        Assert.True(many.IsDone, many.Status);
        var second = Assert.Single(many.Outputs);
        Assert.Equal(Path.Combine(_root, "clip clips 2"), second);            // the first folder is not reused
        Assert.Equal(Enumerable.Range(1, 12).Select(n => $"clip {n:00}.mp4"), Names(second));

        Assert.Equal("clip 007.mp4", SplitVideoPanel.ClipName(@"C:\v\clip.mp4", 7, 120));
        Assert.Equal("clip 9.mp4", SplitVideoPanel.ClipName(@"C:\v\clip.mp4", 9, 9));
        Assert.Equal("clip 10.mp4", SplitVideoPanel.ClipName(@"C:\v\clip.mp4", 10, 10));
    }

    [AvaloniaFact]
    public async Task Split_without_re_encoding_copies_each_stretch_starting_on_the_key_frame_of_each_cut()
    {
        var file = await MakeVideoWithKeyFramesAsync("clip.mp4");            // key frames at 0, 1, 2, … seconds
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        panel.Position = 2;
        panel.AddCutHere();
        panel.Position = 4;
        panel.AddCutHere();
        panel.NoRecode = true;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var folder = Assert.Single(panel.Outputs);
        Assert.Equal(["clip 1.mp4", "clip 2.mp4", "clip 3.mp4"], Names(folder));
        var clips = Names(folder).Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var clip in clips)
        {
            var probe = await Media.ProbeAsync(clip);
            Assert.Equal("h264", probe.VideoCodec);
            Assert.True(probe.HasAudio);
            Assert.InRange(probe.Duration, 1.95, 2.4);                        // two seconds, and the few frames a copy reads past the cut
        }

        // The cuts are on key frames, so the second and third clips begin on exactly the frame of the cut (the same pixels,
        // nothing was decoded), not on the key frame a second earlier.
        using (var expected = await FrameNumberAsync(file, 50))
        using (var actual = await FrameNumberAsync(clips[1], 0))
            Assert.True(Difference(expected, actual) < 0.001, "the second clip does not start on its cut");
        using (var expected = await FrameNumberAsync(file, 100))
        using (var actual = await FrameNumberAsync(clips[2], 0))
            Assert.True(Difference(expected, actual) < 0.001, "the third clip does not start on its cut");
    }

    [AvaloniaFact]
    public async Task Split_without_re_encoding_works_on_mkv_too()
    {
        var file = await MakeVideoWithKeyFramesAsync("clip.mkv");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        panel.SplitEvenly(3);
        panel.NoRecode = true;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var folder = Assert.Single(panel.Outputs);
        Assert.Equal(["clip 1.mkv", "clip 2.mkv", "clip 3.mkv"], Names(folder));
        foreach (var clip in Directory.GetFiles(folder))
        {
            var probe = await Media.ProbeAsync(clip);
            Assert.Equal("h264", probe.VideoCodec);
            Assert.True(probe.HasAudio);
            Assert.InRange(probe.Duration, 1.9, 3.4);                         // each starts on the key frame at or before its cut: up to a second early
        }
    }

    [AvaloniaFact]
    public async Task A_split_that_fails_removes_the_folder_it_made_and_says_why()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        panel.SplitEvenly(3);
        await DeleteWhenIdleAsync(panel, file);

        await panel.RunAsync();

        Assert.True(panel.HasError);
        Assert.False(panel.IsDone);
        Assert.Empty(panel.Outputs);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [AvaloniaFact]
    public async Task A_split_that_is_cancelled_half_way_takes_the_clips_already_written_with_it()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSplitAsync(file);
        panel.SplitEvenly(3);
        using var stop = new CancellationTokenSource();
        // The first clip is a third of the work: the run is stopped as it is done, with the second one still to write.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => panel.ApplyAsync(new CancelOnProgress(stop, 0.3), stop.Token));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    // ───────── Snapshots ─────────

    [Theory]
    [InlineData(5.3, "00-05.30")]
    [InlineData(0, "00-00.00")]
    [InlineData(65, "01-05.00")]
    [InlineData(599.99, "09-59.99")]
    [InlineData(3723.4, "1-02-03.40")]
    public void A_snapshot_is_named_after_its_time_without_colons(double seconds, string expected) =>
        Assert.Equal(expected, VideoSnapshotsPanel.Stamp(seconds));

    [AvaloniaFact]
    public async Task Frames_are_picked_at_the_cursor_listed_in_time_order_once_each_and_flagged_on_the_timeline()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSnapshotsAsync(file);
        Assert.True(panel.IsPickMode);
        Assert.False(panel.ShowRange);
        Assert.False(panel.CanApply);                                         // nothing picked yet
        Assert.False(panel.HasCaptures);

        panel.Position = 5;
        Assert.True(panel.CaptureHere());
        panel.Position = 1;
        Assert.True(panel.CaptureHere());
        panel.Position = 2.52;
        Assert.True(panel.CaptureHere());
        Assert.Equal(["0:01.00", "0:02.52", "0:05.00"], panel.Captures.Select(capture => capture.Text));
        Assert.Equal([1.0, 2.52, 5.0], panel.Markers);
        Assert.True(panel.CanApply);
        Assert.Equal(3, panel.Count);

        panel.Position = 2.51;                                                // the first frame at or after 2.51 s is the one at 2.52 s
        Assert.False(panel.CaptureHere());                                    // the same frame twice is one capture
        panel.Position = 2.53;                                                // the next frame
        Assert.True(panel.CaptureHere());
        Assert.Equal(4, panel.Captures.Count);

        panel.RemoveCapture(panel.Captures[1]);
        Assert.Equal(["0:01.00", "0:02.56", "0:05.00"], panel.Captures.Select(capture => capture.Text));
        Assert.Equal([1.0, 2.56, 5.0], panel.Markers);

        // Each has a small picture of its own frame.
        await WaitAsync(() => panel.Captures.All(capture => capture.Thumb is not null));
        Assert.All(panel.Captures, capture => Assert.Equal(160, capture.Thumb!.Size.Width));
    }

    [AvaloniaFact]
    public async Task Snapshots_of_picked_frames_are_png_files_of_the_real_resolution_named_after_their_time()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var original = await File.ReadAllBytesAsync(file);
        using var panel = await OpenSnapshotsAsync(file);
        foreach (var time in new[] { 1.0, 2.52, 5.0 })
        {
            panel.Position = time;
            panel.CaptureHere();
        }

        var progress = new Recorder();
        var written = await panel.ApplyAsync(progress, CancellationToken.None);

        var folder = Assert.Single(written);
        Assert.Equal(Path.Combine(_root, "clip capturas"), folder);
        var names = Names(folder);
        Assert.Equal(["clip 00-01.00.png", "clip 00-02.52.png", "clip 00-05.00.png"], names);
        foreach (var name in names)
            Assert.Equal((1280u, 720u), SizeOf(Path.Combine(folder, name!)));             // not shrunk to the preview
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
        Assert.Equal([0.333333, 0.666667, 1.0], progress.Values.Select(value => Math.Round(value, 6)));       // one step per picture

        // Each is the very frame of the original at that time, and not its neighbour.
        foreach (var frame in new[] { 25, 63, 125 })
        {
            using var picture = new MagickImage(Path.Combine(folder, $"clip {VideoSnapshotsPanel.Stamp(frame / 25.0)}.png"));
            using var expected = await FrameNumberAsync(file, frame);
            using var next = await FrameNumberAsync(file, frame + 1);
            Assert.True(Difference(expected, picture) < 0.0005, $"the snapshot of frame {frame} is not that frame");
            Assert.True(Difference(next, picture) > 0.01, $"the snapshot of frame {frame} cannot be told from the next frame");
        }
    }

    [AvaloniaFact]
    public async Task The_snapshot_saved_is_the_frame_the_big_picture_shows_there()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSnapshotsAsync(file);
        var shown = panel.Frame;
        panel.Position = 3.7777;                                              // between frames: the first one at or after it is frame 95, at 3.8 s
        await WaitAsync(() => !ReferenceEquals(panel.Frame, shown));
        await PumpForAsync(300);
        var preview = SavePicture(Path.Combine(_root, "preview.png"), panel.Frame!);
        panel.CaptureHere();
        Assert.Equal("0:03.80", Assert.Single(panel.Captures).Text);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var saved = Path.Combine(Assert.Single(panel.Outputs), "clip 00-03.80.png");
        using var picture = Small(new MagickImage(saved));
        using var seen = Small(new MagickImage(preview));
        using var neighbour = Small(await FrameNumberAsync(file, 94));
        AssertSameFrame(seen, picture, neighbour, "snapshot against the preview");
    }

    [AvaloniaFact]
    public async Task Snapshots_at_an_interval_take_one_every_N_seconds_between_the_handles()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSnapshotsAsync(file);
        panel.SelectMode(pick: false);
        Assert.True(panel.IsIntervalMode);
        Assert.True(panel.ShowRange);
        Assert.Null(panel.Markers);
        Assert.Equal(5, panel.Interval);

        panel.Interval = 2;                                                   // 0, 2 and 4 s: the 6 s end is not a frame
        Assert.Equal(3, panel.Count);
        Assert.Equal("Saldrán 3 capturas", panel.CountText);
        panel.Interval = 1;
        Assert.Equal(6, panel.Count);
        panel.Interval = 4;
        Assert.Equal(2, panel.Count);                                         // 0 and 4
        panel.Interval = 60;                                                  // longer than the clip: the first frame
        Assert.Equal(1, panel.Count);
        Assert.Equal("Saldrá 1 captura", panel.CountText);
        panel.Start = 1;
        panel.End = 5;
        panel.Interval = 2;                                                   // 1 and 3: the range stops before 5
        Assert.Equal(2, panel.Count);
        Assert.Equal([25, 75], panel.IntervalFrames());
        panel.Interval = 1;
        Assert.Equal(4, panel.Count);
        Assert.True(panel.CanApply);

        // Apply: one picture per instant, named after its time.
        panel.Interval = 2;
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var folder = Assert.Single(panel.Outputs);
        Assert.Equal(["clip 00-01.00.png", "clip 00-03.00.png"], Names(folder));
        Assert.All(Directory.GetFiles(folder), picture => Assert.Equal((1280u, 720u), SizeOf(picture)));
        using var third = new MagickImage(Path.Combine(folder, "clip 00-03.00.png"));
        using var expected = await FrameNumberAsync(file, 75);
        Assert.True(Difference(expected, third) < 0.0005);
    }

    [AvaloniaFact]
    public async Task An_interval_over_the_whole_clip_gives_the_expected_number_of_pictures()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSnapshotsAsync(file);
        panel.SelectMode(pick: false);
        panel.Interval = 2;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var folder = Assert.Single(panel.Outputs);
        Assert.Equal(["clip 00-00.00.png", "clip 00-02.00.png", "clip 00-04.00.png"], Names(folder));
    }

    [AvaloniaFact]
    public async Task Snapshots_of_a_phone_video_turned_by_its_rotation_tag_come_out_upright()
    {
        var plain = await MakeVideoAsync("recto.mp4");
        var turned = plain is null
            ? null
            : await MakeMediaAsync(Path.Combine(_root, "girado.mp4"), ["-display_rotation", "90", "-i", plain, "-c", "copy"]);
        if (turned is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSnapshotsAsync(turned);
        Assert.Equal((720, 1280), (panel.Probe!.Width, panel.Probe.Height));              // 1280 × 720 stored, 720 × 1280 shown
        panel.Position = 2;
        panel.CaptureHere();
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var picture = Assert.Single(Directory.GetFiles(Assert.Single(panel.Outputs)));
        Assert.Equal((720u, 1280u), SizeOf(picture));
    }

    [AvaloniaFact]
    public async Task A_snapshot_run_that_fails_removes_its_folder_and_says_why()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        using var panel = await OpenSnapshotsAsync(file);
        panel.Position = 1;
        panel.CaptureHere();
        panel.Position = 2;
        panel.CaptureHere();
        await DeleteWhenIdleAsync(panel, file);

        await panel.RunAsync();

        Assert.True(panel.HasError);
        Assert.Empty(panel.Outputs);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    // ───────── The windows ─────────

    [AvaloniaFact]
    public async Task Pressing_and_dragging_on_the_timeline_of_the_window_moves_the_cursor_and_the_handles_of_the_panel()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenTrimAsync(file);
        await WaitAsync(() => panel.Strip is not null);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        try
        {
            var timeline = window.GetVisualDescendants().OfType<VideoTimeline>().Single();
            Assert.Equal(panel.Duration, timeline.Duration);
            Assert.NotNull(timeline.Strip);
            Assert.Equal(25, timeline.FrameRate, 3);
            Point At(double seconds) => timeline.TranslatePoint(new Point(timeline.XOf(seconds), 34), window)!.Value;

            // A click on the track puts the cursor there, and the picture follows.
            var shown = panel.Frame;
            window.MouseDown(At(3), MouseButton.Left);
            window.MouseUp(At(3), MouseButton.Left);
            Pump();
            Assert.InRange(panel.Position, 2.95, 3.05);
            Assert.Equal(panel.Position, timeline.Position);
            await WaitAsync(() => !ReferenceEquals(panel.Frame, shown));

            // The start handle is dragged with the pointer; the cursor goes with it.
            window.MouseDown(At(0), MouseButton.Left);
            window.MouseMove(At(1.5));
            window.MouseUp(At(1.5), MouseButton.Left);
            Pump();
            Assert.InRange(panel.Start, 1.45, 1.55);
            Assert.Equal(panel.Start, panel.Position);
            Assert.Equal(panel.Duration, panel.End);

            // The end handle too.
            window.MouseDown(At(panel.Duration), MouseButton.Left);
            window.MouseMove(At(4.5));
            window.MouseUp(At(4.5), MouseButton.Left);
            Pump();
            Assert.InRange(panel.End, 4.45, 4.55);
            Assert.InRange(panel.Start, 1.45, 1.55);
            Assert.InRange(panel.End - panel.Start, 2.9, 3.1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_buttons_of_the_trim_window_step_mark_and_choose_how_to_cut()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenTrimAsync(file);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        try
        {
            void Press(Control control)
            {
                var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Pump();
            }
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(button => button is not ToggleButton && button.Classes.Contains("pchip")).ToList();

            var next = buttons.Single(button => ToolTip.GetTip(button) as string == "Un fotograma adelante");
            Press(next);
            Press(next);
            Assert.Equal(0.08, panel.Position, 6);
            Press(buttons.Single(button => ToolTip.GetTip(button) as string == "Un fotograma atrás"));
            Assert.Equal(0.04, panel.Position, 6);

            panel.Position = 2.0;
            Press(buttons.Single(button => (button.Content as TextBlock)?.Text == "Marcar inicio aquí"));
            Assert.Equal(2.0, panel.Start);
            panel.Position = 4.0;
            Press(buttons.Single(button => (button.Content as TextBlock)?.Text == "Marcar fin aquí"));
            Assert.Equal(4.0, panel.End);
            Assert.Equal("0:02.00", panel.SelectionText);

            var check = window.GetVisualDescendants().OfType<CheckBox>().Single();
            Assert.False(check.IsChecked);
            Press(check);
            Assert.True(panel.NoRecode);

            // A time typed in the box is taken on Enter, and the box then shows the panel's own writing of it.
            var box = window.GetVisualDescendants().OfType<TextBox>().First();
            box.SetCurrentValue(TextBox.TextProperty, "1.5");
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Pump();
            Assert.Equal(1.5, panel.Start);
            Assert.Equal("0:01.50", box.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_buttons_of_the_split_window_add_and_remove_cuts_and_split_evenly()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenSplitAsync(file);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        try
        {
            void Press(Control control)
            {
                var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Pump();
            }
            Button ButtonSaying(string text) => window.GetVisualDescendants().OfType<Button>().Single(button => (button.Content as TextBlock)?.Text == text);
            var timeline = window.GetVisualDescendants().OfType<VideoTimeline>().Single();
            Assert.False(timeline.ShowRange);

            panel.Position = 2.0;
            Press(ButtonSaying("Añadir corte aquí"));
            Assert.Equal("0:02.00", Assert.Single(panel.Cuts).Text);
            Assert.Equal([2.0], timeline.Markers);                              // the flag is on the timeline

            Press(window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("pcross")));
            Assert.False(panel.HasCuts);

            Press(ButtonSaying("Dividir en 3 partes iguales"));
            Assert.Equal(2, panel.Cuts.Count);
            Assert.Equal(3, panel.Clips.Count);

            // The stepper feeds the panel: plus, plus, and the button says the new number.
            var stepper = window.GetVisualDescendants().OfType<VideoStepper>().Single();
            var more = stepper.GetVisualDescendants().OfType<RepeatButton>().Last();
            Press(more);
            Press(more);
            Assert.Equal(5, panel.EvenParts);
            Assert.Equal(5, stepper.Value);
            Press(ButtonSaying("Dividir en 5 partes iguales"));
            Assert.Equal(5, panel.Clips.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_buttons_of_the_snapshot_window_pick_the_mode_and_capture_frames()
    {
        var file = await MakeVideoAsync("clip.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenSnapshotsAsync(file);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        try
        {
            void Press(Control control)
            {
                var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Pump();
            }
            var pills = window.GetVisualDescendants().OfType<ToggleButton>().ToList();
            Assert.Equal([true, false], pills.Select(pill => pill.IsChecked == true));

            panel.Position = 1.0;
            Press(window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("primary") && button.IsVisible));
            Assert.Single(panel.Captures);
            Press(window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("pshot")));
            Assert.Empty(panel.Captures);

            Press(pills[1]);                                                    // every N seconds
            Assert.False(panel.IsPickMode);
            Assert.Equal([false, true], pills.Select(pill => pill.IsChecked == true));
            Assert.True(window.GetVisualDescendants().OfType<VideoTimeline>().Single().ShowRange);
            Press(pills[1]);                                                    // pressing the mode in use leaves it on
            Assert.True(pills[1].IsChecked);
            Press(pills[0]);
            Assert.True(panel.IsPickMode);
            Assert.Equal([true, false], pills.Select(pill => pill.IsChecked == true));
        }
        finally
        {
            window.Close();
        }
    }

    // ───────── Renders ─────────

    [AvaloniaFact]
    public async Task The_trim_panel_renders_with_its_frame_film_strip_and_handles()
    {
        var file = await MakeVideoAsync("entrevista.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenTrimAsync(file);
        await WaitAsync(() => panel.Strip is not null);
        var shown = panel.Frame;
        panel.Start = 1.52;
        panel.End = 4.2;
        panel.Position = 2.4;
        await WaitAsync(() => !ReferenceEquals(panel.Frame, shown));
        await PumpForAsync(200);

        Render(panel, "video-trim");
        Assert.Equal("Recortar", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_split_panel_renders_with_its_cuts_and_clips()
    {
        var file = await MakeVideoAsync("entrevista.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenSplitAsync(file);
        await WaitAsync(() => panel.Strip is not null);
        foreach (var time in new[] { 1.52, 3.0, 4.2 })
        {
            panel.Position = time;
            panel.AddCutHere();
        }
        panel.Position = 3.0;
        await PumpForAsync(800);

        Render(panel, "video-split");
        Assert.Equal("Dividir", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_snapshot_panel_renders_with_picked_frames()
    {
        var file = await MakeVideoAsync("entrevista.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenSnapshotsAsync(file);
        await WaitAsync(() => panel.Strip is not null);
        foreach (var time in new[] { 0.52, 1.0, 2.52, 3.84, 5.0 })
        {
            panel.Position = time;
            panel.CaptureHere();
        }
        panel.Position = 3.84;
        await WaitAsync(() => panel.Captures.All(capture => capture.Thumb is not null));
        await PumpForAsync(800);

        Render(panel, "video-snapshots");
        Assert.Equal("Capturas", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_snapshot_panel_renders_the_interval_mode()
    {
        var file = await MakeVideoAsync("entrevista.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenSnapshotsAsync(file);
        await WaitAsync(() => panel.Strip is not null);
        panel.SelectMode(pick: false);
        panel.Start = 0.52;
        panel.End = 5.0;
        panel.Interval = 1;
        await PumpForAsync(800);

        Render(panel, "video-snapshots-interval");
        Assert.Equal("Saldrán 5 capturas", panel.CountText);
    }

    [AvaloniaFact]
    public async Task The_split_panel_renders_twenty_clips_and_the_snapshot_panel_a_dozen_pictures_without_overflowing()
    {
        var file = await MakeVideoAsync("entrevista.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var split = await OpenSplitAsync(file);
        await WaitAsync(() => split.Strip is not null);
        split.SplitEvenly(20);
        Assert.Equal(20, split.Clips.Count);
        await PumpForAsync(400);
        Render(split, "video-split-many");

        var snapshots = await OpenSnapshotsAsync(file);
        await WaitAsync(() => snapshots.Strip is not null);
        for (var i = 1; i <= 14; i++)
        {
            snapshots.Position = i * 0.4;
            snapshots.CaptureHere();
        }
        await WaitAsync(() => snapshots.Captures.All(capture => capture.Thumb is not null));
        await PumpForAsync(400);
        Render(snapshots, "video-snapshots-many");
    }

    [AvaloniaFact]
    public async Task The_trim_panel_renders_while_the_clip_is_playing()
    {
        var file = await MakeVideoAsync("entrevista.mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenTrimAsync(file);
        if (!panel.CanPlay)
        {
            Assert.Skip("ffplay is not next to FFmpeg.");
            return;
        }
        await WaitAsync(() => panel.Strip is not null);
        panel.LaunchPlayer = (_, _, cancel) => Task.Delay(Timeout.Infinite, cancel);
        panel.Start = 1;
        panel.End = 4;
        _ = panel.PlayAsync();
        Assert.True(panel.IsPlaying);
        await PumpForAsync(400);

        Render(panel, "video-trim-playing");
        await WaitAsync(() => !panel.IsPlaying);                              // the window closed with the render: the player was stopped
    }

    [AvaloniaFact]
    public async Task The_trim_panel_renders_a_portrait_clip_in_english()
    {
        var plain = await MakeVideoAsync("recto.mp4");
        var turned = plain is null
            ? null
            : await MakeMediaAsync(Path.Combine(_root, "portrait.mp4"), ["-display_rotation", "90", "-i", plain, "-c", "copy"]);
        if (turned is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenTrimAsync(turned, "en");
        await WaitAsync(() => panel.Strip is not null);
        panel.Start = 1;
        panel.End = 4;
        await PumpForAsync(500);

        Render(panel, "video-trim-portrait-en");
        Assert.Equal("Trim", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_trim_panel_renders_the_reason_when_the_file_has_no_video()
    {
        var file = await MakeMediaAsync(Path.Combine(_root, "solo audio.mp3"), ["-f", "lavfi", "-i", "sine=frequency=440:duration=1"]);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new TrimVideoPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);

        Render(panel, "video-trim-error");
    }

    [AvaloniaTheory]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_three_panels_fit_in_the_other_languages(string language)
    {
        var file = await MakeVideoAsync("clip-" + language + ".mp4");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var trim = await OpenTrimAsync(file, language);
        await WaitAsync(() => trim.Strip is not null);
        trim.Start = 1;
        trim.End = 4;
        await PumpForAsync(400);
        Render(trim, "video-trim-" + language);

        var split = await OpenSplitAsync(file, language);
        await WaitAsync(() => split.Strip is not null);
        split.SplitEvenly(4);
        await PumpForAsync(400);
        Render(split, "video-split-" + language);

        var snapshots = await OpenSnapshotsAsync(file, language);
        await WaitAsync(() => snapshots.Strip is not null);
        snapshots.Position = 1;
        snapshots.CaptureHere();
        snapshots.Position = 3;
        snapshots.CaptureHere();
        await WaitAsync(() => snapshots.Captures.All(capture => capture.Thumb is not null));
        await PumpForAsync(400);
        Render(snapshots, "video-snapshots-" + language);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Every_text_of_the_video_panels_exists_in_every_language(string language)
    {
        var context = ContextIn(language, Path.Combine(_root, "clip.mp4"));
        foreach (var key in new[]
                 {
                     "panel.videoStage.loading", "panel.videoStage.no_video", "panel.videoStage.no_duration", "panel.videoStage.no_recode",
                     "panel.trimVideo.frame", "panel.trimVideo.prev_frame", "panel.trimVideo.next_frame", "panel.trimVideo.play", "panel.trimVideo.stop",
                     "panel.trimVideo.hint", "panel.trimVideo.start", "panel.trimVideo.end", "panel.trimVideo.mark_start", "panel.trimVideo.mark_end",
                     "panel.trimVideo.kept", "panel.splitVideo.hint", "panel.splitVideo.add_cut", "panel.splitVideo.cuts", "panel.splitVideo.remove_cut",
                     "panel.splitVideo.no_cuts", "panel.splitVideo.even_parts", "panel.splitVideo.clips", "panel.videoSnapshots.hint_pick",
                     "panel.videoSnapshots.hint_interval", "panel.videoSnapshots.mode_pick", "panel.videoSnapshots.mode_interval",
                     "panel.videoSnapshots.capture", "panel.videoSnapshots.remove", "panel.videoSnapshots.empty", "panel.videoSnapshots.every",
                     "panel.videoSnapshots.seconds", "panel.videoSnapshots.count_one", "panel.videoSnapshots.count_many", "panel.videoSnapshots.no_frame",
                 })
            Assert.NotEqual(key, context[key]);
        Assert.Contains("4", context.Format("panel.splitVideo.even_parts", 4));
        Assert.Contains("4", context.Format("panel.videoSnapshots.count_many", 4));
        Assert.Contains("0:04.00", context.Format("panel.videoSnapshots.no_frame", "0:04.00"));
    }
}
