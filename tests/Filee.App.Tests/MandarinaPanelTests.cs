// Mandarina: the tool panels. The frame (ToolPanelWindow), the registry and the wheel's choice of tools are checked
// here; the first panel, Trim audio, runs against the real FFmpeg (engines\ffmpeg next to the test binaries) and
// writes mandarina-panel-*.png to the screenshot folder for review. Each later panel has its own Mandarina*Tests file.

using Avalonia.Headless.XUnit;
using Filee.App.Mandarina;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;
using Filee.Core.Settings;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

/// <summary>
/// What the tools wheel offers per kind of file (docs/mandarina/ESPECIFICACION.md), minus the panels not built yet, and
/// Mandarina's own transcription petals after them (audio and video, one file or several).
/// </summary>
internal static class WheelTable
{
    private static readonly HashSet<string> Presets = ["normalizeAudio", "muteVideo", "createPDF", "splitPDF", "mergePDF", "extractArchive"];

    public static readonly IReadOnlyDictionary<string, string[]> One = new Dictionary<string, string[]>
    {
        ["photo"] = ["compress", "removeMetadata", "editImage", "annotateImage", "frameImage", "cropImage", "redactImage"],
        ["audio"] = ["compress", "removeMetadata", "normalizeAudio", "audioToVideo", "trimAudio", "audioChannels", "redactAudio", "transcribe", "subtitles"],
        ["video"] = ["compress", "removeMetadata", "muteVideo", "trimVideo", "cropVideo", "changeVideoSpeed", "videoSnapshots", "splitVideo", "redactVideo", "transcribe", "subtitles"],
        ["pdf"] = ["compress", "removeMetadata", "splitPDF", "organizePDF"],
        ["gif"] = ["removeMetadata"],
        ["archive"] = ["extractArchive"],
    };

    public static readonly IReadOnlyDictionary<string, string[]> Many = new Dictionary<string, string[]>
    {
        ["photo"] = ["compress", "removeMetadata", "cropImage", "createPDF", "createCollage"],
        ["audio"] = ["compress", "removeMetadata", "normalizeAudio", "transcribe", "subtitles"],
        ["video"] = ["compress", "removeMetadata", "muteVideo", "joinVideos", "transcribe", "subtitles"],
        ["pdf"] = ["compress", "removeMetadata", "splitPDF", "mergePDF"],
        ["gif"] = ["removeMetadata"],
        ["archive"] = ["extractArchive"],
    };

    /// <summary>
    /// The ids the wheel shows for <paramref name="kind"/> in this build, leaving out the tools whose panel
    /// is not there yet. Compress and metadata also run as presets, except on PDFs.
    /// </summary>
    public static string[] Expected(string kind, bool many) =>
        (many ? Many : One)[kind]
            .Where(id => Presets.Contains(id) || PanelRegistry.Has(id) || (id is "compress" or "removeMetadata" && kind != "pdf"))
            .ToArray();
}

