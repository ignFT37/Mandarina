using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Filee.App.Mandarina.Tools;

public partial class OrganizePdfView : UserControl
{
    /// <summary>How far the pointer must travel after pressing a page before it counts as a drag.</summary>
    private const double DragStart = 6;

    /// <summary>Distance from the top or bottom edge of the grid at which dragging scrolls it.</summary>
    private const double ScrollZone = 36;

    private static readonly Cursor DragCursor = new(StandardCursorType.DragMove);

    private readonly DispatcherTimer _autoScroll;
    private OrganizePage? _pressed;
    private OrganizePage? _collapseTo;
    private Point _pressedAt;
    private Point _pointer;
    private bool _canDrag;
    private bool _dragging;
    private int _slot = -1;

    public OrganizePdfView()
    {
        InitializeComponent();
        // Not the constructor that takes the callback: that one starts the timer at once, and a running timer is held by the
        // dispatcher for ever, with this view, its window and its panel (and 25 ticks a second for nothing).
        _autoScroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _autoScroll.Tick += (_, _) => OnAutoScroll();
        DetachedFromVisualTree += (_, _) => _autoScroll.Stop(); // closed in the middle of a drag
        KeyDown += OnViewKeyDown;
        PageScroll.PointerPressed += OnPagePressed;
        PageScroll.PointerMoved += OnPageMoved;
        PageScroll.PointerReleased += OnPageReleased;
        PageScroll.PointerCaptureLost += OnPageCaptureLost;
        PageScroll.ScrollChanged += (_, _) => ReportVisiblePages();
        Loaded += (_, _) =>
        {
            Focus(); // the keys (Del, Ctrl+A, Alt+arrows, Ctrl+Z) work from the start
            ReportVisiblePages();
        };
    }

    private OrganizePdfPanel? Model => DataContext as OrganizePdfPanel;

    // ───────── Buttons ─────────

    private void OnRotateLeft(object? sender, RoutedEventArgs e) => Model?.RotateLeft();

    private void OnRotateRight(object? sender, RoutedEventArgs e) => Model?.RotateRight();

    private void OnDuplicate(object? sender, RoutedEventArgs e) => Model?.Duplicate();

    private void OnRemove(object? sender, RoutedEventArgs e) => Model?.Remove();

    private void OnMoveBack(object? sender, RoutedEventArgs e) => Model?.MoveBack();

    private void OnMoveForward(object? sender, RoutedEventArgs e) => Model?.MoveForward();

    private void OnSelectAll(object? sender, RoutedEventArgs e) => Model?.SelectAll();

    private void OnUndo(object? sender, RoutedEventArgs e) => Model?.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => Model?.Redo();

    private void OnReset(object? sender, RoutedEventArgs e) => Model?.Reset();

