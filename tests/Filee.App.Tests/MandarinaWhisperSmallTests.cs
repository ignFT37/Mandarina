// Mandarina: the second Whisper package, the small model for PCs without a dedicated graphics card. The choice between the two
// models is checked with made-up installs (every mix of installed models and devices, no model files needed); the package with its
// pinned download, its texts in the four languages and its card in Settings → Engines and in the first-run window; and the panels'
// line that says which model runs where. The small model listening to a real voice is in MandarinaTranscribeTests.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Engines.Infrastructure;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaWhisperSmallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-whisper-small").FullName;

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

    // ───────── Which model for which device ─────────

    [Theory]
    [InlineData(true, true, true, WhisperSize.Large)] // both installed, a dedicated card: the better and quick there
    [InlineData(true, true, false, WhisperSize.Small)] // both, the processor: the one that is three to four times as fast
    [InlineData(true, false, true, WhisperSize.Large)] // only the large one: on the card
    [InlineData(true, false, false, WhisperSize.Large)] // only the large one, the processor: slow, but it works
    [InlineData(false, true, true, WhisperSize.Small)] // only the small one: on the card
    [InlineData(false, true, false, WhisperSize.Small)] // only the small one, the processor
    public void A_card_gets_the_large_model_and_the_processor_the_small_one_whichever_is_installed(bool large, bool small, bool card, WhisperSize expected)
    {
        var models = new WhisperModels(large ? @"C:\engines\whisper\large.bin" : null, small ? @"C:\engines\whisper-small\small.bin" : null);

        var chosen = models.For(card);

        Assert.NotNull(chosen);
        Assert.Equal(expected, chosen.Size);
        Assert.Equal(expected == WhisperSize.Large ? models.Large : models.Small, chosen.Path);
        Assert.True(models.Any);
    }

    [Fact]
    public void With_no_model_there_is_nothing_to_choose()
    {
        var none = new WhisperModels(null, null);

        Assert.False(none.Any);
        Assert.Null(none.For(dedicatedGpu: true));
        Assert.Null(none.For(dedicatedGpu: false));
        Assert.Null(none.ForProbe);
        Assert.Throws<ArgumentException>(() => WhisperEngine.For(MediaTools.Locate()!, none));
    }

    [Fact]
    public void The_small_model_is_the_one_started_to_look_for_the_devices_as_it_loads_sooner()
    {
        Assert.Equal("small.bin", new WhisperModels("large.bin", "small.bin").ForProbe);
        Assert.Equal("large.bin", new WhisperModels("large.bin", null).ForProbe);
        Assert.Equal("small.bin", new WhisperModels(null, "small.bin").ForProbe);
    }

    [Fact]
    public void Each_model_is_found_in_the_folder_of_its_own_package_and_by_its_own_file_name()
    {
        var largeFolder = Directory.CreateDirectory(Path.Combine(_root, "whisper")).FullName;
        var smallFolder = Directory.CreateDirectory(Path.Combine(_root, "whisper-small")).FullName;
        var large = Path.Combine(largeFolder, WhisperModel.LargeFile);
        var small = Path.Combine(smallFolder, WhisperModel.SmallFile);
        string? Folders(string name) => name switch { "whisper" => largeFolder, "whisper-small" => smallFolder, _ => null };

        // Nothing is installed yet: the folders exist (a download that was removed leaves none, but an empty one is possible) and are empty.
        Assert.Equal(new WhisperModels(null, null), WhisperModel.Find(Folders));
        File.WriteAllBytes(large, [1]);
        Assert.Equal(new WhisperModels(large, null), WhisperModel.Find(Folders));
        File.WriteAllBytes(small, [1]);
        Assert.Equal(new WhisperModels(large, small), WhisperModel.Find(Folders));
        File.Delete(large);
        Assert.Equal(new WhisperModels(null, small), WhisperModel.Find(Folders));

        // No folder at all, and a folder that holds the other model's file, are not an install either.
        Assert.Equal(new WhisperModels(null, null), WhisperModel.Find(_ => null));
        Assert.Equal(new WhisperModels(null, small), WhisperModel.Find(_ => smallFolder));
    }

    [AvaloniaTheory]
    [InlineData(false, false, true)] // no model at all: the petals say they need Whisper
    [InlineData(true, false, false)]
    [InlineData(false, true, false)] // the small one alone is enough
    [InlineData(true, true, false)]
    public void The_petals_are_greyed_out_only_when_no_model_at_all_is_installed(bool large, bool small, bool greyedOut)
    {
        Assert.SkipWhen(MediaTools.Locate() is null, "FFmpeg is not installed next to the tests");
        var loc = Context().Loc;
        var models = new WhisperModels(large ? "large.bin" : null, small ? "small.bin" : null);

        foreach (var id in new[] { "transcribe", "subtitles" })
            Assert.Equal(greyedOut ? loc.Format("engine.reason.needs_package", "Whisper") : null, PanelRegistry.Unavailable(id, loc, models));
    }

    // ───────── The processor's threads ─────────

    [Fact]
    public void On_the_processor_the_filter_gets_one_thread_per_core_and_on_a_card_it_is_left_alone()
    {
        Assert.Equal(["-filter_threads", "8", "-i", "a.wav", "-vn", "-map", "0:a:0", "-/af", "g.txt", "-f", "null", "-"],
            WhisperEngine.Arguments("a.wav", "g.txt", gpu: null, cores: 8));
        Assert.Equal(["-i", "a.wav", "-vn", "-map", "0:a:0", "-/af", "g.txt", "-f", "null", "-"], WhisperEngine.Arguments("a.wav", "g.txt", gpu: 1, cores: 8));
    }

    [Fact]
    public void The_physical_cores_are_counted_in_the_records_windows_writes()
    {
        static byte[] Record(int relationship, int size)
        {
            var bytes = new byte[size];
            BitConverter.TryWriteBytes(bytes, relationship);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), size);
            return bytes;
        }

        // Three cores (relationship 0) with a cache (2) among them, each record as long as it says it is.
        byte[] buffer = [.. Record(0, 48), .. Record(2, 56), .. Record(0, 48), .. Record(0, 64)];
        Assert.Equal(3, ProcessorCores.CountCores(buffer));
        Assert.Equal(0, ProcessorCores.CountCores([]));
        Assert.Equal(0, ProcessorCores.CountCores(new byte[16])); // a "record" without a size is not one: it stops instead of looping
        Assert.Equal(1, ProcessorCores.CountCores([.. Record(0, 48), 1, 2, 3])); // and a few stray bytes at the end are ignored
    }

    [Fact]
    public void This_PC_has_at_least_one_core_and_never_more_than_its_logical_processors() =>
        Assert.InRange(ProcessorCores.Count, 1, Environment.ProcessorCount);

    // ───────── What the panels say ─────────

    [AvaloniaTheory]
    [InlineData("en", "large", "small", "graphics card", "processor")]
    [InlineData("es", "grande", "pequeño", "tarjeta gráfica", "procesador")]
    [InlineData("ko", "대형", "소형", "그래픽 카드", "프로세서")]
    [InlineData("zh-CN", "大型", "小型", "显卡", "处理器")]
    public void The_panels_say_which_model_runs_on_which_device_in_every_language(string language, string large, string small, string card, string processor)
    {
        var loc = ContextIn(language).Loc;
        var nvidia = new WhisperDevice(1, "NVIDIA GeForce RTX 4060 Laptop GPU");
        WhisperModels both = new("large.bin", "small.bin"), onlyLarge = new("large.bin", null), onlySmall = new(null, "small.bin");
        string Line(WhisperModels models, WhisperDevice device, bool wantsGpu = true) => TranscribePanelBase.DeviceLine(loc, models, device, wantsGpu);

        var largeOnCard = Line(both, nvidia);
        var smallOnCard = Line(onlySmall, nvidia);
        var smallOnProcessor = Line(both, WhisperDevice.Processor);
        var largeOnProcessor = Line(onlyLarge, WhisperDevice.Processor);

        // Each of the four says its model and its device (the card also by name); none shows an untranslated key.
        Assert.All([largeOnCard, smallOnCard, largeOnProcessor, smallOnProcessor], text => Assert.DoesNotContain("panel.transcribe.", text));
        Assert.Contains(large, largeOnCard);
        Assert.Contains(card, largeOnCard);
        Assert.Contains("RTX 4060", largeOnCard);
        Assert.Contains(small, smallOnCard);
        Assert.Contains(card, smallOnCard);
        Assert.Contains("RTX 4060", smallOnCard);
        Assert.Contains(small, smallOnProcessor);
        Assert.Contains(processor, smallOnProcessor);
        Assert.DoesNotContain("RTX", smallOnProcessor);
        Assert.Contains(large, largeOnProcessor);
        Assert.Contains(processor, largeOnProcessor);
        Assert.Equal(4, new[] { largeOnCard, smallOnCard, largeOnProcessor, smallOnProcessor }.Distinct().Count());

        // The card switched off in the options (or the same install on a PC without one): the processor and its model.
        Assert.Equal(smallOnProcessor, Line(both, nvidia, wantsGpu: false));
        Assert.Equal(smallOnProcessor, Line(onlySmall, WhisperDevice.Processor));
        Assert.Equal(largeOnProcessor, Line(onlyLarge, nvidia, wantsGpu: false));
        Assert.Equal(largeOnCard, Line(onlyLarge, nvidia));
        Assert.Equal("", Line(new WhisperModels(null, null), nvidia));
    }

    private sealed class WaitingPanel(PanelContext context) : TranscribePanel(context)
    {
        public override bool AutoRun => false; // so the window can be looked at before it starts
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void The_longest_line_about_the_model_fits_both_windows_in_every_language(string language)
    {
        var clip = Path.Combine(_root, "entrevista.mp4");
        File.WriteAllBytes(clip, [0]);
        // The long one: the large model on the processor, with the hint that the small one is faster there.
        var line = TranscribePanelBase.DeviceLine(ContextIn(language).Loc, new WhisperModels("large.bin", null), WhisperDevice.Processor, wantsGpu: true);

        using var quick = new WaitingPanel(ContextIn(language, clip)) { DeviceText = line };
        Render(quick, $"transcribe-model-{language}");
        using var options = new SubtitlesPanel(ContextIn(language, clip)) { DeviceText = line, HasGpu = true };
        Render(options, $"subtitles-model-{language}");
    }

    [AvaloniaFact]
    public void Switching_the_graphics_card_off_in_the_options_changes_the_line_to_the_processor_and_its_model()
    {
        var installed = WhisperModel.Installed();
        Assert.SkipWhen(!installed.Any, "No Whisper model is installed (Settings → Engines)");
        var clip = Path.Combine(_root, "clip.wav");
        File.WriteAllBytes(clip, [0]);
        using var panel = new SubtitlesPanel(Context(clip));
        var nvidia = new WhisperDevice(1, "NVIDIA GeForce RTX 4060 Laptop GPU");

        // A card was found. (The probe the panel starts by itself is not waited for: nothing in this test yields to it.)
        panel.ShowDeviceFor(nvidia);
        var onCard = panel.DeviceText;
        Assert.True(panel.HasGpu);
        Assert.Equal(TranscribePanelBase.DeviceLine(panel.Context.Loc, installed, nvidia, wantsGpu: true), onCard);
        Assert.Contains("RTX 4060", onCard);

        panel.UseGpu = false;

        Assert.Equal(TranscribePanelBase.DeviceLine(panel.Context.Loc, installed, nvidia, wantsGpu: false), panel.DeviceText);
        Assert.DoesNotContain("RTX", panel.DeviceText);
        Assert.NotEqual(onCard, panel.DeviceText);
        panel.UseGpu = true;
        Assert.Equal(onCard, panel.DeviceText);
    }

    // ───────── The package ─────────

    [AvaloniaFact]
    public void The_small_model_is_a_second_package_of_the_engines_page_pinned_by_its_checksum()
    {
        var package = EngineDownloads.Packages.Single(p => p.Id == "whisper-small");
        var component = EngineDownloads.Components[package.Components.Single()];

        Assert.Equal("whisper-small", component.Id); // a folder of its own: a package replaces the whole folder it installs into
        Assert.Equal("file", component.Kind);
        Assert.StartsWith("https://huggingface.co/ggerganov/whisper.cpp/resolve/", component.Url);
        Assert.EndsWith("/" + WhisperModel.SmallFile, component.Url);
        Assert.Matches("/resolve/[0-9a-f]{40}/", component.Url); // a commit, not "main": the file cannot change under the checksum
        Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
        Assert.Equal(264_464_607, component.Size);
        Assert.Equal(component.Size, package.InstalledSize);
        Assert.Empty(package.ConverterIds);
        Assert.True(EngineDownloads.CanDownload(package));
        Assert.False(EngineDownloads.IsSuggested(package)); // 264 MB is offered, not ticked for the user

        // It and the large model are two packages with two folders, two files and two checksums.
        var largePackage = EngineDownloads.Packages.Single(p => p.Id == "whisper");
        var largeComponent = EngineDownloads.Components[largePackage.Components.Single()];
        Assert.NotEqual(largeComponent.Id, component.Id);
        Assert.NotEqual(largeComponent.Sha256, component.Sha256);
        Assert.NotEqual(Path.GetFileName(largeComponent.Url), Path.GetFileName(component.Url));
        Assert.True(component.Size < largeComponent.Size, "the small model is the smaller download");

        Assert.False(new EnginePackageState(package, Context().Loc).CanUseOwnCopy); // a data file, not a program to point at
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void Both_packages_have_a_name_a_description_and_a_size_in_every_language(string language)
    {
        var loc = ContextIn(language).Loc;
        var states = EngineDownloads.Packages.Where(p => p.Id.StartsWith("whisper", StringComparison.Ordinal)).Select(p => new EnginePackageState(p, loc)).ToList();

        Assert.Equal(["whisper", "whisper-small"], states.Select(s => s.Package.Id));
        foreach (var state in states)
        {
            Assert.NotEqual($"engines.package.{state.Package.Id}.name", state.Name);
            Assert.NotEqual($"engines.package.{state.Package.Id}.description", state.Description);
            Assert.False(string.IsNullOrWhiteSpace(state.Description));
            Assert.Contains(EnginePackageState.FormatBytes(EngineDownloads.DownloadSize(state.Package)), state.Sizes);
        }
        Assert.NotEqual(states[0].Name, states[1].Name);
        Assert.NotEqual(states[0].Description, states[1].Description);
        Assert.Contains("574 MB", states[0].Sizes);
        Assert.Contains("264 MB", states[1].Sizes);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public void The_first_run_window_lists_both_packages_with_their_texts_and_still_fits_a_small_screen(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<ILocalizer>());
        var window = new EngineSetupWindow { DataContext = vm };
        window.Show();
        window.MaxHeight = 672; // what a 1366×768 screen allows (EngineSetupWindow.MaxHeightFor); a 1080p laptop at 125 % allows 769
        Pump();

        // Both cards are there, each with its name, its description and its sizes (the list holds them all, scrolled or not).
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToList();
        foreach (var id in new[] { "whisper", "whisper-small" })
        {
            var state = vm.Packages.Single(p => p.Package.Id == id);
            Assert.Contains(state.Name, texts);
            Assert.Contains(state.Description, texts);
            Assert.Contains(state.Sizes, texts);
        }
        Assert.Equal(7, vm.Packages.Count);

        // With seven engines the list scrolls inside the window and the buttons stay in view.
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "EngineScroller");
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, "the list should have more engines than room");
        Assert.True(scroller.Viewport.Height > 150, "and still show a few of them");
        var close = window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, vm.CloseCommand));
        var bottom = close.TranslatePoint(new Point(0, close.Bounds.Height), window)!.Value.Y;
        Assert.True(bottom <= window.ClientSize.Height + 0.5, $"the buttons end at {bottom:0}, the window at {window.ClientSize.Height:0}");

        // The two cards, scrolled to the top of the window, for a look.
        var whisperName = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == vm.Packages.Single(p => p.Package.Id == "whisper").Name);
        scroller.Offset = new Vector(0, Math.Max(0, whisperName.TranslatePoint(new Point(0, 0), (Visual)scroller.Content!)!.Value.Y - 24));
        Pump();
        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-setup-whisper-{language}.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        window.Close();
    }
}
