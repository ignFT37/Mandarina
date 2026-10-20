using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Win32;
using Filee.App.Services;

namespace Filee.App.Mandarina.Panels;

/// <summary>The frame around a <see cref="ToolPanel"/>: runs Apply, shows the result and closes itself after it.</summary>
public partial class ToolPanelWindow : Window
{
    /// <summary>How long the ✓ stays before the window closes.</summary>
    private static readonly TimeSpan CloseAfterApplied = TimeSpan.FromMilliseconds(1600);

    /// <summary>The frame's transparent margin (the shadow lives there), on each side.</summary>
    private const double FrameMargin = 18;

    private readonly ToolPanel? _panel;
    private bool _closing;
    private IInputElement? _focusBefore;

    public ToolPanelWindow()
    {
        InitializeComponent();
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button and not PathIcon)
                BeginMoveDrag(e);
        };
        Motion.Track(this);
    }

    public ToolPanelWindow(ToolPanel panel) : this()
    {
        _panel = panel;
        DataContext = panel;
        Width = panel.PreferredSize.Width + 2 * FrameMargin;
        Height = panel.PreferredSize.Height + 2 * FrameMargin;
        Host.Content = panel.CreateView();
        panel.Applied += OnApplied;
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(this, OnWindowMessage);
        if (panel.AutoRun)
            Opened += (_, _) => _ = panel.RunAsync();
        Closed += (_, _) =>
        {
            panel.Applied -= OnApplied;
            panel.Dispose();
            // A UI Automation client that has read this window would keep all of it (see AccessibilityRelease).
            AccessibilityRelease.AfterWindowClosed();
        };
    }

    /// <summary>The panel this window shows.</summary>
    public ToolPanel? Panel => _panel;

    /// <summary>
    /// How much room the options need at the window's width, and how much they have. A panel whose Needed exceeds
    /// Available is clipped: raise its <see cref="ToolPanel.PreferredSize"/> (render tests assert this).
    /// </summary>
    public (double Needed, double Available) ContentRoom()
    {
        if (Host.Content is not Control view)
            return (0, 0);
        view.Measure(new Size(Host.Bounds.Width, double.PositiveInfinity));
        return (view.DesiredSize.Height, Host.Bounds.Height);
    }

    /// <summary>
    /// Shows the window centred on the screen under <paramref name="near"/> (the primary one without it), in front of
    /// whatever is there: the wheel was just used over a file manager, which must not cover the panel.
    /// </summary>
    public void ShowCentered(PixelPoint? near = null)
    {
        Motion.Refresh();
        var screen = (near is { } point ? Screens.ScreenFromPoint(point) : null) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var width = (int)Math.Round(Width * screen.Scaling);
            var height = (int)Math.Round(Height * screen.Scaling);
            Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2), area.Y + Math.Max(0, (area.Height - height) / 2));
        }
        Topmost = true;
        Show();
        Activate();
        // In front now; staying above every other window would make it a nuisance while it is open.
        DispatcherTimer.RunOnce(() => Topmost = false, TimeSpan.FromMilliseconds(400));
    }

    /// <summary>The Windows message that asks a window to close: Alt+F4, the taskbar's Close and the system menu send it.</summary>
    private const uint WmClose = 0x0010;

    /// <summary>
    /// Alt+F4 and the taskbar close the window without going through ✕ or Esc. They send WM_CLOSE, which <c>Close()</c> from code
    /// does not, so this is only ever the user's own close: with unsaved work it asks first instead of closing.
    /// </summary>
    internal nint OnWindowMessage(nint window, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmClose && !_closing && _panel is { IsDone: false, HasUnsavedWork: true })
        {
            handled = true;
            AskBeforeClosing();
        }
        return 0;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            // With the question up, Esc answers it the safe way: keep working.
            if (IsAskingToClose)
                KeepEditing();
            else
                RequestClose();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => RequestClose();

    /// <summary>True while the window asks whether to close and lose the unsaved work.</summary>
    public bool IsAskingToClose => UnsavedLayer.IsVisible;

    /// <summary>
    /// What Esc and ✕ do: close at once, or first ask when the panel holds work that was not saved (a finished run has
    /// saved it, so a window that shows the ✓ never asks).
    /// </summary>
    public void RequestClose()
    {
        if (_closing)
            return;
        if (_panel is { IsDone: false, HasUnsavedWork: true })
            AskBeforeClosing();
        else
            CloseNow();
    }

    private void AskBeforeClosing()
    {
        if (IsAskingToClose)
            return;
        _focusBefore = FocusManager?.GetFocusedElement();
        Shell.IsEnabled = false;
        UnsavedLayer.IsVisible = true;
        UnsavedKeep.Focus();
    }

    private void KeepEditing()
    {
        if (!IsAskingToClose)
            return;
        UnsavedLayer.IsVisible = false;
        Shell.IsEnabled = true;
        if (_focusBefore is { } before && before.IsEffectivelyEnabled)
            before.Focus();
        _focusBefore = null;
    }

    private void OnKeep(object? sender, RoutedEventArgs e) => KeepEditing();

    private void OnDiscard(object? sender, RoutedEventArgs e) => CloseNow();

    private void CloseNow()
    {
        if (_closing)
            return;
        _closing = true;
        _panel?.Cancel();
        Close();
    }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_panel is not null)
            await _panel.RunAsync();
    }

    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        _panel?.Reveal();
        CloseNow();
    }

    private void OnApplied(object? sender, EventArgs e)
    {
        // The run finished while the question was up: the work is saved now, there is nothing left to ask.
        KeepEditing();
        DispatcherTimer.RunOnce(() =>
        {
            if (!_closing)
                CloseNow();
        }, CloseAfterApplied);
    }
}
