// Mandarina: the transcription tools (TRANSCRIBE with one click, SUBTITLES with choices). The text work (SRT, VTT and TXT, tidying
// what chunking leaves behind, fitting the screen) and the filter graph are checked on their own. The rest listens to a real
// voice: Windows' own text-to-speech reads a sentence, FFmpeg's whisper filter transcribes it with the real model and the tests
// look at what was saved. Those are skipped where the model (Settings → Engines → Whisper), FFmpeg or a voice is missing.

using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using Avalonia.Headless.XUnit;
using Filee.App.Mandarina;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaTranscribeTests : IDisposable
{
    private const string SpanishText = "Hola, esto es una prueba de transcripción local. Mañana viajaré a Madrid para ver a mi familia.";
    private const string EnglishText = "Hello, this is a local transcription test. Tomorrow I will travel to Madrid to see my family.";

    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-transcribe").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ───────── The text work ─────────

    private const string WhisperSrt = "\uFEFF0\r\n00:00:00,000 --> 00:00:02,780\r\nHola, esto es una prueba de transcripción local.\r\n\r\n" +
                                      "1\r\n00:00:03,791 --> 00:00:05,951\r\nMañana viajaré a Madrid para ver a mi familia.\r\n\r\n" +
                                      "2\r\n00:00:05,943 --> 00:00:08,923\r\nEl presupuesto del proyecto es de 12.000 euros.\r\n\r\n" +
                                      "3\r\n00:00:08,915 --> 00:00:09,235\r\neuros.\r\n\r\n" +
                                      "4\r\n00:00:09,300 --> 00:00:12,000\r\n[BLANK_AUDIO]\r\n";

    [Fact]
    public void Subtitles_read_what_the_whisper_filter_writes()
    {
        var cues = Subtitles.Parse(WhisperSrt);

        Assert.Equal(5, cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(3791), cues[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(5951), cues[1].End);
        Assert.Equal("Mañana viajaré a Madrid para ver a mi familia.", cues[1].Text);
    }

    [Fact]
    public void Tidying_drops_silence_notes_overlaps_and_the_fragment_a_seam_repeats()
    {
        var cues = Subtitles.Tidy(Subtitles.Parse(WhisperSrt));

        // "euros." repeated the end of the cue before it, and "[BLANK_AUDIO]" is a note about silence.
        Assert.Equal(["Hola, esto es una prueba de transcripción local.", "Mañana viajaré a Madrid para ver a mi familia.",
            "El presupuesto del proyecto es de 12.000 euros."], cues.Select(cue => cue.Text));
        for (var i = 1; i < cues.Count; i++)
            Assert.True(cues[i].Start >= cues[i - 1].End, $"cue {i} starts before the one before it ends");
        Assert.True(cues[^1].End >= TimeSpan.FromMilliseconds(9235), "the repeated fragment's time goes to the cue it repeats");
    }

    [Fact]
    public void Tidying_drops_what_the_model_writes_over_silence_and_noise()
    {
        var cues = Subtitles.Tidy([
            new Cue(TimeSpan.Zero, TimeSpan.FromSeconds(8), "."),
            new Cue(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(38), "Thank you."),
            new Cue(TimeSpan.FromSeconds(38), TimeSpan.FromSeconds(41), "Gracias."), // short: a real answer
            new Cue(TimeSpan.FromSeconds(41), TimeSpan.FromSeconds(70), "Esto sí es una frase larga de verdad en la reunión."),
        ]);

        Assert.Equal(["Gracias.", "Esto sí es una frase larga de verdad en la reunión."], cues.Select(cue => cue.Text));
    }

    [Fact]
    public void Long_cues_are_cut_to_two_short_lines_and_share_the_time()
    {
        var text = "El presupuesto total es de 12.000 euros y ya se han gastado 8.500 en la primera fase del proyecto de ingeniería. " +
                   "La directora comercial propone abrir una nueva oficina en Barcelona durante la primavera del año que viene.";
        var cues = Subtitles.Fit([new Cue(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(22), text)]);

        Assert.True(cues.Count >= 2);
        foreach (var cue in cues)
        {
            var lines = cue.Text.Split('\n');
            Assert.InRange(lines.Length, 1, 2);
            Assert.All(lines, line => Assert.True(line.Length <= 42, $"“{line}” is longer than 42 characters"));
        }
        Assert.Equal(TimeSpan.FromSeconds(10), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(22), cues[^1].End);
        for (var i = 1; i < cues.Count; i++)
            Assert.Equal(cues[i - 1].End, cues[i].Start); // the time of the original, shared out without gaps
        Assert.Equal(text.Replace(" ", ""), string.Concat(cues.Select(cue => cue.Text.Replace("\n", "").Replace(" ", "")))); // no word lost
        Assert.DoesNotContain(cues, cue => cue.Text.StartsWith("000", StringComparison.Ordinal)); // "12.000" is not a sentence end
    }

    [Fact]
    public void A_short_cue_is_left_as_it_is()
    {
        var cue = new Cue(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hola, esto es una prueba.");

        Assert.Equal([cue], Subtitles.Fit([cue]));
    }

    [Fact]
    public void The_three_formats_are_written_as_players_expect_them()
    {
        Cue[] cues = [new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), "Hola"), new(TimeSpan.FromSeconds(3725.25), TimeSpan.FromSeconds(3727), "Adiós")];

        Assert.Equal("1\n00:00:01,500 --> 00:00:04,000\nHola\n\n2\n01:02:05,250 --> 01:02:07,000\nAdiós\n\n", Subtitles.ToSrt(cues));
        Assert.Equal("WEBVTT\n\n00:00:01.500 --> 00:00:04.000\nHola\n\n01:02:05.250 --> 01:02:07.000\nAdiós\n\n", Subtitles.ToVtt(cues));
        Assert.Equal("Hola\nAdiós\n", Subtitles.ToText(cues));
        Assert.Equal(cues, Subtitles.Parse(Subtitles.ToVtt(cues).Replace("WEBVTT\n\n", ""))); // what is written can be read back
    }

    [Fact]
    public void A_path_is_quoted_for_the_filter_graph_whatever_it_holds()
    {
        Assert.Equal("'C\\:/Users/Test/model.bin'", WhisperEngine.Quote(@"C:\Users\Test\model.bin"));
        Assert.Equal("'C\\:/Users/O\\'\\''Brien/model.bin'", WhisperEngine.Quote(@"C:\Users\O'Brien\model.bin"));
    }

    [Fact]
    public async Task FFmpeg_reads_a_file_from_a_folder_with_a_quote_in_its_name_through_that_quoting()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "it's a folder")).FullName;
        var tone = await MakeMediaAsync(Path.Combine(folder, "tone.wav"), "-f", "lavfi", "-i", "sine=frequency=440:duration=1");
        if (tone is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests");
            return;
        }

        // Throws a PanelException with FFmpeg's message when the path does not get through.
        await MediaTools.Locate()!.RunAsync(["-f", "lavfi", "-i", "amovie=" + WhisperEngine.Quote(tone), "-f", "null", "-"]);
    }

    [Fact]
    public void The_graph_names_the_model_the_language_the_device_and_where_the_subtitles_go()
    {
        var graph = WhisperEngine.Graph(@"C:\m\model.bin", @"C:\t\out.srt", "es", gpu: 1, queue: 120);

        Assert.Equal("whisper=model='C\\:/m/model.bin':language=es:queue=120:use_gpu=true:gpu_device=1:format=srt:destination='C\\:/t/out.srt'", graph);
        Assert.Contains("language=auto", WhisperEngine.Graph("m", "o", null, null, 120));
        Assert.Contains("use_gpu=false", WhisperEngine.Graph("m", "o", "auto", null, 120));
        Assert.DoesNotContain("gpu_device", WhisperEngine.Graph("m", "o", "auto", null, 120));
        Assert.Throws<ArgumentException>(() => WhisperEngine.Graph("m", "o", "es:use_gpu=1", null, 120)); // a language is a code, not a graph
    }

    [Fact]
    public void The_dedicated_graphics_card_is_chosen_not_the_integrated_one()
    {
        const string both =
            "[Parsed_whisper_0 @ 0000025606baac80] ggml_vulkan: Found 2 Vulkan devices:\n" +
            "[Parsed_whisper_0 @ 0000025606baac80] ggml_vulkan: 0 = AMD Radeon(TM) Graphics (AMD proprietary driver) | uma: 1 | fp16: 1 | bf16: 0\n" +
            "[Parsed_whisper_0 @ 0000025606baac80] ggml_vulkan: 1 = NVIDIA GeForce RTX 4060 Laptop GPU (NVIDIA) | uma: 0 | fp16: 1 | bf16: 0\n";

        Assert.Equal(new WhisperDevice(1, "NVIDIA GeForce RTX 4060 Laptop GPU"), WhisperEngine.ChooseDevice(both));
        Assert.Null(WhisperEngine.ChooseDevice(both.Split('\n')[1]).Gpu); // only the integrated one: it wrote garbage on this PC, so the processor
        Assert.Null(WhisperEngine.ChooseDevice("whisper_init: no GPU found").Gpu);
    }

    [Fact]
    public void The_languages_are_named_in_themselves_and_ready_for_the_filter()
    {
        var all = WhisperLanguages.All;

        Assert.Equal(all.Count, all.Select(language => language.Code).Distinct().Count());
        Assert.Contains(new WhisperLanguage("es", "Español"), all);
        Assert.Equal("English", all.Single(language => language.Code == "en").Name);
        Assert.Equal("한국어", all.Single(language => language.Code == "ko").Name);
        Assert.All(all, language => Assert.Matches("^[a-z]{2,3}$", language.Code));
    }

    [Fact]
    public void A_pair_of_files_keeps_one_name_and_its_numbers_together()
    {
        var clip = Path.Combine(_root, "clip.mp4");

        Assert.Equal([Path.Combine(_root, "clip.txt"), Path.Combine(_root, "clip.srt")], OutputPaths.BesideAll(clip, "", ".txt", ".srt"));
        File.WriteAllText(Path.Combine(_root, "clip.srt"), "taken");
        Assert.Equal([Path.Combine(_root, "clip 2.txt"), Path.Combine(_root, "clip 2.srt")], OutputPaths.BesideAll(clip, "", ".txt", ".srt"));
    }

    // ───────── The wheel ─────────

    [AvaloniaFact]
    public void Audio_and_video_get_the_two_petals_and_nothing_else_does()
    {
        var loc = Context().Loc;
        string Make(string name, params byte[] bytes)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, bytes.Length == 0 ? [0] : bytes);
            return path;
        }
        string[] Speech(params string[] files) =>
            WheelCatalog.ToolsFor(files, loc).Select(entry => entry.Id).Where(id => id is "transcribe" or "subtitles").ToArray();

        foreach (var name in new[] { "a.mp3", "a.wav", "a.flac", "v.mp4", "v.mkv", "v.mov" })
            Assert.Equal(["transcribe", "subtitles"], Speech(Make(name)));
        Assert.Equal(["transcribe", "subtitles"], Speech(Make("b.mp3"), Make("c.mp3")));
        Assert.Equal(["transcribe", "subtitles"], Speech(Make("w.mp4"), Make("x.mp4")));

        Assert.Empty(Speech(Make("p.jpg", 0xFF, 0xD8, 0xFF)));
        Assert.Empty(Speech(Make("d.pdf", (byte)'%', (byte)'P', (byte)'D', (byte)'F')));
        Assert.Empty(Speech(Make("z.zip", 0x50, 0x4B, 3, 4)));
    }

    [AvaloniaFact]
    public void Without_the_model_the_petals_say_what_is_missing()
    {
        var loc = Context().Loc;

        var reason = PanelRegistry.Unavailable("transcribe", loc);

        var expected = MediaTools.Locate() is null ? loc.Format("engine.reason.needs_package", "FFmpeg")
            : !WhisperModel.Installed().Any ? loc.Format("engine.reason.needs_package", "Whisper")
            : null;
        Assert.Equal(expected, reason);
        Assert.Equal(reason, PanelRegistry.Unavailable("subtitles", loc));
    }

    [AvaloniaFact]
    public void The_model_is_a_package_of_the_engines_page_pinned_by_its_checksum()
    {
        var package = Filee.Engines.Infrastructure.EngineDownloads.Packages.Single(p => p.Id == "whisper");
        var component = Filee.Engines.Infrastructure.EngineDownloads.Components[package.Components.Single()];

        Assert.Equal("file", component.Kind);
        Assert.EndsWith(WhisperModel.LargeFile, component.Url);
        Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
        Assert.Equal(574_041_195, component.Size);
        Assert.Matches("/resolve/[0-9a-f]{40}/", component.Url); // a commit, not "main": the file cannot change under the checksum
        Assert.Empty(package.ConverterIds);
        Assert.False(new Filee.App.Services.EnginePackageState(package, Context().Loc).CanUseOwnCopy); // a data file, not a program to point at
        foreach (var language in new[] { "en", "es", "ko", "zh-CN" })
        {
            var loc = ContextIn(language).Loc;
            Assert.NotEqual($"engines.package.whisper.name", loc["engines.package.whisper.name"]);
            Assert.NotEqual($"engines.package.whisper.description", loc["engines.package.whisper.description"]);
        }
    }

    // ───────── Renders ─────────

    private sealed class QuickPanelThatWaits(PanelContext context) : TranscribePanel(context)
    {
        public override bool AutoRun => false;
    }

    [AvaloniaFact]
    public async Task The_quick_window_and_the_options_window_render_without_clipping()
    {
        var clip = Path.Combine(_root, "entrevista.mp4");
        File.WriteAllBytes(clip, [0]);
        // As it looks mid-run: what it is doing and which model runs where, both lines at their longest (the large model on the
        // processor also tells that the small one is faster there).
        const string running = "Transcribiendo «entrevista-con-un-nombre-muy-largo-del-director.mp4»… (2 de 3)";
        const string device = "Se usará el modelo grande en el procesador (lento). Sin tarjeta gráfica, el modelo pequeño de Ajustes → Motores va más rápido.";
        Render(new QuickPanelThatWaits(Context(clip)) { Phase = running, DeviceText = device }, "transcribe");
        Render(new SubtitlesPanel(Context(clip)) { Phase = running, DeviceText = device, HasGpu = true }, "subtitles");

        // A real video offers burning the subtitles in, and how big: the window must hold those too.
        var video = await MakeMediaAsync(Path.Combine(_root, "paseo.mp4"), "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=1",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-shortest", "-pix_fmt", "yuv420p");
        if (video is null)
            return;
        var panel = new SubtitlesPanel(Context(video)) { Phase = running, DeviceText = device, HasGpu = true };
        await WaitAsync(() => panel.HasVideo);
        panel.Burn = true;
        Render(panel, "subtitles-burn");
    }

    // ───────── With a real voice ─────────

    /// <summary>A WAV of Windows' own voice saying <paramref name="text"/> in <paramref name="culture"/>; null when this PC has no such voice.</summary>
    private string? Speak(string name, string culture, string text)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return SpeakOnWindows(Path.Combine(_root, name), culture, text);
    }

    [SupportedOSPlatform("windows")]
    private static string? SpeakOnWindows(string path, string culture, string text)
    {
        using var synthesizer = new SpeechSynthesizer();
        var voice = synthesizer.GetInstalledVoices(new CultureInfo(culture)).FirstOrDefault(v => v.Enabled);
        if (voice is null)
            return null;
        synthesizer.SelectVoice(voice.VoiceInfo.Name);
        synthesizer.SetOutputToWaveFile(path);
        synthesizer.Speak(text);
        return path;
    }

    /// <summary>The voice, or a skipped test when no model (either of the two), FFmpeg or the voice is missing.</summary>
    private string Voice(string name, string culture, string text)
    {
        Assert.SkipWhen(!WhisperModel.Installed().Any, "No Whisper model is installed (Settings → Engines)");
        Assert.SkipWhen(MediaTools.Locate() is null, "FFmpeg is not installed next to the tests");
        var path = Speak(name, culture, text);
        Assert.SkipWhen(path is null, $"There is no {culture} voice on this PC");
        return path!;
    }

    private static string Read(string path) => File.ReadAllText(path, System.Text.Encoding.UTF8);

    [AvaloniaFact]
    public async Task One_click_writes_the_text_and_the_subtitles_beside_the_original()
    {
        var voice = Voice("charla.wav", "es-ES", SpanishText);
        var before = File.ReadAllBytes(voice);
        var panel = new TranscribePanel(Context(voice));

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal([Path.Combine(_root, "charla.txt"), Path.Combine(_root, "charla.srt")], panel.Outputs);
        var text = Read(panel.Outputs[0]);
        Assert.Contains("Madrid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("familia", text, StringComparison.OrdinalIgnoreCase);
        var cues = Subtitles.Parse(Read(panel.Outputs[1]));
        Assert.NotEmpty(cues);
        Assert.All(cues, cue => Assert.True(cue.End > cue.Start));
        Assert.InRange(cues[^1].End.TotalSeconds, 5, 14); // the speech lasts about 9 s
        Assert.Equal(before, File.ReadAllBytes(voice)); // the original is never touched

        var again = new TranscribePanel(Context(voice));
        await again.RunAsync();
        Assert.Equal([Path.Combine(_root, "charla 2.txt"), Path.Combine(_root, "charla 2.srt")], again.Outputs); // nothing is overwritten
    }

    [AvaloniaFact]
    public async Task The_options_panel_saves_only_the_formats_asked_for_in_the_language_asked_for()
    {
        var voice = Voice("talk.wav", "en-US", EnglishText);
        var panel = new SubtitlesPanel(Context(voice))
        {
            SelectedLanguage = new WhisperLanguage("en", "English"),
            SaveText = false,
            SaveSrt = false,
            SaveVtt = true,
        };
        Assert.True(panel.CanApply);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal([Path.Combine(_root, "talk.vtt")], panel.Outputs);
        var vtt = Read(panel.Outputs[0]);
        Assert.StartsWith("\uFEFFWEBVTT", vtt);
        Assert.Contains("Madrid", vtt, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "talk.srt")));
    }

    [AvaloniaFact]
    public void Nothing_to_save_means_nothing_to_apply()
    {
        var clip = Path.Combine(_root, "x.wav");
        File.WriteAllBytes(clip, [0]);
        var panel = new SubtitlesPanel(Context(clip)) { SaveText = false, SaveSrt = false, SaveVtt = false };

        Assert.False(panel.CanApply);
        panel.SaveSrt = true;
        Assert.True(panel.CanApply == WhisperModel.Installed().Any);
    }

    [AvaloniaFact]
    public async Task The_subtitles_can_be_burned_into_the_picture_of_a_video()
    {
        var voice = Voice("voz.wav", "es-ES", SpanishText);
        var video = await MakeMediaAsync(Path.Combine(_root, "paseo.mp4"), "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=10:duration=12",
            "-i", voice, "-shortest", "-pix_fmt", "yuv420p");
        var panel = new SubtitlesPanel(Context(video!)) { SaveText = false, SaveSrt = false, Burn = true, Size = SubtitleSize.Large };
        await WaitAsync(() => panel.HasVideo);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var burned = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, $"paseo {Context().Loc["wheel.subtitles.suffix"]}.mp4"), burned);
        var media = MediaTools.Locate()!;
        var original = await media.ProbeAsync(video!);
        var result = await media.ProbeAsync(burned);
        Assert.True(result.HasVideo && result.HasAudio);
        Assert.InRange(result.Duration, original.Duration - 0.5, original.Duration + 0.5);
        Assert.Equal((original.Width, original.Height), (result.Width, result.Height));

        // At 2 s somebody is speaking: the lower third of the picture changed, the upper two thirds did not.
        var (low, high) = await DifferenceAsync(media, video!, burned, seconds: 2);
        Assert.True(low > 0.004 && low > high * 3, $"the bottom third differs by {low:0.0000}, the rest by {high:0.0000}");
    }

    /// <summary>How much two frames differ (0..1) in the lower third and in the upper two thirds.</summary>
    private async Task<(double Low, double High)> DifferenceAsync(MediaTools media, string a, string b, double seconds)
    {
        var (pa, pb) = (Path.Combine(_root, "a.png"), Path.Combine(_root, "b.png"));
        await media.RunAsync(["-ss", MediaTools.Seconds(seconds), "-i", a, "-frames:v", "1", pa]);
        await media.RunAsync(["-ss", MediaTools.Seconds(seconds), "-i", b, "-frames:v", "1", pb]);
        using var first = new MagickImage(pa);
        using var second = new MagickImage(pb);
        double Part(uint top, uint height)
        {
            using var x = first.CloneArea(0, (int)top, first.Width, height);
            using var y = second.CloneArea(0, (int)top, second.Width, height);
            return x.Compare(y, ErrorMetric.MeanAbsolute);
        }
        var third = first.Height / 3;
        // The top left corner is testsrc2's frame counter, which differs whatever is done: look at the middle only.
        return (Part(third * 2, first.Height - third * 2), Part(third / 2, third));
    }

    [AvaloniaFact]
    public async Task A_silent_file_says_so_and_writes_nothing()
    {
        // Over silence the model writes "Thank you." across the whole file; the tool must say there is no speech instead.
        Assert.SkipWhen(!WhisperModel.Installed().Any, "No Whisper model is installed (Settings → Engines)");
        var silence = await MakeMediaAsync(Path.Combine(_root, "silencio.wav"), "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "6");
        Assert.SkipWhen(silence is null, "FFmpeg is not installed next to the tests");
        var panel = new TranscribePanel(Context(silence!));

        await panel.RunAsync();

        Assert.True(panel.HasError);
        Assert.Contains("silencio.wav", panel.Status);
        Assert.Equal(["silencio.wav"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [AvaloniaFact]
    public async Task With_several_files_one_without_audio_fails_them_all_and_leaves_nothing_behind()
    {
        var voice = Voice("bien.wav", "es-ES", SpanishText);
        var mute = await MakeMediaAsync(Path.Combine(_root, "mudo.mp4"), "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=2", "-pix_fmt", "yuv420p");
        var panel = new TranscribePanel(Context(voice, mute!));

        await panel.RunAsync();

        Assert.True(panel.HasError);
        Assert.Contains("mudo.mp4", panel.Status);
        Assert.Equal(["bien.wav", "mudo.mp4"], Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal)); // what was written for the first is gone
    }

    [AvaloniaFact]
    public async Task Closing_the_window_while_it_works_stops_it_and_leaves_nothing_behind()
    {
        var voice = Voice("larga.wav", "es-ES", string.Join(' ', Enumerable.Repeat(SpanishText, 12)));
        var panel = new TranscribePanel(Context(voice));

        var running = panel.RunAsync();
        await Task.Delay(1500); // the model is loading or listening
        panel.Cancel();
        await running;

        Assert.False(panel.IsDone);
        Assert.Equal(Context().Loc["panel.cancelled"], panel.Status);
        Assert.Equal(["larga.wav"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [AvaloniaFact]
    public async Task A_model_kept_in_a_folder_with_accents_still_loads()
    {
        // A Windows user called José or Nuño has the letter in the profile folder the model is kept in; whisper.cpp cannot open it there.
        var voice = Voice("acentos.wav", "es-ES", SpanishText);
        var folder = Directory.CreateDirectory(Path.Combine(_root, "ñandú Rodríguez")).FullName;
        var model = Path.Combine(folder, "modelo.bin");
        File.Copy(WhisperModel.Installed().For(dedicatedGpu: true)!.Path, model);
        var media = MediaTools.Locate()!;
        var engine = WhisperEngine.For(media, new WhisperModels(model, null));

        var cues = await engine.TranscribeAsync(voice, (await media.ProbeAsync(voice)).Duration, "es", useGpu: true, null, default);

        Assert.Contains("Madrid", string.Join(' ', cues.Select(cue => cue.Text)), StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task The_engine_says_where_it_runs_and_the_progress_follows_the_written_subtitles()
    {
        var voice = Voice("progreso.wav", "es-ES", string.Join(' ', Enumerable.Repeat(SpanishText, 18)));
        var engine = WhisperEngine.Locate(MediaTools.Locate())!;

        var device = await engine.DeviceAsync();
        Assert.False(string.IsNullOrWhiteSpace(device.Name));

        var media = MediaTools.Locate()!;
        var probe = await media.ProbeAsync(voice);
        var reports = new List<double>();
        var cues = await engine.TranscribeAsync(voice, probe.Duration, "es", useGpu: true, new SyncProgress(reports.Add), default);

        Assert.True(cues.Count >= 10, $"{cues.Count} cues for {probe.Duration:0} s of speech");
        Assert.True(cues[^1].End.TotalSeconds > probe.Duration * 0.8, "the subtitles reach the end of the speech");
        Assert.All(reports, report => Assert.InRange(report, 0, 0.99));
        Assert.Equal(reports.Order(), reports); // never backwards
    }

    [AvaloniaFact]
    public async Task The_small_model_listens_to_a_voice_on_the_processor_when_the_card_is_not_used()
    {
        // The second package: Whisper small, for a PC without a dedicated graphics card. Only the processor is used here.
        var installed = WhisperModel.Installed();
        Assert.SkipWhen(installed.Small is null, "The small Whisper model is not installed (Settings → Engines → Whisper small)");
        var voice = Voice("pequeno.wav", "es-ES", SpanishText);
        var media = MediaTools.Locate()!;
        var log = new CaptureLog();
        var engine = WhisperEngine.For(media, installed, log);

        // With both models installed the processor still gets the small one: the line in the log says which one ran.
        var cues = await engine.TranscribeAsync(voice, (await media.ProbeAsync(voice)).Duration, "es", useGpu: false, null, default);

        var text = string.Join(' ', cues.Select(cue => cue.Text));
        Assert.Contains("Madrid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("familia", text, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(cues[^1].End.TotalSeconds, 5, 14); // the speech lasts about 9 s
        Assert.Contains(log.Lines, line => line.Contains("Small model on CPU", StringComparison.Ordinal));
        Assert.Equal(installed.Small, engine.ModelFor(WhisperDevice.Processor).Path);
    }

    [AvaloniaFact]
    public async Task The_options_panel_without_the_card_saves_the_subtitles_with_the_model_of_the_processor()
    {
        var installed = WhisperModel.Installed();
        Assert.SkipWhen(installed.Small is null, "The small Whisper model is not installed (Settings → Engines → Whisper small)");
        var voice = Voice("sin-tarjeta.wav", "es-ES", SpanishText);
        var panel = new SubtitlesPanel(Context(voice)) { UseGpu = false, SaveText = true, SaveSrt = false, SaveVtt = false };

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal([Path.Combine(_root, "sin-tarjeta.txt")], panel.Outputs);
        Assert.Contains("Madrid", Read(panel.Outputs[0]), StringComparison.OrdinalIgnoreCase);
        panel.Dispose();
    }

    /// <summary>Keeps what is logged, formatted, so a test can look for what the engine says it did.</summary>
    private sealed class CaptureLog : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
