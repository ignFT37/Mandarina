// Mandarina: the three audio tools that open a panel: CHANNELS (mono or stereo, the level of each side), BLEEP (cover
// stretches with a tone) and VISUALIZE (an MP4 with the wave). Each Apply runs the real FFmpeg (engines\ffmpeg next to the test
// binaries) and the result is measured, not just opened: the audio is decoded to PCM and the level and the pitch of stretches are
// computed from the samples; the video is probed and a frame is read back and its pixels counted. The panels are rendered to
// mandarina-panel-audio*.png in the screenshot folder for review.

using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.Core.Platform;
using Filee.Engines.Infrastructure;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaAudioTests : IDisposable
{
    private const string NoFfmpeg = "FFmpeg is not installed next to the tests (engines\\ffmpeg).";

    /// <summary>440 Hz at half scale on the left, 880 Hz at a quarter on the right: every side of a result can be told by its pitch and its level.</summary>
    private const string Stereo = "0.5*sin(2*PI*440*t)|0.25*sin(2*PI*880*t)";

    private const string Mono = "0.5*sin(2*PI*440*t)";

    /// <summary>A loud, moving sound: sums of a few partials under a slow beat, a little like a voice.</summary>
    private const string Speech = "(0.5*sin(2*PI*220*t)+0.25*sin(2*PI*660*t)+0.1*sin(2*PI*1500*t))*(0.3+0.7*abs(sin(2*PI*1.7*t)))";

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-audio").FullName;

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

    private string PathIn(string name) => Path.Combine(_root, name);

    // ───────── Test material ─────────

    /// <summary>
    /// A file of <paramref name="seconds"/> seconds whose channels are the <c>aevalsrc</c> expressions (one per channel, "|" apart),
    /// in the format of the name's extension. Null without FFmpeg.
    /// </summary>
    private Task<string?> MakeAsync(string name, string expressions, double seconds = 4, int rate = 44100, params string[] encoder)
    {
        var channels = expressions.Count(c => c == '|') + 1;
        var layout = channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", _ => throw new ArgumentException("1, 2 or 6 channels") };
        return MakeMediaAsync(PathIn(name), ["-f", "lavfi", "-i",
            string.Create(CultureInfo.InvariantCulture, $"aevalsrc='{expressions}':d={seconds}:s={rate}:c={layout}"), .. encoder]);
    }

    /// <summary>A video file with no audio at all.</summary>
    private Task<string?> MakeSilentVideoAsync(string name) =>
        MakeMediaAsync(PathIn(name), "-f", "lavfi", "-i", "testsrc=d=1:s=64x64:r=10", "-c:v", "libx264", "-pix_fmt", "yuv420p");

    // ───────── Measuring ─────────

    /// <summary>One channel of an audio file as FFmpeg decodes it (mono, 44.1 kHz, 16 bit), and the measures taken of it.</summary>
    private sealed class Pcm(float[] samples)
    {
        public const int Rate = 44100;

        public float[] Samples { get; } = samples;

        public double Seconds => (double)Samples.Length / Rate;

        private ReadOnlySpan<float> Between(double from, double to)
        {
            var first = Math.Clamp((int)Math.Round(from * Rate), 0, Samples.Length);
            var last = Math.Clamp((int)Math.Round(to * Rate), first, Samples.Length);
            return Samples.AsSpan(first, last - first);
        }

        private static double Db(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-10));

        /// <summary>The RMS level of from..to seconds, in dB of full scale (a full-scale sine reads -3 dB).</summary>
        public double Rms(double from, double to)
        {
            var part = Between(from, to);
            double sum = 0;
            foreach (var sample in part)
                sum += sample * (double)sample;
            return Db(Math.Sqrt(sum / Math.Max(1, part.Length)));
        }

        /// <summary>The largest sample of from..to seconds (0..1).</summary>
        public double Peak(double from, double to)
        {
            double peak = 0;
            foreach (var sample in Between(from, to))
                peak = Math.Max(peak, Math.Abs(sample));
            return peak;
        }

        /// <summary>
        /// How loud the component at <paramref name="hertz"/> is in from..to seconds, in dB of full scale (a full-scale sine of that
        /// pitch reads 0 dB): a Hann-windowed Goertzel filter, which keeps a neighbouring pitch out of the reading.
        /// </summary>
        public double Tone(double hertz, double from, double to)
        {
            var part = Between(from, to);
            var n = part.Length;
            if (n < 8)
                return -200;
            var coefficient = 2 * Math.Cos(2 * Math.PI * hertz / Rate);
            double s1 = 0, s2 = 0, window = 0;
            for (var i = 0; i < n; i++)
            {
                var weight = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
                window += weight;
                var s0 = part[i] * weight + coefficient * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
            return Db(2 * Math.Sqrt(Math.Max(0, power)) / window);
        }

        /// <summary>The first moment (seconds) from <paramref name="from"/> on at which the <paramref name="hertz"/> component of a 5 ms window passes -25 dB.</summary>
        public double OnsetOf(double hertz, double from, double to)
        {
            for (var t = from; t < to; t += 0.001)
                if (Tone(hertz, t, t + 0.005) > -25)
                    return t + 0.0025;
            return double.NaN;
        }

        /// <summary>The index of the first and of the last sample where this differs from <paramref name="other"/> by more than a quantization step.</summary>
        public (int First, int Last) Differences(Pcm other)
        {
            int first = -1, last = -1;
            for (var i = 0; i < Math.Min(Samples.Length, other.Samples.Length); i++)
            {
                if (Math.Abs(Samples[i] - other.Samples[i]) <= 1e-5)
                    continue;
                if (first < 0)
                    first = i;
                last = i;
            }
            return (first, last);
        }
    }

    /// <summary>Decodes <paramref name="file"/> to mono 44.1 kHz samples: its channel number <paramref name="channel"/> (0 left, 1 right), or all mixed when -1.</summary>
    private async Task<Pcm> DecodeAsync(string file, int channel = -1)
    {
        var raw = PathIn($"{Guid.NewGuid():N}.raw");
        List<string> args = ["-i", file, "-vn"];
        args.AddRange(channel < 0 ? ["-ac", "1"] : ["-af", $"pan=mono|c0=c{channel}"]);
        args.AddRange(["-ar", Pcm.Rate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", raw]);
        await MediaTools.Locate()!.RunAsync(args);
        var bytes = await File.ReadAllBytesAsync(raw);
        File.Delete(raw);
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return new Pcm(samples);
    }

    /// <summary>The bit depth ffprobe reports for the audio of <paramref name="file"/>.</summary>
    private async Task<int> BitsAsync(string file)
    {
        var result = await ProcessRunner.RunAsync(MediaTools.Locate()!.Ffprobe,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=bits_per_raw_sample", "-of", "csv=p=0", file], TimeSpan.FromMinutes(1), CancellationToken.None);
        return int.TryParse(result.StandardOutput.Trim(), out var bits) ? bits : 0;
    }

    /// <summary>How many pixels of the frame at <paramref name="seconds"/> are within <paramref name="tolerance"/> of <paramref name="rgb"/> in every channel, out of how many.</summary>
    private static async Task<(int Match, int Total)> CountPixelsAsync(string video, double seconds, (int R, int G, int B) rgb, int tolerance = 36)
    {
        using var frame = await MediaTools.Locate()!.FrameAsync(video, seconds, maxWidth: 0);
        using var stream = new MemoryStream();
        frame.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        using var image = new MagickImage(stream);
        var data = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var match = 0;
        for (var i = 0; i < data.Length; i += 3)
            if (Math.Abs(data[i] - rgb.R) <= tolerance && Math.Abs(data[i + 1] - rgb.G) <= tolerance && Math.Abs(data[i + 2] - rgb.B) <= tolerance)
                match++;
        return (match, data.Length / 3);
    }

    private static (int R, int G, int B) Rgb(string hex)
    {
        Assert.True(VisualColors.TryParse(hex, out var color));
        return (color.R, color.G, color.B);
    }

    /// <summary>Asserts a level is within <paramref name="margin"/> dB of <paramref name="expected"/>.</summary>
    private static void AssertDb(double expected, double actual, double margin = 0.3, string? what = null) =>
        Assert.True(Math.Abs(expected - actual) <= margin, $"{what}: expected {expected:0.00} dB ± {margin}, measured {actual:0.00} dB");

    // ───────── What the filters say (no FFmpeg) ─────────

    [Fact]
    public void The_channels_filter_names_the_mix_with_decimal_points_whatever_the_culture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            Assert.Equal("pan=mono|c0=0.5*c0+0.5*c1", ChannelsMix.Filter(2, toMono: true, 1, 1));
            Assert.Equal("pan=mono|c0=0.25*c0+0.5*c1", ChannelsMix.Filter(2, toMono: true, 0.5, 1));
            Assert.Equal("pan=mono|c0=0.75*c0", ChannelsMix.Filter(1, toMono: true, 0.5, 1));
            Assert.Equal("pan=stereo|c0=0.5*c0|c1=1*c1", ChannelsMix.Filter(2, toMono: false, 0.5, 1));
            Assert.Equal("pan=stereo|c0=0*c0|c1=1*c0", ChannelsMix.Filter(1, toMono: false, 0, 1));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void The_channels_filter_limits_only_when_a_side_is_raised_and_mixes_surround_down_first()
    {
        Assert.DoesNotContain("alimiter", ChannelsMix.Filter(2, toMono: false, 1, 1));
        Assert.DoesNotContain("alimiter", ChannelsMix.Filter(2, toMono: false, 0, 1));
        Assert.EndsWith(",alimiter=limit=0.97:latency=1:level=0", ChannelsMix.Filter(2, toMono: false, 1, 2));
        Assert.StartsWith("aformat=channel_layouts=stereo,pan=stereo|c0=1*c0|c1=1*c1", ChannelsMix.Filter(6, toMono: false, 1, 1));
        Assert.StartsWith("aformat=channel_layouts=stereo,pan=mono|", ChannelsMix.Filter(6, toMono: true, 1, 1));
    }

    [Fact]
    public void Bleep_ranges_are_ordered_clipped_and_joined_when_they_overlap_or_touch()
    {
        var joined = BleepGraph.Normalize([new WaveRange(4, 5), new WaveRange(1, 2), new WaveRange(1.5, 3), new WaveRange(3, 3.5)], 10);
        Assert.Equal([new WaveRange(1, 3.5), new WaveRange(4, 5)], joined);

        // Handles the wrong way round, a range past the end, one outside the file, one too short to hear, an edge a hair from the file's.
        var clipped = BleepGraph.Normalize([new WaveRange(2, 1), new WaveRange(9, 12), new WaveRange(20, 30), new WaveRange(5, 5.0004), new WaveRange(0.0004, 0.5)], 10);
        Assert.Equal([new WaveRange(0, 0.5), new WaveRange(1, 2), new WaveRange(9, 10)], clipped);
        Assert.Empty(BleepGraph.Normalize([], 10));
        Assert.Equal("", BleepGraph.Build([new WaveRange(20, 30)], 10, 44100, 0.35));
    }

    [Fact]
    public void The_bleep_graph_tiles_the_whole_file_with_plain_pieces_and_tone_pieces()
    {
        // 2 ranges in the middle: plain, tone, plain, tone, plain. Counting the file in samples keeps the cut where the wave shows it.
        var graph = BleepGraph.Build([new WaveRange(1.5, 2), new WaveRange(3, 3.25)], 6, 44100, 0.35);
        Assert.StartsWith("[0:a]asetpts=N/SR/TB,aformat=sample_fmts=fltp,asplit=5[o0][o1][o2][o3][o4];", graph);
        Assert.Contains("[o0]atrim=start=0:end=1.5,asetpts=PTS-STARTPTS[s0];", graph);
        Assert.Contains("[o1]atrim=start=1.5:end=2,asetpts=PTS-STARTPTS,aeval=exprs='val(ch)+(0.35*sin(2*PI*1000*t)-val(ch))*max(0,min(min(1,t/0.004),(0.5-t)/0.004))':c=same,aformat=sample_fmts=fltp[s1];", graph);
        Assert.Contains("[o4]atrim=start=3.25,asetpts=PTS-STARTPTS[s4];", graph);
        Assert.EndsWith("[s0][s1][s2][s3][s4]concat=n=5:v=0:a=1[out]", graph);

        // A range from the first sample to the last leaves just the tone; one from the start leaves no empty piece before it.
        Assert.Contains("asplit=1[o0];[o0]atrim=start=0:end=6", BleepGraph.Build([new WaveRange(0, 6)], 6, 44100, 1));
        Assert.StartsWith("[0:a]asetpts=N/SR/TB,aformat=sample_fmts=fltp,asplit=2[o0][o1];[o0]atrim=start=0:end=0.5", BleepGraph.Build([new WaveRange(0, 0.5)], 6, 44100, 1));
    }

    [Fact]
    public void The_visual_graph_draws_the_chosen_colours_in_the_chosen_shape()
    {
        var wave = VisualGraph.Build(VisualFormat.Horizontal, VisualStyle.Wave, "#ff5200", "#1b1620");
        Assert.StartsWith("color=c=0x1B1620:s=1920x1080:r=30[bg];[0:a]aformat=channel_layouts=mono,aresample=57600,showwaves=s=1920x648:mode=cline:colors=0xFF5200:rate=30:", wave);
        Assert.Contains("[bg][wave]overlay=x=0:y=216:shortest=1:format=auto,scale=out_color_matrix=bt709", wave);
        Assert.EndsWith("format=yuv420p,setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv[v]", wave);

        // One audio sample for every pixel column: the rate follows the width, whatever the shape. The wave lives in a band of 60 % of the height.
        var vertical = VisualGraph.Build(VisualFormat.Vertical, VisualStyle.Wave, "#FF5200", "#1B1620");
        Assert.Contains("aresample=32400,showwaves=s=1080x1152", vertical);
        Assert.Contains("overlay=x=0:y=384:", vertical);
        Assert.Contains("showwaves=s=1080x648", VisualGraph.Build(VisualFormat.Square, VisualStyle.Wave, "#FF5200", "#1B1620"));

        var bars = VisualGraph.Build(VisualFormat.Vertical, VisualStyle.Bars, "#3B82F6", "#FFFFFF", previewWidth: 540);
        Assert.Contains("showfreqs=s=54x480", bars);
        Assert.Contains("colors=0x3B82F6", bars);
        Assert.Contains("drawgrid=x=-2:y=-10:w=20:h=3840:t=4:c=0xFFFFFF", bars);
        Assert.EndsWith(",scale=540:-2:flags=area[v]", bars);

        // A colour that is not one falls back to the default instead of reaching FFmpeg.
        Assert.Contains("color=c=0x1B1620", VisualGraph.Build(VisualFormat.Square, VisualStyle.Wave, "#FF5200", "not a colour"));
    }

    [Theory]
    [InlineData("#FF5200", true, "#FF5200")]
    [InlineData("ff5200", true, "#FF5200")]
    [InlineData("#f50", true, "#FF5500")]
    [InlineData(" #1b1620 ", true, "#1B1620")]
    [InlineData("#12345", false, null)]
    [InlineData("#GG0000", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void Colours_are_read_as_six_or_three_hex_digits(string? text, bool valid, string? hex)
    {
        Assert.Equal(valid, VisualColors.TryParse(text, out var color));
        if (valid)
            Assert.Equal(hex, VisualColors.ToHex(color));
    }

    [Fact]
    public void The_preview_is_taken_from_the_middle_and_stays_inside_a_short_file()
    {
        Assert.Equal((4.0, 2.0, 0.5), AudioToVideoPanel.PreviewWindow(10));
        Assert.Equal((0.0, 0.5, 0.25), AudioToVideoPanel.PreviewWindow(0.5));
        var (start, length, frame) = AudioToVideoPanel.PreviewWindow(1.5);
        Assert.Equal(0, start);
        Assert.InRange(start + length, 1.4, 1.5);
        Assert.InRange(frame, 0, length);
    }

    // ───────── CHANNELS ─────────

    [AvaloniaTheory]
    [InlineData("en", "Channels")]
    [InlineData("es", "Canales")]
    [InlineData("ko", "채널")]
    [InlineData("zh-CN", "声道")]
    public async Task The_channels_panel_renders_with_a_stereo_file_in_every_language(string language, string title)
    {
        var file = await MakeAsync("concierto.mp3", Stereo, 5);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioChannelsPanel(ContextIn(language, file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.Equal(2, panel.SourceChannels);
        panel.Left = 100;
        panel.Right = 70;

        Render(panel, $"audiochannels-{language}");
        Assert.Equal(title, panel.Title);
    }

    [AvaloniaFact]
    public async Task The_channels_panel_renders_a_mono_file_made_mono_with_its_notice()
    {
        var file = await MakeAsync("charla.wav", Mono, 3);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        panel.SetMonoCommand.Execute(null);
        Render(panel, "audiochannels-mono");
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_channels_panel_fits_its_longest_line_for_a_surround_file(string language)
    {
        var file = await MakeAsync("envolvente.flac", "0.3*sin(2*PI*440*t)|0.3*sin(2*PI*880*t)|0.3*sin(2*PI*660*t)|0.3*sin(2*PI*100*t)|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*550*t)", 2, 48000);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioChannelsPanel(ContextIn(language, file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Equal(6, panel.SourceChannels);
        Render(panel, $"audiochannels-surround-{language}");          // to stereo, which is where it starts
        panel.SetMonoCommand.Execute(null);
        Render(panel, $"audiochannels-surround-mono-{language}");
    }

    [AvaloniaFact]
    public async Task The_channels_panel_starts_on_the_other_layout_and_says_what_it_will_do()
    {
        var stereo = await MakeAsync("estereo.wav", Stereo, 2);
        var mono = await MakeAsync("mono.wav", Mono, 2);
        var surround = await MakeAsync("envolvente.flac", "0.3*sin(2*PI*440*t)|0.3*sin(2*PI*880*t)|0.3*sin(2*PI*660*t)|0.3*sin(2*PI*100*t)|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*550*t)", 2, 48000);
        if (stereo is null || mono is null || surround is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }

        // Stereo: mono to begin with, and that is a change.
        var panel = new AudioChannelsPanel(Context(stereo));
        Assert.False(panel.CanApply);                        // still reading
        await WaitAsync(() => !panel.IsLoading);
        Assert.True(panel.ToMono);
        Assert.True(panel.CanApply);
        Assert.Equal("Ahora: estéreo", panel.SourceText);
        Assert.Equal("Estéreo → mono: mezcla de los dos canales.", panel.ResultText);
        Assert.False(panel.IsNotice);
        // Stereo again with nothing touched is no change; a side at another level is.
        panel.SetStereoCommand.Execute(null);
        Assert.False(panel.CanApply);
        Assert.Equal("Estéreo: solo cambia el nivel de cada lado.", panel.ResultText);
        panel.Left = 80;
        Assert.True(panel.CanApply);
        panel.Left = 100;
        Assert.False(panel.CanApply);
        // The slider drags through fractions: the level used is the percentage shown.
        panel.Right = 99.6;
        Assert.Equal(100, panel.Right);
        Assert.False(panel.CanApply);

        // Mono: stereo to begin with; mono again only changes the level, and the panel says so.
        panel = new AudioChannelsPanel(Context(mono));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.ToMono);
        Assert.True(panel.CanApply);
        Assert.Equal("Mono → estéreo: el mismo sonido por los dos lados.", panel.ResultText);
        panel.SetMonoCommand.Execute(null);
        Assert.True(panel.IsNotice);
        Assert.Equal("Este archivo ya es mono: solo puede cambiar su nivel.", panel.ResultText);
        Assert.False(panel.CanApply);
        panel.Right = 150;
        Assert.True(panel.CanApply);

        // Surround: stereo to begin with.
        panel = new AudioChannelsPanel(Context(surround));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Equal(6, panel.SourceChannels);
        Assert.False(panel.ToMono);
        Assert.True(panel.CanApply);
        Assert.Equal("Ahora: 6 canales", panel.SourceText);
        Assert.Equal("6 canales → estéreo: mezcla de todos los canales en dos.", panel.ResultText);
    }

    [AvaloniaFact]
    public async Task Stereo_to_mono_mixes_both_sides_into_a_new_file_and_leaves_the_original_alone()
    {
        var file = await MakeAsync("tono.wav", Stereo);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var before = await File.ReadAllBytesAsync(file);
        var panel = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("tono mono.wav"), output);
        var probe = await media.ProbeAsync(output);
        Assert.Equal(1, probe.Channels);
        Assert.Equal(44100, probe.SampleRate);
        Assert.InRange(probe.Duration, 3.99, 4.01);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));               // the original is untouched
        Assert.Equal(2, (await media.ProbeAsync(file)).Channels);

        // Each side weighs half: the left tone drops from 0.5 to 0.25 (-12.04 dB), the right from 0.25 to 0.125 (-18.06 dB).
        var mixed = await DecodeAsync(output);
        AssertDb(-12.04, mixed.Tone(440, 0.5, 3.5), what: "left tone in the mix");
        AssertDb(-18.06, mixed.Tone(880, 0.5, 3.5), what: "right tone in the mix");

        // Another run does not overwrite: "tono mono 2.wav".
        var again = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !again.IsLoading);
        await again.RunAsync();
        Assert.Equal(PathIn("tono mono 2.wav"), Assert.Single(again.Outputs));
    }

    [AvaloniaFact]
    public async Task Mono_to_stereo_puts_the_same_sound_on_both_sides_at_the_same_level()
    {
        var file = await MakeAsync("tono.wav", Mono);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.ToMono);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("tono estéreo.wav"), output);
        var probe = await MediaTools.Locate()!.ProbeAsync(output);
        Assert.Equal(2, probe.Channels);
        Assert.InRange(probe.Duration, 3.99, 4.01);
        foreach (var side in new[] { 0, 1 })
        {
            var pcm = await DecodeAsync(output, side);
            AssertDb(-6.02, pcm.Tone(440, 0.5, 3.5), what: $"side {side}");           // 0.5, not -3 dB lower: no panning law
            Assert.True(pcm.Tone(880, 0.5, 3.5) < -80);
        }
    }

    [AvaloniaFact]
    public async Task A_side_at_zero_is_silent_and_the_other_keeps_its_level()
    {
        var file = await MakeAsync("tono.wav", Stereo);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var originalRight = await DecodeAsync(file, 1);
        var panel = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        panel.SetStereoCommand.Execute(null);
        panel.Left = 0;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("tono estéreo.wav"), output);
        var left = await DecodeAsync(output, 0);
        var right = await DecodeAsync(output, 1);
        Assert.True(left.Peak(0, 4) < 1e-4, $"the left side still has sound: peak {left.Peak(0, 4)}");
        AssertDb(originalRight.Rms(0.5, 3.5), right.Rms(0.5, 3.5), 0.05, "right level");
        AssertDb(-12.04, right.Tone(880, 0.5, 3.5), what: "right tone");
        // Not one sample of the right side moved.
        Assert.Equal((-1, -1), originalRight.Differences(right));
    }

    [AvaloniaFact]
    public async Task Two_hundred_percent_doubles_a_side_and_a_loud_file_is_limited_instead_of_clipped()
    {
        var quiet = await MakeAsync("bajo.wav", "0.2*sin(2*PI*440*t)|0.2*sin(2*PI*880*t)");
        var loud = await MakeAsync("alto.wav", "0.9*sin(2*PI*440*t)|0.2*sin(2*PI*880*t)");
        if (quiet is null || loud is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }

        // 0.2 doubled is 0.4: twice as loud (+6.02 dB) and well under full scale, so nothing is limited.
        var panel = new AudioChannelsPanel(Context(quiet));
        await WaitAsync(() => !panel.IsLoading);
        panel.SetStereoCommand.Execute(null);
        panel.Right = 200;
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        var left = await DecodeAsync(output, 0);
        var right = await DecodeAsync(output, 1);
        AssertDb(-13.98, left.Tone(440, 0.5, 3.5), what: "left, unchanged");        // 0.2
        AssertDb(-7.96, right.Tone(880, 0.5, 3.5), what: "right, doubled");          // 0.4
        AssertDb(-10.97, right.Rms(0.5, 3.5), 0.05, "right rms");                    // 0.4 / sqrt 2
        Assert.InRange(right.Peak(0, 4), 0.399, 0.401);

        // 0.9 doubled is 1.8: it would clip. It is limited just under full scale; the other side does not move by more than a fraction of a dB.
        panel = new AudioChannelsPanel(Context(loud));
        await WaitAsync(() => !panel.IsLoading);
        panel.SetStereoCommand.Execute(null);
        panel.Left = 200;
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        output = Assert.Single(panel.Outputs);
        left = await DecodeAsync(output, 0);
        right = await DecodeAsync(output, 1);
        Assert.InRange(left.Peak(0, 4), 0.9, 0.975);
        Assert.True(left.Rms(0.5, 3.5) > -6, $"the limited side is still loud: {left.Rms(0.5, 3.5):0.0} dB");
        AssertDb(-16.99, right.Rms(0.5, 3.5), 0.6, "right rms, not ducked by the limiter");     // a 0.2 sine
    }

    [AvaloniaFact]
    public async Task Channels_keeps_the_format_the_tags_and_the_length_of_a_compressed_file()
    {
        var file = await MakeAsync("cancion.mp3", Stereo, 4, 44100, "-metadata", "artist=Autor01", "-metadata", "title=Prueba");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal("cancion mono.mp3", Path.GetFileName(output));
        var probe = await MediaTools.Locate()!.ProbeAsync(output);
        Assert.Equal("mp3", probe.AudioCodec);
        Assert.Equal(1, probe.Channels);
        Assert.Equal("Autor01", probe.Tags["artist"]);
        Assert.Equal("Prueba", probe.Tags["title"]);
        Assert.InRange(probe.Duration, 3.9, 4.15);
        AssertDb(-12.04, (await DecodeAsync(output)).Tone(440, 0.5, 3.5), 0.6, "left tone in the mix");
    }

    [AvaloniaTheory]
    [InlineData("tono.m4a", "aac")]
    [InlineData("tono.flac", "flac")]
    [InlineData("tono.ogg", "vorbis")]
    [InlineData("tono.opus", "opus")]
    [InlineData("tono.aiff", "pcm_s16be")]
    public async Task Channels_writes_every_format_in_its_own(string name, string codec)
    {
        var file = await MakeAsync(name, Stereo, 3, name.EndsWith(".opus", StringComparison.Ordinal) ? 48000 : 44100);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.GetExtension(name), Path.GetExtension(output));
        var probe = await MediaTools.Locate()!.ProbeAsync(output);
        Assert.Equal(codec, probe.AudioCodec);
        Assert.Equal(1, probe.Channels);
        Assert.InRange(probe.Duration, 2.9, 3.1);
        AssertDb(-12.04, (await DecodeAsync(output)).Tone(440, 0.5, 2.5), 0.8, "left tone in the mix");
    }

    [AvaloniaFact]
    public async Task A_surround_file_is_mixed_down_with_its_centre_not_cut_to_its_front_pair()
    {
        // Front left 440, front right 880, centre 660, low frequency 100, back left 330, back right 550.
        var file = await MakeAsync("envolvente.flac", "0.3*sin(2*PI*440*t)|0.3*sin(2*PI*880*t)|0.3*sin(2*PI*660*t)|0.3*sin(2*PI*100*t)|0.3*sin(2*PI*330*t)|0.3*sin(2*PI*550*t)", 3, 48000);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal("envolvente estéreo.flac", Path.GetFileName(output));
        Assert.Equal(2, (await MediaTools.Locate()!.ProbeAsync(output)).Channels);
        var left = await DecodeAsync(output, 0);
        var right = await DecodeAsync(output, 1);
        // The centre reaches both sides, the back channels the side they are on, the front ones their own.
        Assert.True(left.Tone(660, 0.5, 2.5) > -30 && right.Tone(660, 0.5, 2.5) > -30, "the centre channel is missing");
        Assert.True(left.Tone(440, 0.5, 2.5) > -30 && left.Tone(330, 0.5, 2.5) > -30);
        Assert.True(right.Tone(880, 0.5, 2.5) > -30 && right.Tone(550, 0.5, 2.5) > -30);
        Assert.True(left.Tone(880, 0.5, 2.5) < -80 && right.Tone(440, 0.5, 2.5) < -80);
    }

    [AvaloniaFact]
    public async Task A_24_bit_flac_stays_24_bit_after_a_filter_and_a_16_bit_one_stays_16()
    {
        var deep = await MakeAsync("hifi.flac", Stereo, 2, 44100, "-c:a", "flac", "-sample_fmt", "s32");
        var plain = await MakeAsync("normal.flac", Stereo, 2, 44100, "-c:a", "flac", "-sample_fmt", "s16");
        if (deep is null || plain is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        Assert.Equal(24, await BitsAsync(deep));
        Assert.Equal(16, await BitsAsync(plain));
        foreach (var (file, bits) in new[] { (deep, 24), (plain, 16) })
        {
            var panel = new AudioChannelsPanel(Context(file));
            await WaitAsync(() => !panel.IsLoading);
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            Assert.Equal(bits, await BitsAsync(Assert.Single(panel.Outputs)));
        }
    }

    [AvaloniaFact]
    public async Task A_24_bit_wav_keeps_its_codec_through_channels()
    {
        var file = await MakeAsync("hifi.wav", Stereo, 2, 44100, "-c:a", "pcm_s24le");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioChannelsPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal("pcm_s24le", (await MediaTools.Locate()!.ProbeAsync(Assert.Single(panel.Outputs))).AudioCodec);
    }

    // ───────── BLEEP ─────────

    [AvaloniaTheory]
    [InlineData("en", "Bleep")]
    [InlineData("es", "Pitido")]
    [InlineData("ko", "삐 소리")]
    [InlineData("zh-CN", "消音")]
    public async Task The_bleep_panel_renders_with_its_waveform_and_marked_stretches_in_every_language(string language, string title)
    {
        // 12 s of a sound that swells and fades, like speech.
        var file = await MakeAsync("entrevista.mp3", Speech, 12, 22050);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new RedactAudioPanel(ContextIn(language, file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.InRange(panel.Duration, 11.9, 12.2);
        Assert.NotNull(panel.Peaks);
        foreach (var (start, end) in new[] { (1.2, 2.1), (4.4, 5.3), (9.0, 9.5) })
        {
            panel.Start = start;
            panel.End = end;
            panel.AddSelection();
        }
        panel.Start = 6.2;
        panel.End = 7.4;
        panel.Position = 6.8;

        Render(panel, $"audiobleep-{language}");
        Assert.Equal(title, panel.Title);
    }

    [AvaloniaFact]
    public async Task The_bleep_panel_renders_empty_before_anything_is_marked()
    {
        var file = await MakeAsync("entrevista.mp3", Speech, 12, 22050);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new RedactAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.CanApply);
        Render(panel, "audiobleep-empty");
    }

    [AvaloniaFact]
    public async Task Bleep_keeps_a_list_of_ranges_joined_sorted_and_removable()
    {
        var file = await MakeAsync("voz.wav", Mono, 10);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new RedactAudioPanel(Context(file));
        Assert.False(panel.CanApply);
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.CanApply);                                   // nothing marked yet
        Assert.InRange(panel.Start, 4.4, 4.6);                          // a stretch in the middle to begin with, not the whole file
        Assert.InRange(panel.End, 5.4, 5.6);
        Assert.Equal("Pitidos (0)", panel.RangesTitle);

        void Add(double start, double end)
        {
            panel.Start = start;
            panel.End = end;
            panel.AddSelection();
        }
        Add(6, 7);
        Assert.True(panel.CanApply);
        Add(1, 2);
        Add(6.5, 8);                                                    // overlaps the first: joined
        Assert.Equal([(1.0, 2.0), (6.0, 8.0)], panel.Ranges.Select(r => (r.Start, r.End)));
        Add(2, 3);                                                      // touches the one before: joined
        Assert.Equal([(1.0, 3.0), (6.0, 8.0)], panel.Ranges.Select(r => (r.Start, r.End)));
        Assert.Equal("Pitidos (2)", panel.RangesTitle);
        Assert.Equal([new WaveRange(1, 3), new WaveRange(6, 8)], panel.Marks);

        // What is marked already cannot be added again; a different stretch can.
        panel.Start = 6.5;
        panel.End = 7.5;
        Assert.False(panel.CanAdd);
        panel.End = 9;
        Assert.True(panel.CanAdd);
        panel.Start = panel.End - 0.01;                                 // shorter than the handles may be
        Assert.False(panel.CanAdd);

        Assert.Equal("0:01.00 – 0:03.00", panel.Ranges[0].RangeText);
        Assert.Equal("2.00 s", panel.Ranges[0].LengthText);
        panel.Remove(panel.Ranges[0]);
        Assert.Equal([(6.0, 8.0)], panel.Ranges.Select(r => (r.Start, r.End)));
        Assert.True(panel.CanApply);
        panel.Remove(panel.Ranges[0]);
        Assert.False(panel.CanApply);
        Assert.Empty(panel.Marks);
    }

    [AvaloniaFact]
    public async Task The_bleep_buttons_add_the_selection_and_remove_a_stretch_from_the_list()
    {
        var file = await MakeAsync("voz.wav", Mono, 10);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new RedactAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var add = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Classes.Contains("pchip") && b.Classes.Contains("primary"));
        Assert.True(add.IsEnabled);
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump();
        Assert.Single(panel.Ranges);
        Assert.False(add.IsEnabled);                                    // that stretch is covered now

        var remove = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Classes.Contains("bleepx"));
        remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump();
        Assert.Empty(panel.Ranges);
        Assert.True(add.IsEnabled);
        window.Close();
    }

    /// <summary>A platform that only remembers the sounds it was asked to play and stop (the rest does nothing).</summary>
    private sealed class RecordingSound : IPlatformServices
    {
        public List<byte[]> Played { get; } = [];
        public int Stopped { get; private set; }
        public void PlaySound(byte[] wav) => Played.Add(wav);
        public void StopSound() => Stopped++;
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) => Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }

    [AvaloniaFact]
    public async Task Bleep_plays_the_stretch_between_the_handles_and_stops_on_the_second_press()
    {
        var file = await MakeAsync("voz.wav", Mono, 6);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var platform = new RecordingSound();
        var panel = new RedactAudioPanel(new PanelContext([file], Context(file).Loc, platform));
        await WaitAsync(() => !panel.IsLoading);
        panel.Start = 1;
        panel.End = 2.5;

        await panel.TogglePlayAsync();

        Assert.True(panel.IsPlaying);
        var wav = Assert.Single(platform.Played);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(1.5, (wav.Length - 44) / (22050.0 * 2), 2);                  // mono 22 kHz 16 bit: the stretch, nothing more
        await WaitAsync(() => panel.Position > 1);                                // the playhead runs along the wave
        await panel.TogglePlayAsync();
        Assert.False(panel.IsPlaying);
        Assert.Equal(1, platform.Stopped);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task Bleep_replaces_two_ranges_with_a_tone_exactly_where_they_are_and_touches_nothing_else()
    {
        var file = await MakeAsync("voz.wav", Mono, 6);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var before = await File.ReadAllBytesAsync(file);
        var panel = new RedactAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        foreach (var (start, end) in new[] { (1.2, 2.0), (3.5, 4.1) })
        {
            panel.Start = start;
            panel.End = end;
            panel.AddSelection();
        }

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("voz con pitidos.wav"), output);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));                 // the original is untouched
        var probe = await media.ProbeAsync(output);
        Assert.Equal(1, probe.Channels);
        Assert.Equal(44100, probe.SampleRate);
        Assert.Equal((await media.ProbeAsync(file)).Duration, probe.Duration, 3);   // the very same length

        var original = await DecodeAsync(file);
        var result = await DecodeAsync(output);
        Assert.Equal(original.Samples.Length, result.Samples.Length);

        // Outside the ranges the sound is the original: the same level, the same pitch, not a sample different.
        foreach (var (from, to) in new[] { (0.1, 1.15), (2.05, 3.45), (4.15, 5.9) })
        {
            AssertDb(-6.02, result.Tone(440, from, to), 0.1, $"tone at {from}-{to}");
            Assert.True(result.Tone(1000, from, to) < -80, $"a bleep leaks out at {from}-{to}");
        }
        // The first sample that changes is the one after the first of the first range, the last is the one before the end of the second.
        var differences = original.Differences(result);
        Assert.InRange(differences.First / (double)Pcm.Rate, 1.2, 1.2008);
        Assert.InRange(differences.Last / (double)Pcm.Rate, 4.0992, 4.1);
        // And not one sample outside the ranges is different (a few samples of slack at their edges).
        var outside = 0;
        for (var i = 0; i < original.Samples.Length; i++)
        {
            var t = (double)i / Pcm.Rate;
            if (!((t >= 1.2 - 1e-4 && t <= 2.0 + 1e-4) || (t >= 3.5 - 1e-4 && t <= 4.1 + 1e-4)) && Math.Abs(original.Samples[i] - result.Samples[i]) > 1e-5)
                outside++;
        }
        Assert.Equal(0, outside);

        // Inside, a 1 kHz tone at 35 % of full scale (-9.12 dB) and none of the original.
        foreach (var (from, to) in new[] { (1.25, 1.95), (3.55, 4.05) })
        {
            AssertDb(-9.12, result.Tone(1000, from, to), 0.2, $"bleep at {from}-{to}");
            Assert.True(result.Tone(440, from, to) < -80, $"the voice is still heard at {from}-{to}");
            AssertDb(-12.13, result.Rms(from, to), 0.2, $"level at {from}-{to}");
        }
    }

    [AvaloniaTheory]
    [InlineData("mp3", "mp3", 0.1)]
    [InlineData("flac", "flac", 0.01)]
    [InlineData("m4a", "aac", 0.1)]
    [InlineData("ogg", "vorbis", 0.1)]
    [InlineData("opus", "opus", 0.1)]
    [InlineData("aiff", "pcm_s16be", 0.01)]
    public async Task Bleep_writes_every_format_in_its_own_with_the_tone_where_the_ranges_are(string extension, string codec, double lengthMargin)
    {
        var rate = extension == "opus" ? 48000 : 44100;
        var file = await MakeAsync($"voz.{extension}", Mono, 6, rate);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = new RedactAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        foreach (var (start, end) in new[] { (1.2, 2.0), (3.5, 4.1) })
        {
            panel.Start = start;
            panel.End = end;
            panel.AddSelection();
        }
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal($"voz con pitidos.{extension}", Path.GetFileName(output));
        var probe = await media.ProbeAsync(output);
        Assert.Equal(codec, probe.AudioCodec);
        Assert.Equal(rate, probe.SampleRate);
        Assert.Equal((await media.ProbeAsync(file)).Duration, probe.Duration, lengthMargin);

        var result = await DecodeAsync(output);
        foreach (var (from, to) in new[] { (0.2, 1.1), (2.1, 3.4), (4.2, 5.8) })
        {
            AssertDb(-6.02, result.Tone(440, from, to), 0.8, $"tone at {from}-{to}");
            Assert.True(result.Tone(1000, from, to) < -40, $"a bleep leaks out at {from}-{to}");
        }
        foreach (var (from, to) in new[] { (1.3, 1.9), (3.6, 4.0) })
        {
            AssertDb(-9.12, result.Tone(1000, from, to), 0.8, $"bleep at {from}-{to}");
            Assert.True(result.Tone(440, from, to) < -40, $"the voice is still heard at {from}-{to}");
        }
        // The cut is where the waveform shows it, even for an MP3 whose first frame starts a few milliseconds in (±8 ms for a lossy encoder).
        Assert.InRange(result.OnsetOf(1000, 1.1, 1.4), 1.192, 1.208);
        Assert.InRange(result.OnsetOf(1000, 3.4, 3.7), 3.492, 3.508);
    }

    [AvaloniaFact]
    public async Task Bleep_keeps_the_tags_and_both_channels_of_a_stereo_mp3()
    {
        var file = await MakeAsync("voz.mp3", Stereo, 6, 44100, "-metadata", "artist=Autor01");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new RedactAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        panel.Start = 2;
        panel.End = 3;
        panel.AddSelection();
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal("voz con pitidos.mp3", Path.GetFileName(output));
        var probe = await MediaTools.Locate()!.ProbeAsync(output);
        Assert.Equal("Autor01", probe.Tags["artist"]);
        Assert.Equal(2, probe.Channels);
        // Both channels carry the tone inside the range (the same level on each) and their own tone outside it.
        foreach (var (side, hertz, level) in new[] { (0, 440, -6.02), (1, 880, -12.04) })
        {
            var pcm = await DecodeAsync(output, side);
            AssertDb(level, pcm.Tone(hertz, 0.3, 1.8), 0.8, $"side {side} before");
            AssertDb(-9.12, pcm.Tone(1000, 2.1, 2.9), 0.8, $"side {side} bleep");
            Assert.True(pcm.Tone(hertz, 2.1, 2.9) < -40, $"side {side}: the voice is still heard");
            AssertDb(level, pcm.Tone(hertz, 3.2, 5.7), 0.8, $"side {side} after");
        }
    }

    [AvaloniaFact]
    public async Task Bleep_volume_sets_the_loudness_of_the_tone_down_to_silence()
    {
        var file = await MakeAsync("voz.wav", Mono, 4);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        foreach (var (volume, expected) in new[] { (100.0, 0.0), (10.0, -20.0), (0.0, double.NegativeInfinity) })
        {
            var panel = new RedactAudioPanel(Context(file)) { Volume = volume };
            await WaitAsync(() => !panel.IsLoading);
            panel.Start = 1;
            panel.End = 3;
            panel.AddSelection();
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            var result = await DecodeAsync(Assert.Single(panel.Outputs));
            if (double.IsNegativeInfinity(expected))
                Assert.True(result.Peak(1.05, 2.95) < 1e-4, "a bleep at 0 % is silence");
            else
                AssertDb(expected, result.Tone(1000, 1.2, 2.8), 0.3, $"bleep at {volume} %");
            AssertDb(-6.02, result.Tone(440, 0.1, 0.9), 0.1, "before");
        }
    }

    [AvaloniaFact]
    public async Task Bleep_at_the_edges_of_the_file_leaves_no_gap_and_no_extra_sample()
    {
        var file = await MakeAsync("voz.wav", Mono, 3);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var original = await DecodeAsync(file);
        // From the very first sample, to the very last, and the whole file.
        foreach (var (start, end) in new[] { (0.0, 0.8), (2.2, 3.0), (0.0, 3.0), (0.0004, 2.9996) })
        {
            var panel = new RedactAudioPanel(Context(file));
            await WaitAsync(() => !panel.IsLoading);
            panel.Start = start;
            panel.End = end;
            panel.AddSelection();
            await panel.RunAsync();
            Assert.True(panel.IsDone, $"{start}-{end}: {panel.Status}");
            var result = await DecodeAsync(Assert.Single(panel.Outputs));
            Assert.Equal(original.Samples.Length, result.Samples.Length);
            AssertDb(-9.12, result.Tone(1000, Math.Max(start, 0.01) + 0.1, Math.Min(end, 2.99) - 0.1), 0.3, $"{start}-{end}");
        }
    }

    [AvaloniaFact]
    public async Task Bleep_takes_dozens_of_ranges_through_a_file_when_the_graph_is_too_long_for_the_command_line()
    {
        var file = await MakeAsync("charla.wav", Mono, 100);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new RedactAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        // 120 ranges of a third of a second, one every 0.75 s.
        for (var i = 0; i < 120; i++)
        {
            panel.Start = 0.5 + i * 0.75;
            panel.End = panel.Start + 0.33;
            panel.AddSelection();
        }
        Assert.Equal(120, panel.Ranges.Count);
        var graph = BleepGraph.Build([.. panel.Marks], 100, 44100, 0.35);
        Assert.True(graph.Length > RedactAudioPanel.InlineGraphLimit, $"only {graph.Length} characters: the file path is not exercised");

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("charla con pitidos.wav"), output);
        var original = await DecodeAsync(file);
        var result = await DecodeAsync(output);
        Assert.Equal(original.Samples.Length, result.Samples.Length);
        foreach (var i in new[] { 0, 1, 57, 118, 119 })
        {
            var start = 0.5 + i * 0.75;
            AssertDb(-9.12, result.Tone(1000, start + 0.04, start + 0.29), 0.3, $"range {i}");
            AssertDb(-6.02, result.Tone(440, start + 0.4, start + 0.7), 0.1, $"after range {i}");
        }
    }

    [AvaloniaFact]
    public async Task Bleep_keeps_the_bit_depth_of_a_flac()
    {
        var deep = await MakeAsync("hifi.flac", Mono, 3, 44100, "-c:a", "flac", "-sample_fmt", "s32");
        var plain = await MakeAsync("normal.flac", Mono, 3, 44100, "-c:a", "flac", "-sample_fmt", "s16");
        if (deep is null || plain is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        Assert.Equal(24, await BitsAsync(deep));
        Assert.Equal(16, await BitsAsync(plain));
        foreach (var (file, bits) in new[] { (deep, 24), (plain, 16) })
        {
            var panel = new RedactAudioPanel(Context(file));
            await WaitAsync(() => !panel.IsLoading);
            panel.Start = 1;
            panel.End = 2;
            panel.AddSelection();
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            Assert.Equal(bits, await BitsAsync(Assert.Single(panel.Outputs)));    // not 24 for a 16-bit file: the graph's samples are float
        }
    }

    [AvaloniaFact]
    public async Task Bleep_takes_a_range_that_reaches_past_the_end_of_the_file()
    {
        var file = await MakeAsync("voz.wav", Mono, 3);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new RedactAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        panel.Ranges.Add(new BleepRange(2.5, 9));                       // as a list that was built some other way could hold
        Assert.True(panel.CanApply);
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var result = await DecodeAsync(Assert.Single(panel.Outputs));
        Assert.Equal(3.0, result.Seconds, 3);
        AssertDb(-9.12, result.Tone(1000, 2.6, 2.95), 0.3, "bleep to the end");
    }

    // ───────── VISUALIZE ─────────

    [AvaloniaTheory]
    [InlineData("en", "Visualize")]
    [InlineData("es", "Visualizar")]
    [InlineData("ko", "시각화")]
    [InlineData("zh-CN", "可视化")]
    public async Task The_visualize_panel_renders_a_real_preview_in_every_language(string language, string title)
    {
        var file = await MakeAsync("cancion.mp3", $"{Speech}|{Speech}", 10);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioToVideoPanel(ContextIn(language, file));
        await WaitAsync(() => panel.Preview is not null || panel.PreviewError is not null);
        Assert.Null(panel.PreviewError);
        Assert.Null(panel.LoadError);

        Render(panel, $"audiovisual-{language}");
        Assert.Equal(title, panel.Title);
    }

    [AvaloniaTheory]
    [InlineData(VisualFormat.Vertical, VisualStyle.Bars, "audiovisual-vertical-bars")]
    [InlineData(VisualFormat.Square, VisualStyle.Wave, "audiovisual-square-wave")]
    [InlineData(VisualFormat.Horizontal, VisualStyle.Bars, "audiovisual-horizontal-bars")]
    public async Task The_visualize_panel_renders_every_shape_and_style(VisualFormat format, VisualStyle style, string name)
    {
        var file = await MakeAsync("cancion.mp3", $"{Speech}|{Speech}", 10);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioToVideoPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        panel.Format = format;
        panel.Style = style;
        panel.WaveColor = "#2EC4B6";
        panel.BackgroundColor = "#1E2A4A";
        await WaitAsync(() => panel.Preview is { } preview && Math.Abs(preview.PixelSize.Width / (double)preview.PixelSize.Height - VisualGraph.SizeOf(format).Width / (double)VisualGraph.SizeOf(format).Height) < 0.01
            && PreviewShows(panel, "#2EC4B6"));
        Render(panel, name);
    }

    /// <summary>Whether the panel's preview has pixels of <paramref name="hex"/> in it (so it is the preview of the options now, not an older one).</summary>
    private static bool PreviewShows(AudioToVideoPanel panel, string hex)
    {
        if (panel.Preview is not { } preview)
            return false;
        using var stream = new MemoryStream();
        preview.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        using var image = new MagickImage(stream);
        var (r, g, b) = Rgb(hex);
        var data = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var count = 0;
        for (var i = 0; i < data.Length; i += 3)
            if (Math.Abs(data[i] - r) < 12 && Math.Abs(data[i + 1] - g) < 12 && Math.Abs(data[i + 2] - b) < 12)
                count++;
        return count > 200;
    }

    [AvaloniaFact]
    public async Task The_visualize_preview_follows_the_options_in_the_shape_chosen()
    {
        var file = await MakeAsync("cancion.mp3", $"{Speech}|{Speech}", 6);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioToVideoPanel(Context(file));
        Assert.True(panel.ShowsHint);
        await WaitAsync(() => panel.Preview is not null);
        Assert.Equal(new Avalonia.PixelSize(960, 540), panel.Preview!.PixelSize);
        Assert.Equal((440.0, 248.0), (panel.PreviewWidth, panel.PreviewHeight));
        Assert.Equal("1920 × 1080 · 30 fps · MP4", panel.SizeText);
        Assert.True(PreviewShows(panel, "#FF5200"));
        Assert.True(panel.ShowsPreview);
        Assert.False(panel.ShowsHint);
        Assert.Equal("Color de la onda", panel.ColorText);

        // A new shape hides the old picture (it would be squeezed into the wrong box) until the new one is drawn.
        panel.SetFormatCommand.Execute(VisualFormat.Vertical);
        Assert.False(panel.ShowsPreview);
        Assert.True(panel.ShowsHint);
        await WaitAsync(() => panel.Preview is { PixelSize: { Width: 540, Height: 960 } });
        Assert.True(panel.ShowsPreview);
        Assert.Equal((169.0, 300.0), (panel.PreviewWidth, panel.PreviewHeight));
        Assert.Equal("1080 × 1920 · 30 fps · MP4", panel.SizeText);
        Assert.True(panel.IsVertical && !panel.IsHorizontal && !panel.IsSquare);

        panel.SetFormatCommand.Execute(VisualFormat.Square);
        await WaitAsync(() => panel.Preview is { PixelSize: { Width: 540, Height: 540 } });
        Assert.Equal((300.0, 300.0), (panel.PreviewWidth, panel.PreviewHeight));

        // A run of quick changes ends on a preview of the last one: the earlier ones are cancelled.
        panel.SetStyleCommand.Execute(VisualStyle.Bars);
        Assert.Equal("Color de las barras", panel.ColorText);
        panel.BackgroundColor = "#FFFFFF";
        panel.WaveColor = "#3B82F6";
        panel.WaveColor = "#A98CEF";
        await WaitAsync(() => PreviewShows(panel, "#A98CEF") && PreviewShows(panel, "#FFFFFF"));
        Assert.False(PreviewShows(panel, "#3B82F6"));
        Assert.False(PreviewShows(panel, "#FF5200"));
        Assert.Null(panel.PreviewError);
    }

    [AvaloniaFact]
    public async Task The_visualize_preview_works_for_a_very_short_file_and_reports_a_file_with_no_audio()
    {
        var shortFile = await MakeAsync("clic.wav", Speech, 0.4);
        var silent = await MakeSilentVideoAsync("mudo.mp4");
        if (shortFile is null || silent is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioToVideoPanel(Context(shortFile));
        await WaitAsync(() => panel.Preview is not null || panel.PreviewError is not null);
        Assert.Null(panel.PreviewError);
        Assert.True(PreviewShows(panel, "#FF5200"));

        var none = new AudioToVideoPanel(Context(silent));
        await WaitAsync(() => !none.IsLoading);
        Assert.Equal("Este archivo no tiene audio.", none.LoadError);
        Assert.False(none.CanApply);
        Assert.Null(none.Preview);
    }

    [AvaloniaTheory]
    [InlineData(VisualFormat.Horizontal, 1920, 1080)]
    [InlineData(VisualFormat.Vertical, 1080, 1920)]
    [InlineData(VisualFormat.Square, 1080, 1080)]
    public async Task Visualize_writes_an_mp4_of_the_shape_chosen_with_the_audio_and_its_length(VisualFormat format, int width, int height)
    {
        var file = await MakeAsync("cancion.mp3", $"{Speech}|{Speech}", 4, 44100, "-metadata", "artist=Autor01");
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var media = MediaTools.Locate()!;
        var before = await File.ReadAllBytesAsync(file);
        var panel = new AudioToVideoPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.True(panel.CanApply);
        panel.Format = format;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(PathIn("cancion visualizado.mp4"), output);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));                 // the original is untouched
        var probe = await media.ProbeAsync(output);
        Assert.Equal((width, height), (probe.Width, probe.Height));
        Assert.True(probe.HasVideo && probe.HasAudio);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.Equal(2, probe.Channels);
        Assert.InRange(probe.FrameRate, 29.9, 30.1);
        Assert.InRange(probe.Duration, 3.7, 4.3);                                 // the audio's length (± 0.3 s)
        Assert.Equal("Autor01", probe.Tags["artist"]);

        // The wave is drawn in the orange over the plum, and fills a real part of the frame.
        var (orange, total) = await CountPixelsAsync(output, 2.0, Rgb("#FF5200"));
        Assert.True(orange > total * 0.01, $"only {orange} of {total} pixels are the wave's orange");
        var (plum, _) = await CountPixelsAsync(output, 2.0, Rgb("#1B1620"), 12);
        Assert.True(plum > total * 0.4, $"only {plum} of {total} pixels are the background");
        Assert.True(orange + plum > total * 0.9, "the frame holds colours other than the two chosen");
    }

    [AvaloniaFact]
    public async Task Visualize_draws_bars_in_the_colours_chosen_and_nothing_of_the_default_ones()
    {
        var file = await MakeAsync("cancion.mp3", $"{Speech}|{Speech}", 4);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioToVideoPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        panel.SetStyleCommand.Execute(VisualStyle.Bars);
        panel.Format = VisualFormat.Square;
        panel.WaveColor = "#3B82F6";
        panel.BackgroundColor = "#F2EDE6";

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        var probe = await MediaTools.Locate()!.ProbeAsync(output);
        Assert.Equal((1080, 1080), (probe.Width, probe.Height));
        Assert.InRange(probe.Duration, 3.7, 4.3);
        var (blue, total) = await CountPixelsAsync(output, 2.0, Rgb("#3B82F6"));
        var (cream, _) = await CountPixelsAsync(output, 2.0, Rgb("#F2EDE6"), 12);
        var (orange, _) = await CountPixelsAsync(output, 2.0, Rgb("#FF5200"));
        Assert.True(blue > total * 0.01, $"only {blue} of {total} pixels are the bars' blue");
        Assert.True(cream > total * 0.4, $"only {cream} of {total} pixels are the background");
        Assert.True(orange < total * 0.001, $"{orange} pixels are the default orange");
    }

    [AvaloniaFact]
    public async Task Visualize_keeps_the_colours_true_for_a_player_that_assumes_rec709()
    {
        var file = await MakeAsync("cancion.mp3", $"{Speech}|{Speech}", 3);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioToVideoPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);

        // H.264 for HD is read as BT.709 by players whatever the stream says; written as BT.601 the orange comes out yellower
        // (#FF5D00). The file must say BT.709 and mean it: read back as 709, the orange is still the orange.
        var probe = await ProcessRunner.RunAsync(MediaTools.Locate()!.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_space,color_primaries,color_transfer,color_range", "-of", "default=nw=1", output],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Contains("color_space=bt709", probe.StandardOutput);
        Assert.Contains("color_primaries=bt709", probe.StandardOutput);
        Assert.Contains("color_range=tv", probe.StandardOutput);
        var png = PathIn("cuadro.png");
        await MediaTools.Locate()!.RunAsync(["-ss", "1.5", "-i", output, "-frames:v", "1", "-vf",
            "scale=in_color_matrix=bt709:in_range=tv:out_color_matrix=bt709:out_range=pc,format=rgb24", png]);
        using var image = new MagickImage(png);
        var data = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        // The corner is the plum (#1B1620) to within the rounding of the limited range.
        Assert.InRange(data[0], 22, 31);
        Assert.InRange(data[1], 17, 27);
        Assert.InRange(data[2], 27, 37);
        var orange = 0;
        for (var i = 0; i < data.Length; i += 3)
            if (data[i] > 240 && Math.Abs(data[i + 1] - 0x52) <= 6 && data[i + 2] < 12)
                orange++;
        Assert.True(orange > 500, $"only {orange} pixels are the orange read back as BT.709");
    }

    [AvaloniaFact]
    public async Task Visualize_can_be_cancelled_and_leaves_nothing_behind()
    {
        var file = await MakeAsync("larga.mp3", $"{Speech}|{Speech}", 120);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var panel = new AudioToVideoPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        var run = panel.RunAsync();
        await WaitAsync(() => panel.Progress > 0, 90000);                          // FFmpeg is on its way
        panel.Cancel();
        await run;

        Assert.False(panel.IsDone);
        Assert.Equal("Cancelado", panel.Status);
        Assert.False(File.Exists(PathIn("larga visualizado.mp4")));
        Assert.Empty(Directory.GetFiles(_root, "*.mp4"));
    }

    [AvaloniaFact]
    public async Task Visualize_does_not_overwrite_a_video_that_is_already_there()
    {
        var file = await MakeAsync("tono.wav", Speech, 2);
        if (file is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        for (var run = 1; run <= 2; run++)
        {
            var panel = new AudioToVideoPanel(Context(file));
            await WaitAsync(() => !panel.IsLoading);
            panel.Format = VisualFormat.Square;
            await panel.RunAsync();
            Assert.True(panel.IsDone, panel.Status);
            Assert.Equal(PathIn(run == 1 ? "tono visualizado.mp4" : "tono visualizado 2.mp4"), Assert.Single(panel.Outputs));
        }
    }

    [AvaloniaFact]
    public void The_swatch_picker_takes_a_swatch_a_full_typed_colour_or_a_short_one_and_refuses_the_rest()
    {
        TestServices.EnsureInitialized("es");
        var picker = new VisualSwatchPicker { Palette = VisualColors.WavePalette, Hex = "#FF5200" };
        var other = new Avalonia.Controls.TextBox();
        var window = new Avalonia.Controls.Window { Width = 420, Height = 120, Content = new Avalonia.Controls.StackPanel { Children = { picker, other } } };
        window.Show();
        Pump();
        var box = picker.Children.OfType<Avalonia.Controls.TextBox>().Single();
        var swatches = picker.Children.OfType<Avalonia.Controls.Primitives.UniformGrid>().Single().Children.OfType<Avalonia.Controls.Border>().ToList();
        Assert.Equal(VisualColors.WavePalette.Count, swatches.Count);
        Assert.Equal("#FF5200", box.Text);

        // The swatch of the colour in use has the ring; no other does.
        IEnumerable<int> Ringed() => swatches.Select((swatch, index) => (swatch, index))
            .Where(pair => pair.swatch.BorderBrush is Avalonia.Media.SolidColorBrush { Color.A: > 0 }).Select(pair => pair.index);
        Assert.Equal([0], Ringed());
        picker.Hex = "#3B82F6";
        Assert.Equal([5], Ringed());
        Assert.Equal("#3B82F6", box.Text);

        // Typing: a full colour counts at once, a half-typed one waits, a short one counts when the box loses focus.
        box.Focus();
        Pump();
        box.Text = "#2EC4B6";
        Assert.Equal("#2EC4B6", picker.Hex);
        Assert.Equal([6], Ringed());
        box.Text = "#FFF";
        Assert.Equal("#2EC4B6", picker.Hex);
        other.Focus();
        Pump();
        Assert.Equal("#FFFFFF", picker.Hex);
        Assert.Equal("#FFFFFF", box.Text);

        // A colour that is not one is put back to the one in use.
        box.Focus();
        Pump();
        box.Text = "#GG0000";
        other.Focus();
        Pump();
        Assert.Equal("#FFFFFF", picker.Hex);
        Assert.Equal("#FFFFFF", box.Text);

        // A colour outside the swatches rings none.
        picker.Hex = "#123456";
        Assert.DoesNotContain(picker.Hex, VisualColors.WavePalette);
        Assert.Empty(Ringed());
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_three_tools_work_on_an_mp3_with_cover_art_and_keep_its_tags()
    {
        var sound = await MakeAsync("sonido.mp3", Stereo, 4);
        var picture = await MakeMediaAsync(PathIn("portada.jpg"), "-f", "lavfi", "-i", "color=c=red:s=96x96", "-frames:v", "1");
        if (sound is null || picture is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        // The picture goes into the MP3 as its cover, with the tags of a song.
        var file = await MakeMediaAsync(PathIn("portada.mp3"), "-i", sound, "-i", picture, "-map", "0:a", "-map", "1:v", "-c", "copy", "-id3v2_version", "3",
            "-metadata", "title=Con portada", "-metadata", "artist=Autor01", "-disposition:v", "attached_pic");
        Assert.NotNull(file);
        var media = MediaTools.Locate()!;
        var channels = new AudioChannelsPanel(Context(file));
        var bleep = new RedactAudioPanel(Context(file));
        var visual = new AudioToVideoPanel(Context(file));
        await WaitAsync(() => !channels.IsLoading && !bleep.IsLoading && !visual.IsLoading);
        Assert.Null(channels.LoadError);
        Assert.Null(bleep.LoadError);
        Assert.Null(visual.LoadError);
        bleep.Start = 1;
        bleep.End = 2;
        bleep.AddSelection();

        foreach (var panel in new ToolPanel[] { channels, bleep, visual })
        {
            await panel.RunAsync();
            Assert.True(panel.IsDone, $"{panel.Id}: {panel.Status}");
            var probe = await media.ProbeAsync(Assert.Single(panel.Outputs));
            Assert.Equal("Autor01", probe.Tags["artist"]);
            Assert.Equal("Con portada", probe.Tags["title"]);
            Assert.InRange(probe.Duration, 3.7, 4.3);
        }
        Assert.Equal("portada mono.mp3", Path.GetFileName(channels.Outputs[0]));
        Assert.Equal("portada con pitidos.mp3", Path.GetFileName(bleep.Outputs[0]));
        Assert.Equal("portada visualizado.mp4", Path.GetFileName(visual.Outputs[0]));
    }

    // ───────── Files that cannot be used ─────────

    [AvaloniaFact]
    public async Task Every_audio_tool_says_so_when_the_file_has_no_audio_or_cannot_be_read()
    {
        var silent = await MakeSilentVideoAsync("mudo.mp4");
        if (silent is null)
        {
            Assert.Skip(NoFfmpeg);
            return;
        }
        var missing = PathIn("no existe.mp3");
        foreach (var file in new[] { silent, missing })
        {
            var channels = new AudioChannelsPanel(Context(file));
            var bleep = new RedactAudioPanel(Context(file));
            var visual = new AudioToVideoPanel(Context(file));
            await WaitAsync(() => !channels.IsLoading && !bleep.IsLoading && !visual.IsLoading);
            Assert.NotNull(channels.LoadError);
            Assert.NotNull(bleep.LoadError);
            Assert.NotNull(visual.LoadError);
            Assert.False(channels.CanApply);
            Assert.False(bleep.CanApply);
            Assert.False(visual.CanApply);
            if (file == silent)
            {
                Assert.Equal("Este archivo no tiene audio.", channels.LoadError);
                Assert.Equal("Este archivo no tiene audio.", bleep.LoadError);
                Assert.Equal("Este archivo no tiene audio.", visual.LoadError);
            }
            Render(channels, "audiochannels-error");
            Render(bleep, "audiobleep-error");
            Render(visual, "audiovisual-error");
        }
    }

    [AvaloniaFact]
    public void The_audio_petals_open_these_panels_and_the_titles_and_suffixes_exist_in_every_language()
    {
        foreach (var language in new[] { "en", "es", "ko", "zh-CN" })
        {
            var context = ContextIn(language, PathIn("x.mp3"));
            foreach (var id in new[] { "audioChannels", "redactAudio", "audioToVideo" })
            {
                Assert.True(PanelRegistry.Has(id));
                Assert.NotEqual($"wheel.{id}", context[$"wheel.{id}"]);
                Assert.NotEqual($"wheel.{id}.suffix", context[$"wheel.{id}.suffix"]);
            }
            Assert.NotEqual("panel.audioChannels.suffix_stereo", context["panel.audioChannels.suffix_stereo"]);
        }
        Assert.Equal("estéreo", Context(PathIn("x.mp3"))["panel.audioChannels.suffix_stereo"]);
        Assert.Equal("스테레오", ContextIn("ko", PathIn("x.mp3"))["panel.audioChannels.suffix_stereo"]);
        Assert.Equal("立体声", ContextIn("zh-CN", PathIn("x.mp3"))["panel.audioChannels.suffix_stereo"]);
        Assert.Equal("Stereo", ContextIn("en", PathIn("x.mp3"))["panel.audioChannels.suffix_stereo"]);
    }
}
