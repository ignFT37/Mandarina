// Mandarina: the data of the ANNOTATE tool. An annotation is an immutable value in the ORIGINAL photo's pixels,
// so what the canvas shows scaled down and what the saved PNG shows at full size come from the same numbers. The
// document is the ordered list of them plus the history: every edit swaps in a new list, an undo step is just the list
// as it was before (cheap, because the annotations themselves are never copied).

using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;

namespace Filee.App.Mandarina.Tools;

/// <summary>What the pointer does on the photo.</summary>
public enum AnnotationTool { Select, Arrow, Line, Rectangle, Ellipse, Pen, Text, Callout }

/// <summary>What a mark is.</summary>
public enum AnnotationKind { Arrow, Line, Rectangle, Ellipse, Pen, Text, Callout }

/// <summary>The eight colours of the style card.</summary>
public static class AnnotationPalette
{
    public static readonly Color Red = Color.FromRgb(0xFF, 0x3B, 0x30);
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x52, 0x00);
    public static readonly Color Yellow = Color.FromRgb(0xFF, 0xCC, 0x00);
    public static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x59);
    public static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Violet = Color.FromRgb(0xAF, 0x52, 0xDE);
    public static readonly Color Black = Color.FromRgb(0x1C, 0x1C, 0x1E);
    public static readonly Color White = Colors.White;

    /// <summary>The swatches in order; <c>Name</c> completes the key <c>panel.annotateImage.color.&lt;name&gt;</c>.</summary>
    public static readonly IReadOnlyList<(string Name, Color Color)> All =
    [
        ("red", Red), ("orange", Orange), ("yellow", Yellow), ("green", Green),
        ("blue", Blue), ("violet", Violet), ("black", Black), ("white", White),
    ];
}

/// <summary>
/// One mark on the photo. Coordinates are pixels of the original photo. <see cref="Thickness"/> and
/// <see cref="FontSize"/> are in units of 1/1600 of the photo's longer side, so a mark looks the same on a phone photo
/// and on a thumbnail: thickness 4 is 4 px on a 1600 px photo and 10 px on a 4000 px one.
/// </summary>
public sealed record Annotation
{
    public const double DefaultThickness = 4;
    public const double DefaultFontSize = 32;

    /// <summary>Unique within a document; 0 while the mark is still being drawn.</summary>
    public int Id { get; init; }

    public AnnotationKind Kind { get; init; }

    /// <summary>
    /// Arrow and line: start and end (the arrow's head is at the end). Rectangle and ellipse: two opposite corners, in
    /// whatever order they were dragged. Pen: the stroke. Text: its top-left corner. Callout: its centre.
    /// </summary>
    public ImmutableArray<Point> Points { get; init; } = ImmutableArray<Point>.Empty;

    public Color Color { get; init; } = AnnotationPalette.Red;

    public double Thickness { get; init; } = DefaultThickness;

    /// <summary>Rectangle and ellipse: a see-through fill inside the outline.</summary>
    public bool Filled { get; init; }

    public string Text { get; init; } = "";

    /// <summary>Text size; for a callout it sets the size of the numbered disc.</summary>
    public double FontSize { get; init; } = DefaultFontSize;

    /// <summary>True for the kinds whose look the thickness slider sets (a text or a callout has no outline to thicken).</summary>
    public static bool HasThickness(AnnotationKind kind) =>
        kind is AnnotationKind.Arrow or AnnotationKind.Line or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Pen;

    public static bool HasFill(AnnotationKind kind) => kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse;

    public static bool HasFontSize(AnnotationKind kind) => kind is AnnotationKind.Text or AnnotationKind.Callout;

    public bool UsesThickness => HasThickness(Kind);

    public bool UsesFill => HasFill(Kind);

    public bool UsesFontSize => HasFontSize(Kind);

    /// <summary>The kind the tool draws (null for the selection tool).</summary>
    public static AnnotationKind? KindOf(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Arrow => AnnotationKind.Arrow,
        AnnotationTool.Line => AnnotationKind.Line,
        AnnotationTool.Rectangle => AnnotationKind.Rectangle,
        AnnotationTool.Ellipse => AnnotationKind.Ellipse,
        AnnotationTool.Pen => AnnotationKind.Pen,
        AnnotationTool.Text => AnnotationKind.Text,
        AnnotationTool.Callout => AnnotationKind.Callout,
        _ => null,
    };

    /// <summary>A rectangle or ellipse as a normal rectangle, whichever way it was dragged.</summary>
    public Rect Corners => Points.Length < 2 ? default : Normalize(Points[0], Points[1]);