    // ───────── Keys ─────────

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { IsIdle: true } model || e.Handled)
            return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Delete when !ctrl && !alt:
                model.Remove();
                break;
            case Key.A when ctrl:
                model.SelectAll();
                break;
            case Key.Z when ctrl && shift:
                model.Redo();
                break;
            case Key.Z when ctrl:
                model.Undo();
                break;
            case Key.Y when ctrl:
                model.Redo();
                break;
            case Key.Left or Key.Up when alt:
                model.MoveBack();
                break;
            case Key.Right or Key.Down when alt:
                model.MoveForward();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ───────── Selecting and dragging pages ─────────

    private void OnPagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is not { IsIdle: true } model || !e.GetCurrentPoint(PageScroll).Properties.IsLeftButtonPressed || IsOverScrollBar(e.Source))
            return;
        Focus();
        _pressed = _collapseTo = null;
        if (PageAt(e.Source) is not { } page)
        {
            model.ClearSelection(); // a click on the empty part of the grid
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl)
            model.Toggle(page);
        else if (shift)
            model.SelectRange(page);
        else if (!page.IsSelected)
            model.SelectOnly(page);
        else
            _collapseTo = page; // pressing a page of a selection keeps it, in case this is the start of a drag

        _pressed = page;
        _canDrag = !ctrl && !shift;
        _pressedAt = e.GetPosition(PageScroll);
        e.Pointer.Capture(PageScroll);
    }

    private void OnPageMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || Model is not { } model)
            return;
        _pointer = e.GetPosition(PageScroll);
        if (!_dragging)
        {
            var (dx, dy) = (_pointer.X - _pressedAt.X, _pointer.Y - _pressedAt.Y);
            if (!_canDrag || dx * dx + dy * dy < DragStart * DragStart)
                return;
            _dragging = true;
            _collapseTo = null;
            model.SetDragging(true);
            PageScroll.Cursor = DragCursor;
            _autoScroll.Start();
        }
        UpdateDrop();
    }

    private void OnPageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is null)
            return;
        var dragged = _dragging;
        var drop = _slot;
        var narrow = _collapseTo;
        EndGesture();
        e.Pointer.Capture(null);
        if (Model is not { } model)
            return;
        if (dragged)
        {
            if (drop >= 0)
                model.MoveSelectionTo(drop);
        }
        else if (narrow is not null)
        {
            model.SelectOnly(narrow);
        }
    }

    /// <summary>The pointer was taken away (the window lost focus, …): the gesture is over and nothing moves.</summary>
    private void OnPageCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndGesture();

    private void EndGesture()
    {
        if (_dragging)
            Model?.SetDragging(false);
        _pressed = _collapseTo = null;
        _dragging = false;
        _slot = -1;
        DropMarker.IsVisible = false;
        PageScroll.Cursor = null;
        _autoScroll.Stop();
    }

    /// <summary>Works out the gap the dragged pages would drop into and shows the bar there.</summary>
    private void UpdateDrop()
    {
        _slot = -1;
        DropMarker.IsVisible = false;
        if (PageList.ItemsPanelRoot is not { Children.Count: > 0 } host || PageScroll.TranslatePoint(_pointer, host) is not { } point)
            return;

        // The cell nearest to the pointer; before or after it according to the side of the pointer.
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < host.Children.Count; i++)
        {
            var bounds = host.Children[i].Bounds;
            var dx = Math.Max(Math.Max(bounds.Left - point.X, 0), point.X - bounds.Right);
            var dy = Math.Max(Math.Max(bounds.Top - point.Y, 0), point.Y - bounds.Bottom);
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        var target = host.Children[best].Bounds;
        var after = point.X > target.Center.X;
        _slot = best + (after ? 1 : 0);

        if (host.TranslatePoint(new Point(after ? target.Right : target.Left, target.Top), PageCanvas) is { } edge)
        {
            DropMarker.Height = target.Height;
            DropMarker.Margin = new Thickness(edge.X - DropMarker.Width / 2, edge.Y, 0, 0);
            DropMarker.IsVisible = true;
        }
    }

    /// <summary>While a page is dragged near the top or bottom of the grid, the grid scrolls that way.</summary>
    private void OnAutoScroll()
    {
        if (!_dragging)
            return;
        var height = PageScroll.Viewport.Height;
        var step = 0.0;
        if (_pointer.Y < ScrollZone)
            step = -(ScrollZone - _pointer.Y) * 0.5 - 2;
        else if (_pointer.Y > height - ScrollZone)
            step = (_pointer.Y - (height - ScrollZone)) * 0.5 + 2;
        if (step == 0)
            return;
        var most = Math.Max(0, PageScroll.Extent.Height - height);
        PageScroll.Offset = new Vector(PageScroll.Offset.X, Math.Clamp(PageScroll.Offset.Y + Math.Clamp(step, -24, 24), 0, most));
        UpdateDrop();
    }

    /// <summary>The cell (a Border of class pcell) that <paramref name="source"/> is in or is, and the page it shows.</summary>
    private static OrganizePage? PageAt(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border border && border.Classes.Contains("pcell"))
                return border.DataContext as OrganizePage;
        }
        return null;
    }

    private bool IsOverScrollBar(object? source)
    {
        for (var visual = source as Visual; visual is not null && visual != PageScroll; visual = visual.GetVisualParent())
        {
            if (visual is ScrollBar)
                return true;
        }
        return false;
    }

    // ───────── Which thumbnails matter now ─────────

    /// <summary>Tells the panel which pages are on screen (and a little beyond), so their thumbnails are drawn first.</summary>
    private void ReportVisiblePages()
    {
        if (Model is not { } model || PageList.ItemsPanelRoot is not { } host)
            return;
        var top = PageScroll.Offset.Y - 160;
        var bottom = PageScroll.Offset.Y + PageScroll.Viewport.Height + 160;
        var visible = new List<int>();
        foreach (var child in host.Children)
        {
            if (child.DataContext is OrganizePage page && child.TranslatePoint(default, PageCanvas) is { } at
                && at.Y + child.Bounds.Height >= top && at.Y <= bottom)
                visible.Add(page.Source);
        }
        model.PrioritizeThumbnails(visible);
    }
}
