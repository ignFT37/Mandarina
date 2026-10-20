// Mandarina: the first-run engine choice. With six optional engines (Whisper made it six) the window was taller than a 1080p
// laptop screen at 125 % scaling and its buttons ended below the screen; now the list of engines scrolls inside a window that
// never grows past the working area of its screen. The seventh (the small Whisper model) scrolls the same: see also
// MandarinaWhisperSmallTests.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;

namespace Filee.App.Tests;

public sealed class MandarinaSetupWindowTests
{
    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static EngineSetupWindow Open(string language, out EngineSetupViewModel vm)
    {
        TestServices.EnsureInitialized(language);
        vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<ILocalizer>());
        return new EngineSetupWindow { DataContext = vm };
    }

    [Theory]
    [InlineData(1920, 1032, 1.25, 769)]   // a 1080p laptop at 125 %: 1032 px of working area are 825 DIP, minus the title bar and a margin
    [InlineData(1920, 1040, 1.0, 984)]
    [InlineData(2560, 1392, 1.5, 872)]
    [InlineData(1366, 728, 1.0, 672)]
    public void The_window_never_grows_past_the_working_area(int workingWidth, int workingHeight, double scaling, double expected) =>
        Assert.Equal(expected, EngineSetupWindow.MaxHeightFor(new PixelRect(0, 0, workingWidth, workingHeight), scaling));

    [AvaloniaTheory]
    [InlineData("es", 769)]
    [InlineData("ko", 769)]
    [InlineData("en", 700)]
    public void On_a_small_screen_the_engine_list_scrolls_and_the_buttons_stay_in_view(string language, double maxHeight)
    {
        var window = Open(language, out var vm);
        window.Show();
        window.MaxHeight = maxHeight; // the window fitted itself to the (headless) screen when it opened; this is a smaller one
        Pump();

        Assert.True(window.Bounds.Height <= maxHeight + 1 || window.ClientSize.Height <= maxHeight + 1,
            $"the window is {window.ClientSize.Height:0} DIP tall, the screen allows {maxHeight:0}");
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "EngineScroller");
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, "the list should have more engines than room");
        Assert.True(scroller.Viewport.Height > 150, "and still show a few of them");

        // The buttons are inside the window, below the list.
        var close = window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, vm.CloseCommand));
        var bottom = close.TranslatePoint(new Point(0, close.Bounds.Height), window)!.Value.Y;
        Assert.True(bottom <= window.ClientSize.Height + 0.5, $"the buttons end at {bottom:0}, the window at {window.ClientSize.Height:0}");

        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"mandarina-setup-small-screen-{language}.png"));
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        window.Close();
    }

    [AvaloniaFact]
    public void The_window_fits_itself_to_the_screen_it_opens_on()
    {
        var window = Open("en", out _);
        window.Show();
        Pump();

        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        Assert.NotNull(screen);
        Assert.Equal(EngineSetupWindow.MaxHeightFor(screen.WorkingArea, screen.Scaling), window.MaxHeight);
        window.Close();
    }

    [AvaloniaFact]
    public void On_a_tall_screen_nothing_scrolls_and_the_window_keeps_its_natural_height()
    {
        var window = Open("en", out _);
        window.Show();
        window.MaxHeight = 2000;
        Pump();

        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "EngineScroller");
        // What the window wants: the whole list and everything around it. A headless window sizes itself only up to 1080 DIP and,
        // with seven engines (the second Whisper package made it seven), the list and the rest need more than that. So this checks
        // that a 1440p screen allows what the window wants, and that a window as tall as it wants shows the whole list.
        var wanted = scroller.Extent.Height + (window.ClientSize.Height - scroller.Viewport.Height);
        var detail = $"(list {scroller.Extent.Height:0} DIP, room {scroller.Viewport.Height:0}, window {window.ClientSize.Height:0}, it wants {wanted:0})";
        Assert.True(EngineSetupWindow.MaxHeightFor(new PixelRect(0, 0, 2560, 1392), 1.0) >= wanted, "a 1440p screen has room for the whole window " + detail);
        if (window.ClientSize.Height >= wanted - 1)
            Assert.True(scroller.Extent.Height <= scroller.Viewport.Height + 1, "with room to spare the whole list shows " + detail);
        window.Close();
    }
}
