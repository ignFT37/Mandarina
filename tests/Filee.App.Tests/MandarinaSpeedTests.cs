// Mandarina: SPEED and JOIN for video. The arithmetic (SpeedFilter, JoinPlan) is checked on its own; the panels
// run the real FFmpeg on small clips, and their results are probed by length, size and sound, and looked at as pictures.

using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaSpeedTests : IDisposable
{
    private const string NoFfmpeg = "FFmpeg is not installed next to the tests (engines\\ffmpeg).";

    /// <summary>320×240 with a picture full of structure, so a wrong frame or offset shows.</summary>
    private const string Picture320 = "testsrc2=size=320x240:rate=25";

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-speed").FullName;

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

    private static MediaProbe Probe(double seconds, int width, int height, double fps, bool audio) =>
        new(seconds, true, audio, width, height, fps, audio ? 2 : 0, audio ? 48000 : 0, new Dictionary<string, string>(),
            audio ? "aac" : null, "h264");

    /// <summary>A clip made by FFmpeg from a lavfi picture (<paramref name="picture"/>), with a 440 Hz tone when <paramref name="withAudio"/>.</summary>
    private Task<string?> MakeClipAsync(string name, string picture, double seconds, bool withAudio = true) =>
        withAudio
            ? MakeMediaAsync(Path.Combine(_root, name), "-f", "lavfi", "-i", picture, "-f", "lavfi", "-i", "sine=frequency=440",
                "-t", Seconds(seconds), "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest")
            : MakeMediaAsync(Path.Combine(_root, name), "-f", "lavfi", "-i", picture, "-t", Seconds(seconds), "-c:v", "libx264",
                "-pix_fmt", "yuv420p");

    /// <summary>A plain colour clip of two seconds, 320×240 and silent (the colour tells the clips apart in the picture).</summary>
    private Task<string?> MakeSolidAsync(string name, string colour) =>
        MakeMediaAsync(Path.Combine(_root, name), "-f", "lavfi", "-i", $"color=c={colour}:size=320x240:rate=25", "-t", "2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p");

    private static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private async Task<ChangeVideoSpeedPanel> OpenSpeedAsync(string file)
    {
        var panel = new ChangeVideoSpeedPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    private async Task<JoinVideosPanel> OpenJoinAsync(params string[] files)
    {
        var panel = new JoinVideosPanel(Context(files));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    /// <summary>
    /// How far the first frame of <paramref name="video"/> is from a plain <paramref name="colour"/> (ImageMagick's mean
    /// absolute error: 0 is the same colour). The frame comes from <see cref="MediaTools.FrameAsync"/>, at full size.
    /// </summary>
    private static async Task<double> FirstFrameDistanceAsync(string video, string colour)
    {
        using var frame = await MediaTools.Locate()!.FrameAsync(video, 0, 0);
        using var stream = new MemoryStream();
        frame.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        using var picture = new MagickImage(stream);
        using var plain = new MagickImage($"xc:{colour}", picture.Width, picture.Height);
        return picture.Compare(plain, ErrorMetric.MeanAbsolute);
    }

    // ───────── Speed: the arithmetic of the filter ─────────

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.3)]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    [InlineData(8.0)]
    public void Tempo_steps_stay_between_half_and_double_and_multiply_to_the_speed(double speed)
    {
        var steps = SpeedFilter.TempoSteps(speed);
        Assert.All(steps, step => Assert.InRange(step, 0.5, 2));
        Assert.Equal(speed, steps.Aggregate(1.0, (product, step) => product * step), 9);
    }

    [Fact]
    public void Four_times_is_two_steps_of_two_and_a_quarter_is_two_of_a_half()
    {
        Assert.Equal(new[] { 2.0, 2.0 }, SpeedFilter.TempoSteps(4));
        Assert.Equal(new[] { 0.5, 0.5 }, SpeedFilter.TempoSteps(0.25));
        Assert.Equal(new[] { 2.0, 2.0, 2.0 }, SpeedFilter.TempoSteps(8));
        Assert.Equal(new[] { 2.0, 1.5 }, SpeedFilter.TempoSteps(3));
    }

    [Fact]
    public void One_times_changes_nothing_and_has_no_arguments()
    {
        Assert.False(SpeedFilter.Changes(1));
        Assert.Empty(SpeedFilter.TempoSteps(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedFilter.Build(1, hasAudio: true));
    }

    [Fact]
    public void The_arguments_map_the_picture_and_stretch_the_sound_with_a_decimal_point_whatever_the_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // a comma would break the filter
        try
        {
            Assert.Equal(new[] { "-filter_complex", "[0:v:0]setpts=PTS/0.75[v];[0:a:0]atempo=0.75[a]", "-map", "[v]", "-map", "[a]" },
                SpeedFilter.Build(0.75, hasAudio: true));
            Assert.Equal(new[] { "-filter_complex", "[0:v:0]setpts=PTS/4[v];[0:a:0]atempo=2,atempo=2[a]", "-map", "[v]", "-map", "[a]" },
                SpeedFilter.Build(4, hasAudio: true));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Without_sound_there_is_no_audio_chain()
    {
        Assert.Equal(new[] { "-filter_complex", "[0:v:0]setpts=PTS/2[v]", "-map", "[v]" }, SpeedFilter.Build(2, hasAudio: false));
    }

    // ───────── Join: the plan ─────────

    [Fact]
    public void The_join_takes_the_size_and_rate_of_the_first_clip_and_writes_silence_for_a_silent_one()
    {
        var first = Probe(3, 320, 240, 25, audio: true);
        var second = Probe(2, 640, 360, 30, audio: false);
        var plan = JoinPlan.Build([new JoinInput("a.mp4", first), new JoinInput("b.mp4", second)], JoinSize.AsFirst);

        Assert.Equal((320, 240), (plan.Width, plan.Height));
        Assert.Equal(25.0, plan.FrameRate, 9);
        Assert.Equal(5.0, plan.Duration, 9);
        Assert.Equal(new[] { "a.mp4", "b.mp4" }, plan.Inputs);
        Assert.Contains("[0:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a_0];", plan.Graph);
        Assert.Contains("[1:v:0]scale=320:240:force_original_aspect_ratio=decrease,pad=320:240:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=25,format=yuv420p[v_1];",
            plan.Graph);
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=2[a_1];", plan.Graph);
        Assert.EndsWith("[v_0][a_0][v_1][a_1]concat=n=2:v=1:a=1[v][a]", plan.Graph);
        Assert.DoesNotContain("fps=30", plan.Graph);
    }

    [Fact]
    public void A_fixed_size_is_1080p_or_720p_and_an_odd_first_size_is_rounded_down()
    {
        var clips = new[] { new JoinInput("a.mp4", Probe(1, 321, 241, 25, true)), new JoinInput("b.mp4", Probe(1, 321, 241, 25, true)) };
        var first = JoinPlan.Build(clips, JoinSize.AsFirst);
        Assert.Equal((320, 240), (first.Width, first.Height));

        var hd = JoinPlan.Build(clips, JoinSize.Hd1080);
        Assert.Equal((1920, 1080), (hd.Width, hd.Height));
        Assert.Contains("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:", hd.Graph);

        var small = JoinPlan.Build(clips, JoinSize.Hd720);
        Assert.Equal((1280, 720), (small.Width, small.Height));
    }

    [Fact]
    public void A_first_clip_without_a_frame_rate_gives_thirty_and_one_clip_is_no_join()
    {
        var clips = new[] { new JoinInput("a.mp4", Probe(1, 320, 240, 0, true)), new JoinInput("b.mp4", Probe(1, 320, 240, 25, true)) };
        Assert.Equal(30.0, JoinPlan.Build(clips, JoinSize.AsFirst).FrameRate, 9);
        Assert.Throws<ArgumentException>(() => JoinPlan.Build(clips.Take(1).ToArray(), JoinSize.AsFirst));
    }

    // ───────── Speed: the panel and the real FFmpeg ─────────

    [AvaloniaFact]
    public async Task The_speed_pills_and_the_slider_move_together_in_steps_of_five_hundredths()
    {
        var file = await MakeClipAsync("pelicula.mp4", Picture320, 6);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenSpeedAsync(file);

        Assert.Equal(new[] { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 }, panel.Presets.Select(preset => preset.Value).ToArray());
        Assert.Equal(1.0, panel.Speed);
        Assert.Equal(1.0, panel.Presets.Single(preset => preset.IsSelected).Value);

        panel.Speed = 1.234;                                  // a slider position between two steps
        Assert.Equal(1.25, panel.Speed, 9);
        Assert.Equal(1.25, panel.Presets.Single(preset => preset.IsSelected).Value);

        panel.SelectSpeed(panel.Presets[0]);
        Assert.Equal(0.25, panel.Speed, 9);
        Assert.Equal(0.25, panel.Presets.Single(preset => preset.IsSelected).Value);

        panel.Speed = 9;                                      // beyond the slider's range
        Assert.Equal(SpeedFilter.Fastest, panel.Speed, 9);
        panel.Speed = 0.1;
        Assert.Equal(SpeedFilter.Slowest, panel.Speed, 9);
    }

    [AvaloniaFact]
    public async Task Normal_speed_has_nothing_to_apply_and_the_length_is_shown_before_and_after()
    {
        var file = await MakeClipAsync("pelicula.mp4", Picture320, 6);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenSpeedAsync(file);
        Assert.False(panel.CanApply);                         // 1×: the video would come out as it went in
        Assert.Equal("Duración: 0:06.00 → 0:06.00", panel.DurationText);

        panel.SelectSpeed(panel.Presets[6]);                  // 2×
        Assert.True(panel.CanApply);
        Assert.Equal("Duración: 0:06.00 → 0:03.00", panel.DurationText);
    }

    [AvaloniaFact]
    public async Task A_file_without_video_says_so_and_cannot_be_applied()
    {
        var sound = await MakeMediaAsync(Path.Combine(_root, "sonido.wav"), "-f", "lavfi", "-i", "sine=frequency=440:duration=2");
        if (sound is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new ChangeVideoSpeedPanel(Context(sound));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Equal("Este archivo no tiene vídeo.", panel.LoadError);
        Assert.False(panel.IsReady);
        panel.SelectSpeed(panel.Presets[6]);
        Assert.False(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task Double_speed_halves_a_six_second_clip_and_keeps_its_sound_in_step_and_the_original()
    {
        var file = await MakeClipAsync("clip.mp4", Picture320, 6);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var before = await File.ReadAllBytesAsync(file);
        var panel = await OpenSpeedAsync(file);
        panel.SelectSpeed(panel.Presets[6]);                  // 2×

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "clip velocidad.mp4"), output);
        var probe = await media.ProbeAsync(output);
        Assert.InRange(probe.Duration, 2.8, 3.2);             // 3,0 s, give or take
        Assert.Equal((320, 240), (probe.Width, probe.Height));
        Assert.True(probe.HasAudio);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal("aac", probe.AudioCodec);

        // The sound on its own is as long as the picture.
        var sound = await MakeMediaAsync(Path.Combine(_root, "sonido.wav"), "-i", output, "-vn", "-c:a", "pcm_s16le");
        Assert.InRange((await media.ProbeAsync(sound!)).Duration, 2.8, 3.2);

        Assert.Equal(before, await File.ReadAllBytesAsync(file)); // the original is untouched
    }

    [AvaloniaFact]
    public async Task Half_speed_doubles_the_length_and_a_repeated_name_gets_a_number()
    {
        var file = await MakeClipAsync("clip.mp4", Picture320, 6);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;

        var slow = await OpenSpeedAsync(file);
        slow.SelectSpeed(slow.Presets[1]);                    // 0.5×
        await slow.RunAsync();
        Assert.InRange((await media.ProbeAsync(Assert.Single(slow.Outputs))).Duration, 11.6, 12.4);

        var fast = await OpenSpeedAsync(file);
        fast.SelectSpeed(fast.Presets[8]);                    // 4×: 6 s become 1,5 s
        await fast.RunAsync();
        var output = Assert.Single(fast.Outputs);
        Assert.Equal(Path.Combine(_root, "clip velocidad 2.mp4"), output);
        Assert.InRange((await media.ProbeAsync(output)).Duration, 1.3, 1.7);
    }

    [AvaloniaFact]
    public async Task A_silent_mkv_keeps_its_container_and_gets_no_sound()
    {
        var file = await MakeClipAsync("mudo.mkv", Picture320, 4, withAudio: false);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = await OpenSpeedAsync(file);
        Assert.False(panel.HasAudio);
        panel.SelectSpeed(panel.Presets[8]);                  // 4×

        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "mudo velocidad.mkv"), output);
        var probe = await media.ProbeAsync(output);
        Assert.InRange(probe.Duration, 0.8, 1.2);
        Assert.False(probe.HasAudio);
        Assert.Equal("h264", probe.VideoCodec);
    }

    [AvaloniaFact]
    public async Task The_speed_panel_renders_its_pills_slider_and_length()
    {
        var file = await MakeClipAsync("paseo.mp4", Picture320, 6);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenSpeedAsync(file);
        panel.Speed = 1.5;

        Render(panel, "changevideospeed");
        Assert.Equal("Velocidad", panel.Title);
    }

    // ───────── Join: the panel and the real FFmpeg ─────────

    [AvaloniaFact]
    public async Task Joining_two_clips_writes_one_mp4_beside_the_first_with_its_size_and_sound()
    {
        var first = await MakeClipAsync("primero.mp4", Picture320, 3);
        var second = await MakeClipAsync("segundo.mp4", "mandelbrot=size=640x360:rate=25", 2, withAudio: false);
        if (first is null || second is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = await OpenJoinAsync(first, second);
        Assert.True(panel.CanApply);
        Assert.Equal("0:05.00", panel.TotalDurationText);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "primero unido.mp4"), output);
        var probe = await media.ProbeAsync(output);
        Assert.InRange(probe.Duration, 4.7, 5.3);
        Assert.Equal((320, 240), (probe.Width, probe.Height)); // the first clip's size, though the second is bigger
        Assert.True(probe.HasAudio);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.InRange((await media.ProbeAsync(second)).Duration, 1.9, 2.1); // the originals are untouched
    }

    /// <summary>The duration of the first stream of <paramref name="kind"/> ("video" or "audio") of a file, with ffprobe.</summary>
    private static async Task<double> StreamSecondsAsync(string file, string kind)
    {
        var result = await Filee.Engines.Infrastructure.ProcessRunner.RunAsync(MediaTools.Locate()!.Ffprobe,
            ["-v", "error", "-select_streams", kind == "video" ? "v:0" : "a:0", "-show_entries", "stream=duration", "-of", "csv=p=0", file],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        return double.Parse(result.StandardOutput.Trim().Split(',')[0], CultureInfo.InvariantCulture);
    }

    [AvaloniaTheory]
    [InlineData(3, 3.5)]
    [InlineData(3.5, 3)]
    public async Task A_clip_whose_sound_and_picture_differ_in_length_does_not_push_the_next_clip_out_of_sync(double pictureSeconds, double soundSeconds)
    {
        // Phones record like this (a few tenths of a second apart). The join must keep every later clip's sound with its
        // own picture: the whole is as long as the longer stream of each clip, and the sound ends with the picture.
        var first = await MakeMediaAsync(Path.Combine(_root, "largo.mp4"), "-t", Seconds(pictureSeconds), "-f", "lavfi", "-i", Picture320, "-t",
            Seconds(soundSeconds), "-f", "lavfi", "-i", "sine=frequency=440", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac");
        var second = await MakeClipAsync("segundo.mp4", Picture320, 2);
        if (first is null || second is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        Assert.InRange(await StreamSecondsAsync(first, "video"), pictureSeconds - 0.1, pictureSeconds + 0.1);
        Assert.InRange(await StreamSecondsAsync(first, "audio"), soundSeconds - 0.1, soundSeconds + 0.1);
        var panel = await OpenJoinAsync(first, second);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        var picture = await StreamSecondsAsync(output, "video");
        var sound = await StreamSecondsAsync(output, "audio");
        Assert.InRange(picture, 5.38, 5.62);                // 3.5 s (the longer stream of the first clip) + 2 s
        Assert.InRange(sound, 5.38, 5.62);
        Assert.InRange(Math.Abs(picture - sound), 0, 0.06); // and not one ahead of the other
    }

    [AvaloniaFact]
    public async Task The_result_can_be_1080p_at_the_rate_of_the_first_clip()
    {
        var first = await MakeClipAsync("primero.mp4", Picture320, 3);
        var second = await MakeClipAsync("segundo.mp4", "mandelbrot=size=640x360:rate=30", 2, withAudio: false);
        if (first is null || second is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = await OpenJoinAsync(first, second);
        panel.SelectSize(panel.Sizes[1]);                     // 1080p

        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        var probe = await media.ProbeAsync(output);
        Assert.Equal((1920, 1080), (probe.Width, probe.Height));
        Assert.Equal(25.0, probe.FrameRate, 1);               // the first clip's rate, not the second's 30
        Assert.InRange(probe.Duration, 4.7, 5.3);
    }

    [AvaloniaFact]
    public async Task Moving_a_clip_to_the_front_makes_it_the_first_picture()
    {
        var red = await MakeSolidAsync("rojo.mp4", "red");
        var blue = await MakeSolidAsync("azul.mp4", "blue");
        if (red is null || blue is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var inOrder = await OpenJoinAsync(red, blue);
        await inOrder.RunAsync();
        var first = Assert.Single(inOrder.Outputs);
        Assert.Equal(Path.Combine(_root, "rojo unido.mp4"), first);
        Assert.True(await FirstFrameDistanceAsync(first, "red") < 0.1, "the joined video should open on red");

        var swapped = await OpenJoinAsync(red, blue);
        swapped.MoveDown(swapped.Clips[0]);                   // blue goes first
        Assert.Equal(blue, swapped.Clips[0].FilePath);
        await swapped.RunAsync();
        var second = Assert.Single(swapped.Outputs);
        Assert.Equal(Path.Combine(_root, "azul unido.mp4"), second);
        Assert.True(await FirstFrameDistanceAsync(second, "blue") < 0.1, "moved to the front, blue opens the joined video");
        Assert.True(await FirstFrameDistanceAsync(second, "red") > 0.3);
    }

    [AvaloniaFact]
    public async Task Taking_a_clip_out_of_the_list_joins_the_others()
    {
        var one = await MakeClipAsync("uno.mp4", Picture320, 1);
        var two = await MakeClipAsync("dos.mp4", Picture320, 1);
        var three = await MakeClipAsync("tres.mp4", Picture320, 1);
        if (one is null || two is null || three is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = await OpenJoinAsync(one, two, three);
        panel.Remove(panel.Clips[1]);
        Assert.Equal(2, panel.Clips.Count);
        Assert.Equal("0:02.00", panel.TotalDurationText);

        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "uno unido.mp4"), output);
        Assert.InRange((await media.ProbeAsync(output)).Duration, 1.8, 2.2);
    }

    [AvaloniaFact]
    public async Task One_clip_alone_cannot_be_joined()
    {
        var one = await MakeClipAsync("solo.mp4", Picture320, 1);
        if (one is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenJoinAsync(one);
        Assert.False(panel.CanApply);
        Assert.Equal("Elige al menos dos vídeos", panel.NoteText);
    }

    [AvaloniaFact]
    public async Task A_clip_that_cannot_be_read_blocks_the_join_until_it_leaves_the_list()
    {
        var good = await MakeClipAsync("bueno.mp4", Picture320, 1);
        var other = await MakeClipAsync("otro.mp4", Picture320, 1);
        if (good is null || other is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var broken = Path.Combine(_root, "roto.mp4");
        File.WriteAllText(broken, "esto no es un vídeo");
        var panel = await OpenJoinAsync(good, broken, other);

        Assert.True(panel.Clips[1].Unreadable);
        Assert.Null(panel.Clips[1].Probe);
        Assert.False(panel.CanApply);
        panel.Remove(panel.Clips[1]);
        Assert.True(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task Thirty_clips_are_the_most_and_their_graph_is_read_from_a_file()
    {
        var media = MediaTools.Locate();
        if (media is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var files = new List<string>();
        for (var i = 1; i <= 30; i++)
            files.Add((await MakeClipAsync($"parte{i:00}.mp4", "testsrc2=size=160x120:rate=25", 0.2))!);
        var panel = await OpenJoinAsync([.. files, Path.Combine(_root, "de mas.mp4")]); // a 31st is left out

        Assert.Equal(30, panel.Clips.Count);
        Assert.Equal("Solo se usan los primeros 30 vídeos", panel.NoteText);
        Assert.True(panel.CanApply);
        // Longer than the command line takes inline: FFmpeg reads it from a file.
        var plan = JoinPlan.Build(panel.Clips.Select(clip => new JoinInput(clip.FilePath, clip.Probe!)).ToList(), JoinSize.AsFirst);
        Assert.True(plan.Graph.Length > 4000, $"the graph is {plan.Graph.Length} characters");

        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "parte01 unido.mp4"), output);
        Assert.InRange((await media.ProbeAsync(output)).Duration, 5.7, 6.3);
    }

    [AvaloniaFact]
    public async Task The_join_panel_renders_three_clips_in_a_list()
    {
        var a = await MakeClipAsync("uno.mp4", Picture320, 3);
        var b = await MakeClipAsync("dos.mp4", "mandelbrot=size=640x360:rate=25", 2, withAudio: false);
        var c = await MakeClipAsync("tres.mp4", "testsrc2=size=160x120:rate=25", 4);
        if (a is null || b is null || c is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = await OpenJoinAsync(a, b, c);
        panel.SelectSize(panel.Sizes[1]);                     // 1080p

        Render(panel, "joinvideos");
        Assert.Equal("Unir", panel.Title);
    }
}