public sealed class MandarinaPanelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-panels").FullName;

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

    private Task<string?> MakeAudioAsync(string name, params string[] ffmpegArguments) =>
        MakeMediaAsync(Path.Combine(_root, name), ffmpegArguments);

    // ───────── The wheel and the registry ─────────

    [AvaloniaFact]
    public void The_wheel_offers_the_expected_tools_for_the_panels_that_exist()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        string[] Ids(params string[] files) => WheelCatalog.ToolsFor(files, loc).Select(t => t.Id).ToArray();

        Assert.Equal(WheelTable.Expected("photo", many: false), Ids(@"C:\a.jpg"));
        Assert.Equal(WheelTable.Expected("photo", many: true), Ids(@"C:\a.jpg", @"C:\b.png"));
        Assert.Equal(WheelTable.Expected("audio", many: false), Ids(@"C:\a.mp3"));
        Assert.Equal(WheelTable.Expected("audio", many: true), Ids(@"C:\a.mp3", @"C:\b.flac"));
        Assert.Equal(WheelTable.Expected("video", many: false), Ids(@"C:\a.mp4"));
        Assert.Equal(WheelTable.Expected("video", many: true), Ids(@"C:\a.mov", @"C:\b.mkv"));
        Assert.Equal(WheelTable.Expected("pdf", many: false), Ids(@"C:\a.pdf"));
        Assert.Equal(WheelTable.Expected("pdf", many: true), Ids(@"C:\a.pdf", @"C:\b.pdf"));
        Assert.Equal(WheelTable.Expected("gif", many: false), Ids(@"C:\a.gif"));
        Assert.Equal(WheelTable.Expected("archive", many: false), Ids(@"C:\a.zip"));

        // A petal that opens a panel says so; the others carry the preset that runs them.
        foreach (var entry in WheelCatalog.ToolsFor([@"C:\a.mp3"], loc))
            Assert.Equal(PanelRegistry.Has(entry.Id), entry.OpensPanel);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Every_panel_has_a_title_and_an_output_suffix_in_every_language(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        foreach (var id in PanelRegistry.Ids)
        {
            Assert.NotEqual($"wheel.{id}", loc[$"wheel.{id}"]);
            Assert.NotEqual($"wheel.{id}.suffix", loc[$"wheel.{id}.suffix"]);
        }
    }

    [AvaloniaFact]
    public void A_petal_that_needs_ffmpeg_is_greyed_out_without_it()
    {
        TestServices.EnsureInitialized("es");
        var loc = AppHost.Get<ILocalizer>();
        var reason = PanelRegistry.Unavailable("trimAudio", loc);
        Assert.Equal(MediaTools.Locate() is null, reason is not null);
        Assert.Null(PanelRegistry.Unavailable("noSuchTool", loc));
    }

    [AvaloniaFact]
    public async Task Choosing_a_panel_tool_on_the_wheel_opens_its_panel_and_closes_the_wheel()
    {
        var file = await MakeAudioAsync("tono.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=2");
        if (file is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var radial = AppHost.Get<RadialController>();
        var host = AppHost.Get<PanelHost>();
        var wheel = AppHost.Get<RadialViewModel>();
        try
        {
            radial.ShowForFiles(300, 300, [file], WheelMode.Tools);
            var index = wheel.Entries.ToList().FindIndex(entry => entry.Id == "trimAudio");
            Assert.True(index >= 0);
            Assert.True(wheel.Items[index].IsEnabled);

            radial.Invoke(index, -1, [file]);

            var window = Assert.Single(host.OpenWindows);
            Assert.Equal("trimAudio", window.Panel!.Id);
            Assert.True(window.IsVisible);
            Assert.False(radial.IsVisible);
            window.Close();
            Assert.Empty(host.OpenWindows);
        }
        finally
        {
            radial.Close(animate: false);
            wheel.ResetForDrag(WheelMode.Convert); // the view model is shared: leave it on the conversion wheel
        }
    }

    /// <summary>A tool that fails the way nobody planned for (not a <see cref="PanelException"/>).</summary>
    [ToolPanel("testFail")]
    private sealed class ThrowingPanel(PanelContext context) : ToolPanel(context)
    {
        public override Avalonia.Controls.Control CreateView() => new Avalonia.Controls.Border();

        public override Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel) =>
            throw new InvalidDataException("boom");
    }

    [AvaloniaFact]
    public async Task An_unexpected_exception_in_a_tool_ends_in_the_footer_and_not_in_a_crash()
    {
        var panel = new ThrowingPanel(Context());

        await panel.RunAsync(); // the Apply button awaits this from an async void handler: a throw here would end the app

        Assert.True(panel.HasError);
        Assert.Contains("boom", panel.Status);
        Assert.False(panel.IsBusy);
        Assert.False(panel.IsDone);
        Assert.True(panel.CanRun); // and the user can try again
    }

    /// <summary>Opens a photo panel in its window, closes it, and returns weak references to what must then be free to go.</summary>
    private async Task<(WeakReference Window, WeakReference Panel, WeakReference Picture)> OpenAndCloseAsync(string photo)
    {
        var panel = new EditImagePanel(Context(photo));
        await WaitAsync(() => !panel.IsLoading);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var picture = new WeakReference(panel.Shown);
        window.Close();
        Pump();
        return (new WeakReference(window), new WeakReference(panel), picture);
    }

    [AvaloniaFact]
    public async Task A_closed_panel_and_its_pictures_can_be_collected()
    {
        var photo = MakePhoto(Path.Combine(_root, "grande.jpg"), 1600, 1067);
        var (window, panel, picture) = await OpenAndCloseAsync(photo);

        for (var round = 0; round < 6 && (window.IsAlive || panel.IsAlive || picture.IsAlive); round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(300); // the timers the window left behind run out
            Pump();
        }

        Assert.False(window.IsAlive, "something still holds the closed window");
        Assert.False(panel.IsAlive, "something still holds the closed panel");
        Assert.False(picture.IsAlive, "something still holds the panel's preview picture");
    }

    [AvaloniaFact]
    public async Task ContentRoom_tells_a_window_that_is_too_small_from_one_that_fits()
    {
        // No FFmpeg needed: the panel shows its load error where the waveform would be, the layout is the same.
        var panel = new TrimAudioPanel(Context(Path.Combine(_root, "missing.mp3")));
        await WaitAsync(() => !panel.IsLoading);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var (needed, available) = window.ContentRoom();
        Assert.True(needed <= available + 1, $"needs {needed:0}, has {available:0}");

        window.Height = 330;
        Pump();
        (needed, available) = window.ContentRoom();
        Assert.True(needed > available + 1, $"a 330 px window should clip the options: needs {needed:0}, has {available:0}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_slider_row_is_compact_and_follows_its_value()
    {
        TestServices.EnsureInitialized("es");
        var row = new Filee.App.Mandarina.Panels.SliderRow { Label = "Exposición", Minimum = -2, Maximum = 2, Step = 0.05, Value = 0.5, ValueFormat = "0.00" };
        var window = new Avalonia.Controls.Window { Width = 420, Height = 120, Content = new Avalonia.Controls.StackPanel { Children = { row } } };
        window.Show();
        Pump();
        Assert.InRange(row.Bounds.Height, 20, 34); // Fluent's own template would make it 50
        row.Value = 1.25;
        Pump();
        Assert.Equal(1.25, row.Value);
        row.Value = row.DefaultValue;
        Assert.Equal(0, row.Value);
        window.Close();
    }

    // ───────── Trim audio ─────────

    [AvaloniaFact]
    public async Task The_trim_panel_renders_with_its_waveform()
    {
        // 12 s of a swelling tone, like speech: loud, quiet, loud.
        var file = await MakeAudioAsync("entrevista.mp3", "-f", "lavfi", "-i",
            "aevalsrc='sin(2*PI*220*t)*(0.05+0.95*abs(sin(2*PI*0.35*t)))*(0.4+0.6*abs(sin(2*PI*1.7*t)))':d=12:s=22050");
        if (file is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var panel = new TrimAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        Assert.InRange(panel.Duration, 11.9, 12.2);
        Assert.NotNull(panel.Peaks);
        panel.Start = 2.4;
        panel.End = 9.1;
        panel.Position = 5.0;

        Render(panel, "trimaudio");
        Assert.Equal("Recortar", panel.Title);
    }

    [AvaloniaFact]
    public async Task Trim_audio_saves_the_range_as_a_new_file_beside_the_original()
    {
        var file = await MakeAudioAsync("tono.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=4");
        if (file is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = new TrimAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        Assert.True(panel.CanApply);
        panel.Start = 1;
        panel.End = 3;

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "tono recortado.wav"), output);
        Assert.InRange((await media.ProbeAsync(output)).Duration, 1.95, 2.05);
        Assert.InRange((await media.ProbeAsync(file)).Duration, 3.95, 4.05); // the original is untouched

        // Another run does not overwrite: "tono recortado 2.wav".
        var again = new TrimAudioPanel(Context(file));
        await WaitAsync(() => !again.IsLoading);
        again.Start = 0;
        again.End = 1;
        await again.RunAsync();
        Assert.Equal(Path.Combine(_root, "tono recortado 2.wav"), Assert.Single(again.Outputs));
    }

    [AvaloniaFact]
    public async Task Trim_audio_keeps_the_tags_and_the_format_of_a_compressed_file()
    {
        var file = await MakeAudioAsync("cancion.mp3", "-f", "lavfi", "-i", "sine=frequency=330:duration=3", "-metadata", "artist=Autor01");
        if (file is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var media = MediaTools.Locate()!;
        var panel = new TrimAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);
        panel.Start = 0.5;
        panel.End = 2.5;
        await panel.RunAsync();

        var output = Assert.Single(panel.Outputs);
        Assert.Equal("cancion recortado.mp3", Path.GetFileName(output));
        var probe = await media.ProbeAsync(output);
        Assert.Equal("mp3", probe.AudioCodec);
        Assert.Equal("Autor01", probe.Tags["artist"]);
        Assert.InRange(probe.Duration, 1.9, 2.2);
    }

    [AvaloniaFact]
    public async Task Trim_silence_moves_the_handles_to_the_sound()
    {
        // 1 s of silence, 1 s of tone, 1 s of silence.
        var file = await MakeAudioAsync("hueco.wav", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono:d=1", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=1", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono:d=1",
            "-filter_complex", "[0][1][2]concat=n=3:v=0:a=1");
        if (file is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var panel = new TrimAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);

        panel.TrimSilence();

        Assert.InRange(panel.Start, 0.7, 1.0);   // never cuts into the tone
        Assert.InRange(panel.End, 2.0, 2.3);
        panel.Reset();
        Assert.Equal(0, panel.Start);
        Assert.Equal(panel.Duration, panel.End);
    }

    [AvaloniaFact]
    public async Task Typed_times_are_clamped_and_read_back()
    {
        var file = await MakeAudioAsync("tono.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=4");
        if (file is null)
        {
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
            return;
        }
        var panel = new TrimAudioPanel(Context(file));
        await WaitAsync(() => !panel.IsLoading);

        panel.CommitStart("0:01.5");
        panel.CommitEnd("0:03");
        Assert.Equal(1.5, panel.Start, 3);
        Assert.Equal(3, panel.End, 3);
        panel.CommitEnd("99");                       // beyond the end of the file
        Assert.Equal(panel.Duration, panel.End, 3);
        panel.CommitStart("not a time");             // unreadable: the old value stays
        Assert.Equal(1.5, panel.Start, 3);
        Assert.Equal("0:01.50", panel.StartText);
    }

    [Theory]
    [InlineData("0:05.30", 5.3)]
    [InlineData("1:05", 65)]
    [InlineData("1:02:03.4", 3723.4)]
    [InlineData("12,5", 12.5)]
    [InlineData("7", 7)]
    public void Times_are_read_as_seconds(string text, double seconds)
    {
        Assert.True(PanelFormat.TryParseTime(text, out var parsed));
        Assert.Equal(seconds, parsed, 3);
    }

    [Theory]
    [InlineData(5.3, "0:05.30")]
    [InlineData(65, "1:05.00")]
    [InlineData(3723.4, "1:02:03.40")]
    public void Times_are_written_with_hundredths(double seconds, string text) => Assert.Equal(text, PanelFormat.Time(seconds));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    public void Unreadable_times_are_refused(string text) => Assert.False(PanelFormat.TryParseTime(text, out _));
}
