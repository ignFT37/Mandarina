// Mandarina: gesture (start dragging, then tap Shift / Ctrl / Alt), the tools and actions wheels and the
// glass look. The render tests write mandarina-*.png to the screenshot folder for review.

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Mandarina;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class MandarinaTests
{
    private static GestureDetector Wheels()
    {
        var detector = new GestureDetector { SystemDragThreshold = 4 };
        detector.Configure(TriggerGesture.Defaults());
        return detector;
    }

    [Fact]
    public void Tapping_shift_during_a_plain_drag_opens_the_conversion_wheel()
    {
        var detector = Wheels();
        TriggerGesture? started = null;
        detector.DragStarted += (g, _, _) => started = g;

        detector.MouseDown(TriggerMouseButton.Left, 100, 100, ModifierKeys.None);
        detector.KeyDown("LeftShift", ModifierKeys.Shift); // not dragging yet: nothing
        Assert.Null(started);
        detector.MouseMove(140, 100, ModifierKeys.None, TriggerMouseButton.Left);
        Assert.Null(started);
        detector.KeyDown("LeftShift", ModifierKeys.Shift);

        Assert.NotNull(started);
        Assert.Equal(WheelMode.Convert, started!.Wheel);
    }

    [Fact]
    public void Tapping_other_keys_switches_wheels_and_repeats_are_harmless()
    {
        var detector = Wheels();
        var starts = 0;
        var requested = new List<WheelMode>();
        detector.DragStarted += (_, _, _) => starts++;
        detector.WheelRequested += g => requested.Add(g.Wheel);

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.None);
        detector.MouseMove(30, 0, ModifierKeys.None, TriggerMouseButton.Left);
        detector.KeyDown("LeftControl", ModifierKeys.Ctrl);   // opens tools
        detector.KeyDown("LeftControl", ModifierKeys.Ctrl);   // key repeat: same wheel again
        detector.MouseMove(60, 0, ModifierKeys.Ctrl, TriggerMouseButton.Left);
        detector.KeyDown("LeftAlt", ModifierKeys.Ctrl | ModifierKeys.Alt);
        detector.KeyDown("RightShift", ModifierKeys.Shift);

        Assert.Equal(1, starts);
        Assert.Equal(new[] { WheelMode.Tools, WheelMode.Actions, WheelMode.Convert }, requested);
    }

    [Fact]
    public void Escape_cancels_only_an_open_wheel()
    {
        var detector = Wheels();
        var cancelled = 0;
        detector.DragCancelled += () => cancelled++;

        detector.KeyDown("Escape", ModifierKeys.None);
        Assert.Equal(0, cancelled);
        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.None);
        detector.MouseMove(30, 0, ModifierKeys.None, TriggerMouseButton.Left);
        detector.KeyDown("LeftAlt", ModifierKeys.Alt);
        detector.KeyDown("Escape", ModifierKeys.None);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void A_key_after_the_button_went_up_does_nothing()
    {
        var detector = Wheels();
        var starts = 0;
        detector.DragStarted += (_, _, _) => starts++;

        detector.MouseDown(TriggerMouseButton.Left, 0, 0, ModifierKeys.None);
        detector.MouseMove(30, 0, ModifierKeys.None, TriggerMouseButton.Left);
        detector.MouseUp(TriggerMouseButton.Left, 30, 0);
        detector.KeyDown("LeftShift", ModifierKeys.Shift);

        Assert.Equal(0, starts);
    }

    [AvaloniaFact]
    public void Tools_follow_the_table_for_one_file_and_a_selection()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        string[] Ids(params string[] files) => WheelCatalog.ToolsFor(files, loc).Select(t => t.Id).ToArray();

        // The full table, panels included, is checked in MandarinaPanelTests; here what each kind offers.
        Assert.Equal(WheelTable.Expected("photo", many: false), Ids(@"C:\a.jpg"));
        Assert.Equal(WheelTable.Expected("photo", many: true), Ids(@"C:\a.jpg", @"C:\b.png"));
        Assert.Equal(WheelTable.Expected("audio", many: false), Ids(@"C:\a.mp3"));
        Assert.Equal(WheelTable.Expected("video", many: false), Ids(@"C:\a.mov"));
        Assert.Equal(WheelTable.Expected("pdf", many: false), Ids(@"C:\a.pdf"));
        Assert.Equal(WheelTable.Expected("pdf", many: true), Ids(@"C:\a.pdf", @"C:\b.pdf"));
        Assert.Equal(new[] { "extractArchive" }, Ids(@"C:\a.zip"));
        Assert.Empty(Ids(@"C:\a.jpg", @"C:\b.mp3")); // different kinds
        Assert.True(WheelCatalog.ToolsFor([@"C:\a.wav"], loc)[0] is { Id: "compress", OpensPanel: true }); // its panel saves WAV as FLAC
        Assert.Equal(".flac", AudioCompressor.OutputExtension(@"C:\a.wav"));
        Assert.Equal(6, WheelCatalog.Actions.Count);
    }

    [AvaloniaTheory]
    [InlineData(WheelMode.Convert, 2)]
    [InlineData(WheelMode.Tools, 0)]
    [InlineData(WheelMode.Actions, 3)]
    public void The_glass_wheels_render(WheelMode wheel, int highlighted)
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("mandarina-render").FullName;
        var photo = Path.Combine(folder, "mandarin tree.png");
        File.WriteAllBytes(photo, new byte[2_200_000]);
        try
        {
            var vm = AppHost.Get<RadialViewModel>();
            vm.ResetForDrag(wheel);
            vm.Load([photo], clickMode: false);
            if (wheel == WheelMode.Convert)
                Assert.DoesNotContain(vm.Items, item => item.Label == "PNG"); // not its own format
            var window = new RadialWindow { DataContext = vm };
            vm.Backdrop = Wallpaper((int)Math.Ceiling(DonutMenu.SideFor(vm.OuterRadius, vm.ReserveSubRing)));
            window.Show();
            var donut = window.DonutControl;
            donut.SetExternalHit(new DonutHit(DonutHitKind.Segment, highlighted));
            vm.ShowHint(highlighted, -1, centerHot: false);
            Pump();
            Save(window, $"mandarina-{wheel.ToString().ToLowerInvariant()}.png");
            window.Close();
        }
        finally
        {
            // The view model is shared by every test: leave it on the conversion wheel, as Filee's tests expect.
            AppHost.Get<RadialViewModel>().ResetForDrag(WheelMode.Convert);
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData("es")]
    [InlineData("en")]
    public void The_eleven_petals_of_a_videos_wheel_fit_their_labels(string language)
    {
        TestServices.EnsureInitialized(language);
        var folder = Directory.CreateTempSubdirectory("mandarina-render").FullName;
        var video = Path.Combine(folder, "paseo.mp4");
        File.WriteAllBytes(video, new byte[3_000_000]);
        try
        {
            var vm = AppHost.Get<RadialViewModel>();
            vm.ResetForDrag(WheelMode.Tools);
            vm.Load([video], clickMode: false);
            Assert.Equal(MediaTools.Locate() is null ? 3 : 11, vm.Items.Count); // compress … redact, and transcribe and subtitles
            var window = new RadialWindow { DataContext = vm };
            vm.Backdrop = Wallpaper((int)Math.Ceiling(DonutMenu.SideFor(vm.OuterRadius, vm.ReserveSubRing)));
            window.Show();
            window.DonutControl.SetExternalHit(new DonutHit(DonutHitKind.Segment, 3));
            vm.ShowHint(3, -1, centerHot: false);
            Pump();
            Save(window, $"mandarina-tools-video-{language}.png");
            window.Close();
        }
        finally
        {
            AppHost.Get<RadialViewModel>().ResetForDrag(WheelMode.Convert);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>An orange-to-violet gradient standing in for the blurred desktop.</summary>
    private static Bitmap Wallpaper(int side)
    {
        var pixels = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var t = (x + y) / (2.0 * side);
            var at = (y * side + x) * 4;
            pixels[at] = (byte)(60 + 140 * t);      // B
            pixels[at + 1] = (byte)(150 - 40 * t);  // G
            pixels[at + 2] = (byte)(245 - 60 * t);  // R
            pixels[at + 3] = 255;
        }
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(), new PixelSize(side, side),
                new Vector(96, 96), side * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(Avalonia.Controls.TopLevel window, string name)
    {
        using var frame = window.CaptureRenderedFrame(); // disposed, not left to the finalizer
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, name));
        frame.Save(stream, new PngBitmapEncoderOptions());
    }
}
