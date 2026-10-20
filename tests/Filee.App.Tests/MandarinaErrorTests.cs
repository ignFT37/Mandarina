// Mandarina: what the footer of a tool panel says when FFmpeg, a compressor or the file system fails. The causes a person can act
// on (damaged file, disk full, codec not supported…) come out in the app's language; whatever is not recognised stays as the
// engine said it; and the engine's own words always reach the log. FFmpeg is the real one (engines\ffmpeg next to the tests).

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaErrorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-errors").FullName;

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

    /// <summary>Keeps what is logged, formatted, so a test can look for the original words.</summary>
    private sealed class CaptureLog : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static ILocalizer Loc(string language)
    {
        TestServices.EnsureInitialized(language);
        return AppHost.Get<ILocalizer>();
    }

    private static PanelContext Logged(string language, CaptureLog log, params string[] files)
    {
        var loc = Loc(language);
        return new PanelContext(files, loc, AppHost.Get<IPlatformServices>()) { Log = log };
    }

    private static MediaTools Media()
    {
        var media = MediaTools.Locate();
        if (media is null)
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
        return media!;
    }

    private string Junk(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new Random(7).GetItems<byte>(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 4000));
        return path;
    }

    // ───────── FFmpeg's own words, recognised ─────────

    [AvaloniaFact]
    public async Task A_file_that_is_not_media_is_said_to_be_damaged()
    {
        var ex = await Assert.ThrowsAsync<PanelException>(() => Media().ProbeAsync(Junk("roto.mp4"), TestContext.Current.CancellationToken));

        Assert.Equal("panel.error.damaged", ex.Key);
        Assert.Contains("Invalid data", ex.Message);              // the short English stays: the tools that retry read it
        Assert.Contains("Invalid data", ex.Detail);               // and the log gets FFmpeg's words
        var es = Loc("es");
        Assert.Equal(es["panel.error.damaged"], PanelErrors.Describe(ex, es));
        Assert.DoesNotContain("Invalid data", PanelErrors.Describe(ex, es));
    }

    [AvaloniaFact]
    public async Task A_cut_off_video_is_said_to_be_damaged()
    {
        var whole = await MakeMediaAsync(Path.Combine(_root, "entero.mp4"), "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=3", "-pix_fmt", "yuv420p");
        if (whole is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var cut = Path.Combine(_root, "cortado.mp4");
        var bytes = await File.ReadAllBytesAsync(whole, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(cut, bytes[..(bytes.Length / 2)], TestContext.Current.CancellationToken); // the "moov" index is at the end

        var ex = await Assert.ThrowsAsync<PanelException>(() => Media().ProbeAsync(cut, TestContext.Current.CancellationToken));

        Assert.Equal("panel.error.damaged", ex.Key);
    }

    [AvaloniaFact]
    public async Task A_missing_file_is_said_to_be_gone()
    {
        var ex = await Assert.ThrowsAsync<PanelException>(() => Media().ProbeAsync(Path.Combine(_root, "nada.mp4"), TestContext.Current.CancellationToken));

        Assert.Equal("panel.error.not_found", ex.Key);
        Assert.Contains("No such file", ex.Detail);
    }

    [AvaloniaFact]
    public async Task Asking_for_a_stream_the_file_lacks_is_said_to_have_nothing_to_use()
    {
        var silent = await MakeMediaAsync(Path.Combine(_root, "mudo.mp4"), "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=2", "-pix_fmt", "yuv420p");
        if (silent is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }

        var ex = await Assert.ThrowsAsync<PanelException>(() =>
            Media().RunAsync(["-i", silent, "-map", "0:a", "-c", "copy", Path.Combine(_root, "audio.m4a")], cancel: TestContext.Current.CancellationToken));

        Assert.Equal("panel.error.no_stream", ex.Key);
    }

    [AvaloniaFact]
    public async Task An_encoder_this_build_lacks_is_said_to_be_a_codec_that_is_not_supported()
    {
        var clip = await MakeMediaAsync(Path.Combine(_root, "clip.mp4"), "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=2", "-pix_fmt", "yuv420p");
        if (clip is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }

        var ex = await Assert.ThrowsAsync<PanelException>(() =>
            Media().RunAsync(["-i", clip, "-c:v", "no_such_encoder", Path.Combine(_root, "fuera.mp4")], cancel: TestContext.Current.CancellationToken));

        Assert.Equal("panel.error.codec", ex.Key);
    }

    [Theory]
    [InlineData("av_interleaved_write_frame(): No space left on device", "panel.error.no_space")]
    [InlineData("Error writing trailer of out.mp4: No space left on device", "panel.error.no_space")]
    [InlineData("out.mp4: Permission denied", "panel.error.no_permission")]
    [InlineData("Could not open file : Cannot allocate memory", "panel.error.out_of_memory")]
    [InlineData("Decoder (codec hevc) not found for input stream #0:0", "panel.error.codec")]
    [InlineData("Could not write header for output file #0 (incorrect codec parameters ?): Invalid argument", null)]
    [InlineData("Unrecognized option 'whisper'.", null)]
    [InlineData("", null)]
    public void FFmpegs_error_text_points_to_a_cause_or_to_none(string text, string? key) =>
        Assert.Equal(key, PanelErrors.Recognize(text));

    [Fact]
    public void The_log_keeps_the_end_of_a_long_error_text()
    {
        var text = string.Concat(Enumerable.Repeat("line of noise\n", 400)) + "No space left on device";

        var ex = PanelErrors.FromEngine(text, "No space left on device", "FFmpeg failed.");

        Assert.Equal("panel.error.no_space", ex.Key);
        Assert.True(ex.Detail!.Length <= 2000);
        Assert.EndsWith("No space left on device", ex.Detail);
    }

    // ───────── The file system's exceptions ─────────

    [AvaloniaFact]
    public void The_file_systems_own_exceptions_are_recognised()
    {
        var es = Loc("es");

        Assert.Equal(es["panel.error.no_space"], PanelErrors.Describe(new IOException("disk", unchecked((int)0x80070070)), es));
        Assert.Equal(es["panel.error.no_space"], PanelErrors.Describe(new IOException("disk", unchecked((int)0x80070027)), es));
        Assert.Equal(es["panel.error.no_permission"], PanelErrors.Describe(new IOException("busy", unchecked((int)0x80070020)), es));
        Assert.Equal(es["panel.error.no_permission"], PanelErrors.Describe(new UnauthorizedAccessException("denied"), es));
        Assert.Equal(es["panel.error.not_found"], PanelErrors.Describe(new FileNotFoundException("gone"), es));
        Assert.Equal(es["panel.error.not_found"], PanelErrors.Describe(new DirectoryNotFoundException("gone"), es));
    }

    [AvaloniaFact]
    public void A_cause_wrapped_in_a_message_is_still_found_and_an_unknown_one_stays_as_it_is()
    {
        var es = Loc("es");
        var wrapped = new PanelException("foto.jpg: There is not enough space on the disk.", new IOException("disk", unchecked((int)0x80070070)));
        Assert.Equal(es["panel.error.no_space"], PanelErrors.Describe(wrapped, es));

        Assert.Equal("Unrecognized option 'x'.", PanelErrors.Describe(new PanelException("Unrecognized option 'x'."), es));
        Assert.Equal("boom", PanelErrors.Describe(new InvalidDataException("boom"), es));
    }

    [AvaloniaFact]
    public void A_generic_cause_names_the_file_the_tool_was_working_on_and_a_tools_own_sentence_does_not()
    {
        var es = Loc("es");

        var generic = new PanelException("Invalid data") { Key = "panel.error.damaged", Subject = "viaje.mp4" };
        Assert.Equal("«viaje.mp4»: " + es["panel.error.damaged"], PanelErrors.Describe(generic, es));

        var own = new PanelException("x") { Key = "panel.compress.error.failed", Args = ["viaje.mp4"], Subject = "viaje.mp4" };
        Assert.Equal(es.Format("panel.compress.error.failed", "viaje.mp4"), PanelErrors.Describe(own, es));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Every_cause_and_compressor_message_exists_in_every_language(string language)
    {
        var loc = Loc(language);
        string[] keys =
        [
            "panel.error.subject", "panel.error.damaged", "panel.error.no_space", "panel.error.codec", "panel.error.no_permission",
            "panel.error.not_found", "panel.error.no_stream", "panel.error.out_of_memory", "panel.compress.error.no_audio",
            "panel.compress.error.no_video", "panel.compress.error.no_target", "panel.compress.error.no_length",
            "panel.compress.error.unsupported", "panel.compress.error.failed", "panel.compress.error.unsafe", "panel.compress.error.exists",
            "panel.compress.unreadable",
        ];
        foreach (var key in keys)
            Assert.NotEqual(key, loc[key]);
        // The placeholders survive the translation.
        Assert.Contains("{0}", loc["panel.compress.error.failed"]);
        Assert.Contains("{1}", loc["panel.error.subject"]);
    }

    // ───────── In the panels ─────────

    [AvaloniaFact]
    public async Task A_file_that_does_not_load_shows_the_cause_in_the_users_language_and_logs_the_original()
    {
        var log = new CaptureLog();
        var panel = new TrimAudioPanel(Logged("es", log, Junk("roto.mp3")));
        await WaitAsync(() => !panel.IsLoading);
        var es = Loc("es");

        Assert.Equal(es["panel.error.damaged"], panel.LoadError);
        Assert.Contains(log.Lines, line => line.StartsWith("Warning") && (line.Contains("Invalid data") || line.Contains("Header missing")));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task Compressing_a_damaged_video_says_which_file_and_why_and_logs_ffmpegs_words(string language)
    {
        Media();
        var file = Path.Combine(_root, "roto.mp4");
        await File.WriteAllBytesAsync(file, new byte[200_000], TestContext.Current.CancellationToken);
        var log = new CaptureLog();
        var panel = new CompressPanel(Logged(language, log, file));
        await WaitAsync(() => !panel.IsLoading);

        await panel.RunAsync();

        var loc = Loc(language);
        Assert.True(panel.HasError, panel.Status);
        Assert.Equal(loc.Format("panel.failed", loc.Format("panel.error.subject", "roto.mp4", loc["panel.error.damaged"])), panel.Status);
        Assert.DoesNotContain("Invalid data", panel.Status);
        Assert.Contains(log.Lines, line => line.Contains("The compress panel failed") && (line.Contains("Invalid data") || line.Contains("moov atom")));
        Assert.Single(Directory.GetFiles(_root)); // nothing half-written is left beside it
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_footer_shows_the_translated_cause_whole_and_clear_of_the_Apply_button(string language)
    {
        Media();
        var file = Path.Combine(_root, "roto.mp4");
        await File.WriteAllBytesAsync(file, new byte[200_000], TestContext.Current.CancellationToken);
        var panel = new CompressPanel(Logged(language, new CaptureLog(), file));
        await WaitAsync(() => !panel.IsLoading);
        await panel.RunAsync();
        Assert.True(panel.HasError, panel.Status);

        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();

        // A long message used to run under the Apply button in the narrower windows: it wraps in the room that is left instead.
        var status = window.GetVisualDescendants().OfType<TextBlock>().First(text => AutomationProperties.GetAutomationId(text) == "PanelStatus");
        var apply = window.GetVisualDescendants().OfType<Button>().First(button => AutomationProperties.GetAutomationId(button) == "PanelApply");
        var statusRight = status.TranslatePoint(new Point(status.Bounds.Width, 0), window)!.Value.X;
        var applyLeft = apply.TranslatePoint(default, window)!.Value.X;
        Assert.True(statusRight <= applyLeft, $"the message ends at {statusRight:0} and the Apply button starts at {applyLeft:0} ({language})");
        Assert.Equal(status.DesiredSize.Height, status.Bounds.Height, 1);                       // nothing cut off at the bottom
        Assert.True(status.Bounds.Height < 90, "it wraps into a few lines, not a column");
        var (needed, available) = window.ContentRoom();
        Assert.True(needed <= available + 1, $"the options need {needed:0} px and have {available:0}");

        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-panel-error-damaged-{language}.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Compressing_a_text_file_as_a_picture_still_says_it_in_the_users_language()
    {
        var file = Path.Combine(_root, "falsa.jpg");
        await File.WriteAllTextAsync(file, "no soy una foto", TestContext.Current.CancellationToken);
        var panel = new CompressPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);

        await panel.RunAsync();

        Assert.True(panel.HasError);
        Assert.Contains("«falsa.jpg» está dañado, protegido", panel.Status);
        Assert.DoesNotContain("damaged", panel.Status);
    }

    [AvaloniaFact]
    public async Task The_compressors_own_messages_carry_a_key_in_every_case()
    {
        var media = Media();
        var audio = await MakeMediaAsync(Path.Combine(_root, "tono.mp3"), "-f", "lavfi", "-i", "sine=frequency=440:duration=2");
        Assert.NotNull(audio);

        // A size of zero is refused before anything runs.
        var zero = await Assert.ThrowsAsync<PanelException>(() => AudioCompressor.CompressAsync(media, audio!, Path.Combine(_root, "t.mp3"),
            new CompressOptions(CompressMode.TargetSize, 70, 0, 0), null, TestContext.Current.CancellationToken));
        Assert.Equal("panel.compress.error.no_target", zero.Key);

        // An output that is taken is not written over.
        await File.WriteAllTextAsync(Path.Combine(_root, "ya existe.mp3"), "x", TestContext.Current.CancellationToken);
        var taken = await Assert.ThrowsAsync<PanelException>(() => AudioCompressor.CompressAsync(media, audio!, Path.Combine(_root, "ya existe.mp3"),
            new CompressOptions(CompressMode.Preset, 70, 0, 0), null, TestContext.Current.CancellationToken));
        Assert.Equal("panel.compress.error.exists", taken.Key);

        // A file with no audio in it.
        var silent = await MakeMediaAsync(Path.Combine(_root, "mudo.mp3.mp4"), "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=5:duration=1", "-pix_fmt", "yuv420p");
        File.Move(silent!, Path.Combine(_root, "mudo.mp3"));
        var none = await Assert.ThrowsAsync<PanelException>(() => AudioCompressor.CompressAsync(media, Path.Combine(_root, "mudo.mp3"), Path.Combine(_root, "m.mp3"),
            new CompressOptions(CompressMode.Preset, 70, 0, 0), null, TestContext.Current.CancellationToken));
        Assert.Equal("panel.compress.error.no_audio", none.Key);
    }
}
