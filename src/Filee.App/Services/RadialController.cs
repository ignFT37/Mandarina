// Opens, fills and closes the live donut toolbar.
//
// Drag mode  (modifier + drag in Explorer):
//   gesture detected → an invisible window waits at the cursor (not activated) → DragEnter brings the file list →
//   the donut pops out → drop on a slice converts / drop in the hole or release elsewhere closes. Without files
//   (text, a rubber-band selection) nothing ever shows.
// Click mode (keyboard shortcut, context menu, drop zone):
//   files are known up front → donut appears activated → click a slice to convert, right-click to edit,
//   Esc / click outside closes.
// After a drop / click the donut becomes a progress ring at the same spot (DonutMenu.Progress.cs):
//   finished quickly → ✓ pops up in the centre, then the ring fades out;
//   still running after HandOffAfter, failed, Esc, click elsewhere or a new gesture → the toast takes over.

using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Mandarina;
using Filee.App.Mandarina.Panels;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class RadialController(
    RadialViewModel viewModel,
    ConversionService conversions,
    WindowService windows,
    UserDataStore store,
    IPlatformServices platform,
    FileActions actions,
    PanelHost panels,
    ILogger<RadialController> log)
{
    /// <summary>Mandarina: blur radius of the frosted ring, in logical pixels (CSS backdrop-filter: blur(16px)).</summary>
    private const double BackdropBlur = 16;

    /// <summary>
    /// How long the ring shows a running job before the toast window takes over. Long enough for a typical office
    /// document (LibreOffice needs a few seconds), so the user sees it finish where they dropped it.
    /// </summary>
    private static readonly TimeSpan HandOffAfter = TimeSpan.FromSeconds(5);

    private RadialWindow? _window;
    private bool _visible;
    private bool _dropped;
    private bool _editing;
    private JobViewModel? _progressJob;   // job currently shown by the ring (null once finished / handed off)
    private JobViewModel? _lastRingJob;   // for "click the ✓ to open the folder"
    private IDisposable? _handOffTimer;
    private int _dragGeneration;
    private CancellationTokenSource? _actionCancel; // Mandarina: the file action the ring is showing, for its ×

    /// <summary>Mandarina: how the reason an action failed reaches the user: a message on screen (tests replace it).</summary>
    internal Func<string, Task>? ShowProblem { get; set; }

    /// <summary>
    /// How long a drag that never brought files may keep the invisible window up, in case the button release is
    /// missed (the system dropped the input hook, a UAC prompt took the desktop): the window catches clicks.
    /// </summary>
    private static readonly TimeSpan EmptyDragLimit = TimeSpan.FromSeconds(10);

    public bool IsVisible => _visible;

    internal RadialWindow? Window => _window;

    /// <summary>
    /// Drag gesture started at the given physical screen point. The window is placed under the cursor but stays
    /// invisible until files are dragged into it (FilesEntered); a gesture without files (text, a rubber-band
    /// selection, nothing at all) never shows anything and closes when the button is released.
    /// </summary>
    public void ShowForDrag(int x, int y, WheelMode wheel = WheelMode.Convert)
    {
        ReleaseProgressToToast();
        var window = EnsureWindow();
        // A ring from an earlier conversion (finished or handed to the toast) must not keep this gesture's window up.
        window.DonutControl.LeaveProgressMode();
        _dropped = false;
        viewModel.ResetForDrag(wheel);
        window.DonutControl.AllowContextEdit = false;
        PlaceAndShow(window, x, y, activate: false);

        var generation = ++_dragGeneration;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _dragGeneration && _visible && !viewModel.HasFiles && !viewModel.IsClickMode)
                Close(animate: false);
        }, EmptyDragLimit);
    }

    /// <summary>Opens the donut for known files (click mode).</summary>
    public void ShowForFiles(int x, int y, IReadOnlyList<string> files, WheelMode wheel = WheelMode.Convert)
    {
        if (files.Count == 0)
            return;
        ReleaseProgressToToast();
        var window = EnsureWindow();
        _dropped = false;
        viewModel.ResetForDrag(wheel);
        viewModel.Load(files, clickMode: true);
        viewModel.FitToThisDonut();
        window.DonutControl.AllowContextEdit = wheel == WheelMode.Convert;
        PlaceAndShow(window, x, y, activate: true);
        window.DonutControl.PlayOpenAnimation();
    }

    /// <summary>
    /// Mandarina: another wheel's key was tapped while the wheel is open. Shows that wheel for the same files (it
    /// blooms open again); before the files arrived it just remembers which wheel to show.
    /// </summary>
    public void SwitchWheel(WheelMode wheel)
    {
        if (!_visible || _window is null || _window.DonutControl.IsInProgressMode || !viewModel.SetWheel(wheel))
            return;
        _window.DonutControl.AllowContextEdit = viewModel.IsClickMode && wheel == WheelMode.Convert;
        if (viewModel.HasFiles)
            _window.DonutControl.PlayOpenAnimation();
    }

    /// <summary>Mandarina: Esc during the drag. Explorer cancels the drag; the wheel goes too.</summary>
    public void CancelDrag()
    {
        if (_visible && !viewModel.IsClickMode && _window is { DonutControl.IsInProgressMode: false })
            Close();
    }

    /// <summary>Mandarina: the wheel a modifier key opens, from the drag gestures in Settings → Shortcuts.</summary>
    private WheelMode? WheelOf(ModifierKeys key) =>
        store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag && t.Modifiers == key)?.Wheel;

    /// <summary>The mouse button of the drag gesture was released somewhere.</summary>
    public void OnDragGestureEnded()
    {
        if (!_visible || viewModel.IsClickMode)
            return;
        // Nothing was brought to the donut: the window is invisible but catches clicks, so go right away.
        if (!viewModel.HasFiles && !_window!.DonutControl.IsInProgressMode)
        {
            Close(animate: false);
            return;
        }
        // A drop on the donut arrives around the same time as the button release; give it a moment.
        DispatcherTimer.RunOnce(() =>
        {
            if (_visible && !_dropped && !viewModel.IsClickMode)
                Close(animate: true);
        }, TimeSpan.FromMilliseconds(250));
    }

    public void Close(bool animate = true)
    {
        if (!_visible || _window is null)
            return;
        if (_window.DonutControl.IsInProgressMode)
        {
            // Esc / click elsewhere while the ring is showing: let the toast carry on (a finishing ✓ just fades).
            if (_progressJob is not null)
            {
                HandOffToToast();
                return;
            }
            if (_window.DonutControl.IsFinishingProgress)
                return;
            // A ring left over after it finished: nothing is showing any more, so close like any donut.
            _window.DonutControl.LeaveProgressMode();
        }
        _visible = false;
        var window = _window;
        // A group about to open (its dwell timer running) must not open on a closing donut.
        window.DonutControl.CollapseGroup();
        if (animate)
            window.DonutControl.PlayCloseAnimation(() =>
            {
                if (!_visible)
                    window.Hide();
            });
        else
            window.Hide();
    }

    private RadialWindow EnsureWindow()
    {
        if (_window is not null)
            return _window;

        var window = new RadialWindow { DataContext = viewModel };
        var donut = window.DonutControl;

        window.FilesEntered += files =>
        {
            if (viewModel.IsClickMode || SameFiles(files, viewModel.Files))
                return;
            viewModel.Load(files, clickMode: false);
            donut.PlayOpenAnimation();
        };
        window.Dropped += (hit, files) =>
        {
            _dropped = true;
            if (hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment)
                Invoke(hit.Index, hit.Sub, files.Count > 0 ? files : viewModel.Files);
            else
                Close();
        };
        window.WheelKeyPressed += key =>
        {
            if (WheelOf(key) is { } wheel)
                SwitchWheel(wheel);
        };
        // One line per drag, to tell "the drag never reached the donut" from "it did but nothing lit up" when someone
        // reports a donut that doesn't react.
        window.DragSummary += (events, hit, lit) =>
            log.LogInformation("Drag over the donut: {Events} move event(s), ended on {Kind} {Index}, drawn lit {Lit:0.00}",
                events, hit.Kind, hit.Index, lit);
        window.EscapePressed += () => Close();
        window.Deactivated += (_, _) =>
        {
            if (viewModel.IsClickMode && !_editing)
                Close();
        };

        var lastTick = -1;
        donut.ActiveIndexChanged += (_, index) =>
        {
            viewModel.ShowHint(index, donut.ActiveSubIndex, donut.IsCenterHot);
            // Mandarina: a soft tick each time the pointer reaches another slice.
            if (index >= 0 && index != lastTick && store.Settings.Donut.Sound && !donut.IsInProgressMode)
                platform.PlaySound(WheelSound.Tick);
            lastTick = index;
        };
        donut.SegmentInvoked += (_, index) => Invoke(index, -1, viewModel.Files);
        donut.SubSegmentInvoked += (_, hit) => Convert(hit.Group, hit.Sub, viewModel.Files);
        donut.CenterInvoked += (_, _) => Close();
        donut.SegmentContextRequested += async (_, index) => await EditPresetAsync(index);
        donut.SubSegmentContextRequested += async (_, hit) => await EditPresetAsync(hit.Group, hit.Sub);
        donut.ProgressCancelRequested += (_, _) =>
        {
            _progressJob?.CancelCommand.Execute(null);
            _actionCancel?.Cancel(); // Mandarina
        };
        donut.ProgressClicked += (_, _) =>
        {
            if (_lastRingJob is { HasOutputs: true } job)
                job.OpenFolderCommand.Execute(null);
        };

        _window = window;
        return window;
    }

    /// <summary>Mandarina: runs slice <paramref name="index"/> of whichever wheel is open.</summary>
    internal void Invoke(int index, int sub, IReadOnlyList<string> files)
    {
        if (viewModel.Wheel == WheelMode.Actions)
        {
            _ = RunActionAsync(index, files);
            return;
        }
        var paths = files.Where(path => !Directory.Exists(path)).ToList(); // folders only have actions
        if (viewModel.Wheel == WheelMode.Tools && index >= 0 && index < viewModel.Entries.Count && viewModel.Entries[index].OpensPanel)
            OpenPanel(index, paths);
        else
            Convert(index, sub, paths); // the other tools are presets too
    }

    /// <summary>Mandarina: a tool with a panel. The wheel goes away and the panel opens on the screen it was on.</summary>
    private void OpenPanel(int index, IReadOnlyList<string> files)
    {
        if (_window is not { } window || files.Count == 0 || !viewModel.Items[index].IsEnabled)
        {
            Close();
            return;
        }
        var at = window.Position + new PixelPoint((int)(window.Bounds.Width * window.RenderScaling / 2),
            (int)(window.Bounds.Height * window.RenderScaling / 2));
        _dropped = true;
        var id = viewModel.Entries[index].Id;
        Close();
        panels.Open(id, files, at);
    }

    /// <summary>
    /// Mandarina: runs an action. The wheel turns into a ✓ (or ✗) where it was, as after a conversion; the folder picker
    /// of Move opens while the window is still up, so it has an owner on top of Explorer.
    /// </summary>
    private async Task RunActionAsync(int index, IReadOnlyList<string> files)
    {
        if (_window is not { } window || index < 0 || index >= viewModel.Entries.Count || files.Count == 0)
        {
            Close();
            return;
        }
        var entry = viewModel.Entries[index];
        var donut = window.DonutControl;
        var at = window.Position + new PixelPoint((int)(window.Bounds.Width * window.RenderScaling / 2),
            (int)(window.Bounds.Height * window.RenderScaling / 2));
        _dropped = true;
        log.LogInformation("File action {Action} on {Count} item(s)", entry.Id, files.Count);
        donut.BeginProgress();
        donut.SetProgress(FileActions.StartProgress(entry.Id, files));
        // The ring's × stops a long action (compressing a folder to share it); its progress fills the ring as it goes.
        var cancel = new CancellationTokenSource();
        _actionCancel = cancel;
        ActionResult result;
        try
        {
            result = await actions.RunAsync(entry.Id, files, window, at, new Progress<double>(donut.SetProgress), cancel.Token);
        }
        finally
        {
            if (ReferenceEquals(_actionCancel, cancel))
                _actionCancel = null;
            cancel.Dispose();
        }
        // A failure with something to say (a file that could not be read) shows the "!" where the arc stopped, and says why; an action
        // that simply did nothing (the picker was closed, the ring's × was pressed) shows neither the ✓ nor the "!".
        if (result.Problem is null)
            donut.SetProgress(1);
        donut.CompleteProgress(result.Succeeded ? DonutProgressOutcome.Succeeded
            : result.Problem is null ? DonutProgressOutcome.Cancelled : DonutProgressOutcome.Failed, HideWindow);
        if (result.Problem is { } problem)
            _ = (ShowProblem ?? (text => windows.MessageAsync(null, text)))(problem);
    }

    /// <summary>Converts with slice <paramref name="index"/>, or with slice <paramref name="sub"/> of that group's ring.</summary>
    private void Convert(int index, int sub, IReadOnlyList<string> files)
    {
        if (viewModel.PresetAt(index, sub) is not { } preset || files.Count == 0)
        {
            Close();
            return;
        }

        log.LogInformation("Converting {Count} file(s) with preset {Preset}", files.Count, preset.Id);
        var job = conversions.Start(files, preset, showInToast: false);
        if (job is null || _window is null)
        {
            Close();
            return;
        }
        ShowProgress(job);
    }

    // ───────── Progress ring ─────────

    private void ShowProgress(JobViewModel job)
    {
        var donut = _window!.DonutControl;
        _dropped = true;
        _progressJob = job;
        _lastRingJob = job;
        donut.BeginProgress();
        donut.SetProgress(job.Progress / 100);
        job.PropertyChanged += OnProgressJobChanged;
        _handOffTimer = DispatcherTimer.RunOnce(() =>
        {
            if (_progressJob == job && job.IsRunning)
                HandOffToToast();
        }, HandOffAfter);
    }

    private void OnProgressJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not JobViewModel job || job != _progressJob || _window is null)
            return;
        var donut = _window.DonutControl;
        donut.SetProgress(job.Progress / 100);
        if (job.IsRunning || e.PropertyName != nameof(JobViewModel.IsRunning))
            return;

        var outcome = job.Job.State switch
        {
            JobState.Completed => DonutProgressOutcome.Succeeded,
            JobState.Cancelled => DonutProgressOutcome.Cancelled,
            _ => DonutProgressOutcome.Failed,
        };
        if (outcome == DonutProgressOutcome.Failed)
            conversions.ShowInToast(job); // the toast explains what went wrong
        DetachProgress();
        donut.CompleteProgress(outcome, HideWindow);
    }

    /// <summary>Hands the running job to the toast with a short "shrink away" of the ring.</summary>
    private void HandOffToToast()
    {
        if (_progressJob is null || _window is null)
            return;
        conversions.ShowInToast(_progressJob);
        DetachProgress();
        _window.DonutControl.HandOffProgress(HideWindow);
    }

    /// <summary>A new gesture needs the window right now: move the job to the toast without animation.</summary>
    private void ReleaseProgressToToast()
    {
        if (_progressJob is not null)
            conversions.ShowInToast(_progressJob);
        DetachProgress();
    }

    private void DetachProgress()
    {
        _handOffTimer?.Dispose();
        _handOffTimer = null;
        if (_progressJob is not null)
            _progressJob.PropertyChanged -= OnProgressJobChanged;
        _progressJob = null;
    }

    private void HideWindow()
    {
        _visible = false;
        _window?.DonutControl.CollapseGroup();
        _window?.Hide();
    }

    /// <summary>Edits the preset of slice <paramref name="index"/>, or of slice <paramref name="sub"/> of its group's ring.</summary>
    private async Task EditPresetAsync(int index, int sub = -1)
    {
        if (_window is null || viewModel.PresetShownAt(index, sub) is not { } preset)
            return;
        _editing = true;
        try
        {
            if (await windows.EditPresetAsync(preset, _window))
            {
                store.SaveLibrary();
                viewModel.Load(viewModel.Files, viewModel.IsClickMode);
            }
        }
        finally
        {
            _editing = false;
        }
        if (_visible)
            _window.Activate();
    }

    /// <summary>
    /// Top-left corner of a <paramref name="sidePx"/> square window centred on <paramref name="cursor"/>, moved into
    /// <paramref name="area"/> when that would cross its edge. With group slices the window holds the ring they fan out
    /// (<see cref="DonutMenu.SideFor"/>), so near a screen edge the donut moves in far enough for every ring to fit.
    /// </summary>
    internal static PixelPoint WindowPosition(PixelPoint cursor, PixelRect? area, int sidePx)
    {
        var position = new PixelPoint(cursor.X - sidePx / 2, cursor.Y - sidePx / 2);
        if (area is not { } a)
            return position;
        return new PixelPoint(
            Math.Clamp(position.X, a.X, Math.Max(a.X, a.Right - sidePx)),
            Math.Clamp(position.Y, a.Y, Math.Max(a.Y, a.Bottom - sidePx)));
    }

    private void PlaceAndShow(RadialWindow window, int x, int y, bool activate)
    {
        var cursor = new PixelPoint(x, y);
        var screen = window.Screens.ScreenFromPoint(cursor) ?? window.Screens.Primary;
        var scaling = OperatingSystem.IsMacOS() ? 1 : screen?.Scaling ?? 1;

        // Window size follows the donut size (SizeToContent); compute it up front to centre on the cursor.
        var side = DonutMenu.SideFor(viewModel.OuterRadius, viewModel.ReserveSubRing);
        var sidePx = (int)Math.Ceiling(side * scaling);
        var position = WindowPosition(cursor, screen?.WorkingArea, sidePx);

        // Mandarina: the screen under the wheel, blurred, before the window covers it (the frosted ring).
        if (!window.IsVisible)
            viewModel.Backdrop = Backdrop.Capture(platform, new PixelRect(position, new PixelSize(sidePx, sidePx)), BackdropBlur * scaling);

        Motion.Refresh();
        window.Position = position;
        if (!window.IsVisible)
            window.Show();
        window.Position = position; // again, in case showing changed the DPI scale
        // Showing a window that is already marked topmost does not move it in the z-order, and Windows can push it
        // below normal windows while it keeps the mark (after the Photos app opened, for one). Then Explorer stays on
        // top of the donut and the drop never reaches it, so put it back on top every time.
        if (window.TryGetPlatformHandle()?.Handle is { } handle)
            platform.RaiseTopmost(handle);
        _visible = true;
        if (activate)
            window.Activate();
    }

    private static bool SameFiles(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.SequenceEqual(b, FileSystemPaths.Comparer);
}
