// Mandarina: closing a tool panel with Esc or ✕ while it holds work that was not saved (marks, covered areas, a new page order,
// a crop box…) asks first. The window's flow is checked with a small panel of its own; then each real panel says what counts
// as work, and the question is rendered in the four languages.

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaUnsavedTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-unsaved").FullName;

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

    /// <summary>A panel whose "work" the test switches by hand, and whose run can be held until the test lets it go.</summary>
    [ToolPanel("testUnsaved")]
    private sealed class WorkPanel(PanelContext context) : ToolPanel(context)
    {
        public bool Work { get; set; }

        public bool Disposed { get; private set; }

        public TaskCompletionSource? Hold { get; set; }

        public override bool HasUnsavedWork => Work;

        public override Control CreateView() => new Border();

        public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
        {
            if (Hold is { } hold)
                await hold.Task.WaitAsync(cancel);
            return [];
        }

        public override void Dispose()
        {
            Disposed = true;
            base.Dispose();
        }
    }

    private static Button Find(Window window, string automationId) =>
        window.GetVisualDescendants().OfType<Button>().First(button => AutomationProperties.GetAutomationId(button) == automationId);

    private static void Click(Window window, string automationId)
    {
        Find(window, automationId).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private static void PressEscape(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    private static (WorkPanel Panel, ToolPanelWindow Window) Open(bool work)
    {
        var panel = new WorkPanel(Context()) { Work = work };
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        return (panel, window);
    }

    // ───────── The window ─────────

    [AvaloniaTheory]
    [InlineData("Esc")]
    [InlineData("X")]
    public void Without_work_to_lose_Esc_and_the_x_close_at_once(string how)
    {
        var (panel, window) = Open(work: false);

        if (how == "Esc")
            PressEscape(window);
        else
            Click(window, "PanelClose");

        Assert.False(window.IsVisible);
        Assert.True(panel.Disposed);
    }

    [AvaloniaTheory]
    [InlineData("Esc")]
    [InlineData("X")]
    public void With_unsaved_work_Esc_and_the_x_ask_first(string how)
    {
        var (panel, window) = Open(work: true);

        if (how == "Esc")
            PressEscape(window);
        else
            Click(window, "PanelClose");

        Assert.True(window.IsVisible);
        Assert.True(window.IsAskingToClose);
        Assert.False(panel.Disposed);
        Assert.False(Find(window, "PanelApply").IsEffectivelyEnabled, "the panel under the question must not take input");
        Assert.True(Find(window, "PanelUnsavedKeep").IsEffectivelyEnabled);

        window.Close();
    }

    [AvaloniaFact]
    public void Keep_editing_goes_back_to_the_panel()
    {
        var (panel, window) = Open(work: true);
        Click(window, "PanelClose");
        Assert.True(window.IsAskingToClose);

        Click(window, "PanelUnsavedKeep");

        Assert.False(window.IsAskingToClose);
        Assert.True(window.IsVisible);
        Assert.False(panel.Disposed);
        Assert.True(Find(window, "PanelApply").IsEffectivelyEnabled);

        // And it asks again the next time.
        PressEscape(window);
        Assert.True(window.IsAskingToClose);
        window.Close();
    }

    [AvaloniaFact]
    public void Esc_on_the_question_answers_it_the_safe_way()
    {
        var (panel, window) = Open(work: true);
        PressEscape(window);
        Assert.True(window.IsAskingToClose);

        PressEscape(window);

        Assert.False(window.IsAskingToClose);
        Assert.True(window.IsVisible);
        Assert.False(panel.Disposed);
        window.Close();
    }

    [AvaloniaFact]
    public void Close_without_saving_closes_the_window_and_stops_the_panel()
    {
        var (panel, window) = Open(work: true);
        Click(window, "PanelClose");

        Click(window, "PanelUnsavedDiscard");

        Assert.False(window.IsVisible);
        Assert.True(panel.Disposed);
    }

    [AvaloniaFact]
    public async Task A_saved_panel_closes_without_asking()
    {
        var (panel, window) = Open(work: true);
        await panel.RunAsync(); // the work is on disk now: the ✓ is up

        Assert.True(panel.IsDone);
        PressEscape(window);

        Assert.False(window.IsVisible);
        Assert.True(panel.Disposed);
    }

    [AvaloniaFact]
    public async Task A_run_that_fails_keeps_the_work_so_the_question_still_comes()
    {
        var panel = new WorkPanel(Context()) { Work = true, Hold = new TaskCompletionSource() };
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        panel.Hold.SetException(new PanelException("boom"));

        await panel.RunAsync();
        Assert.True(panel.HasError);
        PressEscape(window);

        Assert.True(window.IsAskingToClose);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Discarding_while_it_is_applying_stops_the_run()
    {
        var panel = new WorkPanel(Context()) { Work = true, Hold = new TaskCompletionSource() };
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var run = panel.RunAsync();
        Assert.True(panel.IsBusy);

        Click(window, "PanelClose");
        Assert.True(window.IsAskingToClose, "the work is not saved until the run ends");
        Click(window, "PanelUnsavedDiscard");
        await run;

        Assert.False(window.IsVisible);
        Assert.False(panel.IsBusy);
        Assert.False(panel.IsDone);
    }

    [AvaloniaFact]
    public async Task The_question_goes_away_when_the_run_finishes_meanwhile()
    {
        var panel = new WorkPanel(Context()) { Work = true, Hold = new TaskCompletionSource() };
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var run = panel.RunAsync();
        Click(window, "PanelClose");
        Assert.True(window.IsAskingToClose);

        panel.Hold.SetResult();
        await run;
        Pump();

        Assert.True(panel.IsDone);
        Assert.False(window.IsAskingToClose);
        window.Close();
    }

    [AvaloniaFact]
    public void Alt_F4_and_the_taskbar_ask_too_but_closing_from_code_does_not()
    {
        var (panel, window) = Open(work: true);
        var handled = false;

        // A close that comes from the system (WM_CLOSE) is held back, with the question…
        window.OnWindowMessage(0, 0x0010, 0, 0, ref handled);
        Assert.True(handled);
        Assert.True(window.IsAskingToClose);
        Assert.True(window.IsVisible);
        Assert.False(panel.Disposed);

        // …any other message is none of its business…
        handled = false;
        window.OnWindowMessage(0, 0x0001, 0, 0, ref handled);
        Assert.False(handled);

        // …and the answer closes it.
        Click(window, "PanelUnsavedDiscard");
        Assert.False(window.IsVisible);
        Assert.True(panel.Disposed);
    }

    [AvaloniaFact]
    public async Task A_system_close_goes_through_when_there_is_nothing_to_lose_or_it_is_saved()
    {
        var (_, plain) = Open(work: false);
        var handled = false;
        plain.OnWindowMessage(0, 0x0010, 0, 0, ref handled);
        Assert.False(handled);
        Assert.False(plain.IsAskingToClose);
        plain.Close();

        var (saved, done) = Open(work: true);
        await saved.RunAsync();
        handled = false;
        done.OnWindowMessage(0, 0x0010, 0, 0, ref handled);
        Assert.False(handled);
        done.Close();
    }

    [AvaloniaFact]
    public void Closing_from_code_still_closes_a_panel_with_unsaved_work()
    {
        // Close() is what the app and the tests use to dispose of a window: it never asks.
        var (panel, window) = Open(work: true);

        window.Close();

        Assert.False(window.IsVisible);
        Assert.True(panel.Disposed);
    }

    // ───────── The question, in every language ─────────

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_question_renders_over_a_real_panel_in_every_language(string language)
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"), 1200, 800);
        var panel = new AnnotateImagePanel(ContextIn(language, photo));
        await WaitAsync(() => !panel.IsLoading);
        panel.Document.Add(new Annotation { Kind = AnnotationKind.Line, Color = AnnotationPalette.Red, Thickness = 8, Points = [new Point(100, 100), new Point(700, 450)] });
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();

        Click(window, "PanelClose");
        Assert.True(window.IsAskingToClose);
        Pump();

        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-panel-unsaved-{language}.png"));
            frame.Save(stream, new PngBitmapEncoderOptions());
        }

        // Both buttons and the texts sit inside the window, whatever the language.
        foreach (var id in new[] { "PanelUnsavedKeep", "PanelUnsavedDiscard" })
        {
            var button = Find(window, id);
            var origin = button.TranslatePoint(default, window)!.Value;
            Assert.True(origin.X >= 0 && origin.X + button.Bounds.Width <= window.Width, $"{id} is cut off in {language}");
        }
        Assert.All(window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Text is { Length: > 0 } && text.IsEffectivelyVisible),
            text => Assert.False(text.Text!.StartsWith("panel.unsaved"), "a missing translation shows its key"));
        window.Close();
    }

    // ───────── What counts as work, panel by panel ─────────

    private async Task<string> AudioAsync()
    {
        var file = await MakeMediaAsync(Path.Combine(_root, "tono.wav"), "-f", "lavfi", "-i", "sine=frequency=440:duration=6");
        if (file is null)
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
        return file!;
    }

    private async Task<string> VideoAsync(string name = "clip.mp4")
    {
        var file = await MakeMediaAsync(Path.Combine(_root, name), "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-shortest", "-pix_fmt", "yuv420p");
        if (file is null)
            Assert.Skip("FFmpeg is not installed next to the tests (engines\\ffmpeg).");
        return file!;
    }

    [AvaloniaFact]
    public async Task Annotate_asks_once_there_is_a_mark()
    {
        using var panel = new AnnotateImagePanel(Context(MakePhoto(Path.Combine(_root, "a.jpg"), 900, 600)));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);

        var mark = panel.Document.Add(new Annotation { Kind = AnnotationKind.Line, Color = AnnotationPalette.Red, Thickness = 4, Points = [new Point(0, 0), new Point(5, 5)] });
        Assert.True(panel.HasUnsavedWork);

        panel.Document.Remove(mark.Id);
        Assert.False(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Redact_asks_once_there_is_an_area_to_cover()
    {
        using var panel = new RedactImagePanel(Context(MakePhoto(Path.Combine(_root, "r.jpg"), 900, 600)));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);

        panel.Regions.Add(new RedactRegion(new Rect(100, 100, 400, 300)));
        Assert.True(panel.HasUnsavedWork);

        panel.ClearRegions();
        Assert.False(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Redact_audio_asks_once_there_is_a_stretch_to_bleep()
    {
        using var panel = new RedactAudioPanel(Context(await AudioAsync()));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);

        panel.Ranges.Add(new BleepRange(1, 2));
        Assert.True(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Organize_asks_once_the_pages_differ_from_the_original()
    {
        using var panel = new OrganizePdfPanel(Context(MakePdf(Path.Combine(_root, "doc.pdf"), 3)));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);

        panel.SelectOnly(panel.Items[2]);
        panel.MoveSelectionTo(0);
        Assert.True(panel.HasUnsavedWork);

        panel.Reset();
        await WaitAsync(() => !panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Edit_asks_once_a_control_was_moved()
    {
        using var panel = new EditImagePanel(Context(MakePhoto(Path.Combine(_root, "e.jpg"), 900, 600)));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);

        panel.Exposure = 0.5;
        Assert.True(panel.HasUnsavedWork);

        panel.Exposure = 0;
        Assert.False(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Crop_asks_once_the_box_leaves_the_whole_picture_but_not_with_several_photos()
    {
        using var panel = new CropImagePanel(Context(MakePhoto(Path.Combine(_root, "c.jpg"), 900, 600)));
        await WaitAsync(() => panel.IsReady);
        Assert.False(panel.HasUnsavedWork);

        panel.SelectRatio("1:1");
        Assert.True(panel.HasUnsavedWork);

        // With several photos nothing is dragged, only a proportion is chosen: choosing it again costs nothing.
        using var many = new CropImagePanel(Context(MakePhoto(Path.Combine(_root, "c1.jpg"), 900, 600), MakePhoto(Path.Combine(_root, "c2.jpg"), 600, 900)));
        await WaitAsync(() => many.IsReady);
        many.SelectRatio("1:1");
        Assert.False(many.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Crop_video_asks_once_the_box_leaves_the_whole_frame()
    {
        using var panel = new CropVideoPanel(Context(await VideoAsync()));
        await WaitAsync(() => panel.IsReady);
        Assert.False(panel.HasUnsavedWork);

        panel.SelectRatio("1:1");
        Assert.True(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Trim_audio_asks_once_a_handle_moves_in()
    {
        using var panel = new TrimAudioPanel(Context(await AudioAsync()));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);

        panel.Start = 1;
        Assert.True(panel.HasUnsavedWork);

        panel.Reset();
        Assert.False(panel.HasUnsavedWork);

        panel.End = panel.Duration - 1;
        Assert.True(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Trim_video_asks_once_a_handle_moves_in()
    {
        using var panel = new TrimVideoPanel(Context(await VideoAsync()));
        await WaitAsync(() => panel.IsReady);
        Assert.False(panel.HasUnsavedWork);

        panel.Start = 1;
        Assert.True(panel.HasUnsavedWork);

        panel.Start = 0;
        Assert.False(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Split_asks_once_there_is_a_cut()
    {
        using var panel = new SplitVideoPanel(Context(await VideoAsync()));
        await WaitAsync(() => panel.IsReady);
        Assert.False(panel.HasUnsavedWork);

        panel.Position = 2;
        Assert.True(panel.AddCutHere());
        Assert.True(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Metadata_asks_once_a_value_was_typed_but_not_when_it_only_removes()
    {
        using var panel = new MetadataPanel(Context(MakePhoto(Path.Combine(_root, "m.jpg"), 600, 400)));
        await WaitAsync(() => !panel.IsLoading);
        panel.IsEditMode = true;
        Assert.False(panel.HasUnsavedWork);

        panel.Field("Author")!.Text = "Persona Autora";
        Assert.True(panel.HasUnsavedWork);

        panel.IsEditMode = false; // "remove everything" has nothing typed to lose
        Assert.False(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Join_asks_once_the_clips_were_reordered_or_removed()
    {
        using var panel = new JoinVideosPanel(Context(await VideoAsync("uno.mp4"), await VideoAsync("dos.mp4")));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);

        panel.Clips.Move(0, 1);
        Assert.True(panel.HasUnsavedWork);

        panel.Clips.Move(1, 0);
        Assert.False(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Collage_asks_once_the_photos_were_reordered_or_removed()
    {
        using var panel = new CreateCollagePanel(Context(MakePhoto(Path.Combine(_root, "k1.jpg"), 900, 600), MakePhoto(Path.Combine(_root, "k2.jpg"), 600, 900),
            MakePhoto(Path.Combine(_root, "k3.jpg"), 800, 800)));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);

        panel.Move(panel.Items[0], 1);
        Assert.True(panel.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task A_panel_that_only_has_options_never_asks()
    {
        using var panel = new CompressPanel(Context(MakePhoto(Path.Combine(_root, "o.jpg"), 900, 600)));
        await WaitAsync(() => !panel.IsLoading);
        Assert.False(panel.HasUnsavedWork);
    }
}
