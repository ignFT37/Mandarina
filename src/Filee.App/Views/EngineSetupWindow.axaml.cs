using Avalonia;
using Avalonia.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class EngineSetupWindow : Window
{
    /// <summary>What a window loses besides its client area: the title bar and frame (about 40 DIP) and a little air from the screen's edge.</summary>
    private const double NonClientAndMargin = 56;

    public EngineSetupWindow()
    {
        InitializeComponent();
        FitToScreen();
        Opened += (_, _) => FitToScreen();
        Motion.Track(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is EngineSetupViewModel vm)
                vm.CloseRequested += (_, _) => Close();
        };
    }

    /// <summary>
    /// Mandarina: the tallest this window may be on a screen whose usable area (without the taskbar) is <paramref name="workingArea"/>
    /// pixels at <paramref name="scaling"/> (1.25 for 125 %). The engine list is as tall as the number of engines, and at 125 % a 1080p
    /// laptop has only about 825 DIP: the list scrolls inside this height instead of pushing the buttons off the screen.
    /// </summary>
    internal static double MaxHeightFor(PixelRect workingArea, double scaling) =>
        Math.Max(320, Math.Floor(workingArea.Height / Math.Max(scaling, 1)) - NonClientAndMargin);

    private void FitToScreen()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
            MaxHeight = MaxHeightFor(screen.WorkingArea, screen.Scaling);
    }
}