    /// <summary>The spots that resize the mark (image coordinates): the two ends of a line, the four corners of a shape.</summary>
    public ImmutableArray<Point> HandlePoints()
    {
        switch (Kind)
        {
            case AnnotationKind.Arrow or AnnotationKind.Line when Points.Length >= 2:
                return [Points[0], Points[1]];
            case AnnotationKind.Rectangle or AnnotationKind.Ellipse when Points.Length >= 2:
                var box = Corners;
                return [box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft];
            default:
                return ImmutableArray<Point>.Empty;
        }
    }

    /// <summary>The mark with handle <paramref name="index"/> (see <see cref="HandlePoints"/>) dragged to <paramref name="to"/>.</summary>
    public Annotation WithHandle(int index, Point to)
    {
        switch (Kind)
        {
            case AnnotationKind.Arrow or AnnotationKind.Line when index is 0 or 1 && Points.Length >= 2:
                return this with { Points = Points.SetItem(index, to) };
            case AnnotationKind.Rectangle or AnnotationKind.Ellipse when index is >= 0 and < 4 && Points.Length >= 2:
                // The opposite corner stays where it is; the dragged one follows the pointer, even across it.
                return this with { Points = [HandlePoints()[(index + 2) % 4], to] };
            default:
                return this;
        }
    }

    public Annotation Translated(Vector delta) =>
        delta == default ? this : this with { Points = ImmutableArray.CreateRange(Points, point => point + delta) };

    /// <summary>The rectangle between two points, with the smaller coordinates first.</summary>
    public static Rect Normalize(Point a, Point b) =>
        new(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
}

/// <summary>
/// The marks on a photo, bottom to top, and the undo/redo history. Edits swap in a new immutable list; consecutive
/// edits of the same thing (a slider being dragged) pass the same merge key and share one history step.
/// </summary>
public sealed class AnnotationDocument
{
    private const int MaxHistory = 300;
    private const long MergeWindowMs = 1200;

    private readonly List<ImmutableArray<Annotation>> _undo = [];
    private readonly List<ImmutableArray<Annotation>> _redo = [];
    private ImmutableArray<Annotation> _items = ImmutableArray<Annotation>.Empty;
    private int _lastId;
    private string? _mergeKey;
    private long _mergedAt;

    /// <summary>Raised after every change, including undo and redo.</summary>
    public event EventHandler? Changed;

    public ImmutableArray<Annotation> Items => _items;

    public int Count => _items.Length;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>A document that starts with <paramref name="items"/> and an empty history.</summary>
    public static AnnotationDocument From(IEnumerable<Annotation> items)
    {
        var document = new AnnotationDocument { _items = [.. items] };
        document._lastId = document._items.Select(item => item.Id).DefaultIfEmpty(0).Max();
        return document;
    }

    public Annotation? Find(int id)
    {
        foreach (var item in _items)
            if (item.Id == id)
                return item;
        return null;
    }

    /// <summary>The 1-based number a callout shows: its place among the callouts, so deleting one renumbers the rest. 0 for any other mark.</summary>
    public int NumberOf(int id)
    {
        var number = 0;
        foreach (var item in _items)
        {
            if (item.Kind == AnnotationKind.Callout)
                number++;
            if (item.Id == id)
                return item.Kind == AnnotationKind.Callout ? number : 0;
        }
        return 0;
    }

    public int CalloutCount => _items.Count(item => item.Kind == AnnotationKind.Callout);

    /// <summary>Puts a new mark on top. Returns it with its id.</summary>
    public Annotation Add(Annotation draft)
    {
        var item = draft with { Id = ++_lastId };
        Remember();
        _items = _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    /// <summary>
    /// Swaps in the edited version of a mark (found by its id). Edits with the same non-null <paramref name="mergeKey"/>
    /// in quick succession are one history step.
    /// </summary>
    public void Replace(Annotation edited, string? mergeKey = null)
    {
        var index = IndexOf(edited.Id);
        if (index < 0 || _items[index] == edited)
            return;
        var now = Environment.TickCount64;
        var merge = mergeKey is not null && mergeKey == _mergeKey && now - _mergedAt <= MergeWindowMs && _undo.Count > 0;
        if (!merge)
            Remember();
        _mergeKey = mergeKey;
        _mergedAt = now;
        _items = _items.SetItem(index, edited);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(int id)
    {
        var index = IndexOf(id);
        if (index < 0)
            return;
        Remember();
        _items = _items.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_items.IsEmpty)
            return;
        Remember();
        _items = ImmutableArray<Annotation>.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        _redo.Add(_items);
        _items = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _mergeKey = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        _undo.Add(_items);
        _items = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _mergeKey = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private int IndexOf(int id)
    {
        for (var i = 0; i < _items.Length; i++)
            if (_items[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>A new edit: today's list becomes an undo step and whatever could be redone is gone.</summary>
    private void Remember()
    {
        _undo.Add(_items);
        if (_undo.Count > MaxHistory)
            _undo.RemoveAt(0);
        _redo.Clear();
        _mergeKey = null;
    }
}
