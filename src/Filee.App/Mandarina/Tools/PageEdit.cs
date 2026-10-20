// Mandarina: the editing model of the PDF ORGANIZE panel, apart from any view or PDF engine. The result is a list of
// PageEdit (which page of the original, how far it is turned); PageEditor changes that list (turn, duplicate, remove,
// move), knows which pages are selected and keeps the undo / redo history, so the panel and its tests drive it without a
// window.

using System.Collections.Immutable;

namespace Filee.App.Mandarina.Tools;

/// <summary>One page of the result.</summary>
/// <param name="Id">Tells pages apart while they are edited (a copy shares its <paramref name="Source"/>); the originals use their own index.</param>
/// <param name="Source">Zero-based page of the original PDF this page comes from.</param>
/// <param name="Turn">Degrees clockwise on top of the rotation the page already has in the original: 0, 90, 180 or 270.</param>
public sealed record PageEdit(int Id, int Source, int Turn);

public sealed class PageEditor
{
    /// <summary>What an undo step brings back: the pages, which of them were selected and which were moved by hand.</summary>
    private sealed record State(ImmutableArray<PageEdit> Pages, ImmutableHashSet<int> Selected, ImmutableHashSet<int> Moved);

    private readonly Stack<State> _undo = new();
    private readonly Stack<State> _redo = new();
    private State _state;
    private int _nextId;
    private int _anchor = -1;

    public PageEditor(int pageCount)
    {
        OriginalCount = Math.Max(0, pageCount);
        _nextId = OriginalCount;
        _state = new State(Untouched(), ImmutableHashSet<int>.Empty, ImmutableHashSet<int>.Empty);
    }

    /// <summary>Pages of the original PDF.</summary>
    public int OriginalCount { get; }

    /// <summary>The pages of the result, in order.</summary>
    public IReadOnlyList<PageEdit> Pages => _state.Pages;

    public int Count => _state.Pages.Length;

    /// <summary>True when the result differs from the original: other order, a turned, duplicated or missing page.</summary>
    public bool HasChanges
    {
        get
        {
            var pages = _state.Pages;
            if (pages.Length != OriginalCount)
                return true;
            for (var i = 0; i < pages.Length; i++)
            {
                if (pages[i].Source != i || pages[i].Turn != 0)
                    return true;
            }
            return false;
        }
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int SelectedCount => _state.Selected.Count;

    /// <summary>Raised after any change of the pages, the selection or the history.</summary>
    public event EventHandler? Changed;

    public bool IsSelected(int index) => (uint)index < (uint)Count && _state.Selected.Contains(_state.Pages[index].Id);

    /// <summary>Position of the page with this id, or -1.</summary>
    public int IndexOf(int id)
    {
        for (var i = 0; i < _state.Pages.Length; i++)
        {
            if (_state.Pages[i].Id == id)
                return i;
        }
        return -1;
    }

    // ───────── Selection (not part of the history) ─────────

    /// <summary>A plain click: only this page is selected. An index outside the list clears the selection.</summary>
    public void SelectOnly(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            ClearSelection();
            return;
        }
        var id = _state.Pages[index].Id;
        _anchor = id;
        SetSelection(ImmutableHashSet.Create(id));
    }

    /// <summary>Ctrl+click: adds the page to the selection or takes it out.</summary>
    public void Toggle(int index)
    {
        if ((uint)index >= (uint)Count)
            return;
        var id = _state.Pages[index].Id;
        _anchor = id;
        SetSelection(_state.Selected.Contains(id) ? _state.Selected.Remove(id) : _state.Selected.Add(id));
    }

    /// <summary>Shift+click: selects from the page last clicked to this one.</summary>
    public void SelectRange(int index)
    {
        if ((uint)index >= (uint)Count)
            return;
        var from = IndexOf(_anchor);
        if (from < 0)
        {
            SelectOnly(index);
            return;
        }
        var (low, high) = (Math.Min(from, index), Math.Max(from, index));
        SetSelection(_state.Pages.Skip(low).Take(high - low + 1).Select(page => page.Id).ToImmutableHashSet());
    }

    public void SelectAll()
    {
        if (Count > 0)
            _anchor = _state.Pages[0].Id;
        SetSelection(_state.Pages.Select(page => page.Id).ToImmutableHashSet());
    }

    public void ClearSelection() => SetSelection(ImmutableHashSet<int>.Empty);

    private void SetSelection(ImmutableHashSet<int> selected)
    {
        if (selected.SetEquals(_state.Selected))
            return;
        _state = _state with { Selected = selected };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ───────── Edits (each is one undo step; false when there was nothing to do) ─────────

    /// <summary>Turns the selected pages by <paramref name="quarterTurns"/> × 90° (positive clockwise, negative the other way).</summary>
    public bool Rotate(int quarterTurns)
    {
        var delta = (quarterTurns % 4 + 4) % 4 * 90;
        if (delta == 0 || _state.Selected.Count == 0)
            return false;
        Commit(_state with
        {
            Pages = _state.Pages
                .Select(page => _state.Selected.Contains(page.Id) ? page with { Turn = (page.Turn + delta) % 360 } : page)
                .ToImmutableArray(),
        });
        return true;
    }

    /// <summary>Puts a copy right behind each selected page and selects the copies.</summary>
    public bool Duplicate()
    {
        if (_state.Selected.Count == 0)
            return false;
        var pages = ImmutableArray.CreateBuilder<PageEdit>(Count + _state.Selected.Count);
        var copies = ImmutableHashSet.CreateBuilder<int>();
        var moved = _state.Moved.ToBuilder();
        foreach (var page in _state.Pages)
        {
            pages.Add(page);
            if (!_state.Selected.Contains(page.Id))
                continue;
            var copy = page with { Id = _nextId++ };
            pages.Add(copy);
            if (copies.Count == 0)
                _anchor = copy.Id;
            copies.Add(copy.Id);
            if (_state.Moved.Contains(page.Id))
                moved.Add(copy.Id);
        }
        Commit(new State(pages.ToImmutable(), copies.ToImmutable(), moved.ToImmutable()));
        return true;
    }

    /// <summary>Takes the selected pages out of the result.</summary>
    public bool Remove()
    {
        if (_state.Selected.Count == 0)
            return false;
        Commit(new State(_state.Pages.Where(page => !_state.Selected.Contains(page.Id)).ToImmutableArray(), ImmutableHashSet<int>.Empty, _state.Moved));
        return true;
    }

    /// <summary>
    /// Moves the selected pages one place back (<paramref name="delta"/> &lt; 0) or forward (&gt; 0): each swaps with the
    /// unselected page next to it, so a block moves together and pages already at the end stay there.
    /// </summary>
    public bool Move(int delta)
    {
        if (delta == 0 || _state.Selected.Count == 0)
            return false;
        var pages = _state.Pages.ToArray();
        var selected = pages.Select(page => _state.Selected.Contains(page.Id)).ToArray();
        var moved = _state.Moved.ToBuilder();
        var any = false;
        if (delta < 0)
        {
            for (var i = 1; i < pages.Length; i++)
            {
                if (selected[i] && !selected[i - 1])
                {
                    moved.Add(pages[i].Id);
                    Swap(pages, selected, i - 1, i);
                    any = true;
                }
            }
        }
        else
        {
            for (var i = pages.Length - 2; i >= 0; i--)
            {
                if (selected[i] && !selected[i + 1])
                {
                    moved.Add(pages[i].Id);
                    Swap(pages, selected, i, i + 1);
                    any = true;
                }
            }
        }
        if (!any)
            return false;
        Commit(_state with { Pages = pages.ToImmutableArray(), Moved = moved.ToImmutable() });
        return true;
    }

    /// <summary>
    /// Drag and drop: moves the selected pages, in their present order, to the gap before position
    /// <paramref name="slot"/> of the present list (0 = first, <see cref="Count"/> = last).
    /// </summary>
    public bool MoveTo(int slot)
    {
        if (_state.Selected.Count == 0)
            return false;
        slot = Math.Clamp(slot, 0, Count);
        var block = _state.Pages.Where(page => _state.Selected.Contains(page.Id)).ToList();
        var before = _state.Pages.Take(slot).Count(page => _state.Selected.Contains(page.Id));
        var result = _state.Pages.Where(page => !_state.Selected.Contains(page.Id)).ToList();
        result.InsertRange(slot - before, block);
        if (result.SequenceEqual(_state.Pages))
            return false;
        Commit(_state with { Pages = result.ToImmutableArray(), Moved = _state.Moved.Union(block.Select(page => page.Id)) });
        return true;
    }

    /// <summary>Back to the original PDF: every page, in its order, unturned.</summary>
    public bool Reset()
    {
        if (!HasChanges)
            return false;
        Commit(new State(Untouched(), ImmutableHashSet<int>.Empty, ImmutableHashSet<int>.Empty));
        return true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        _redo.Push(_state);
        _state = _undo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        _undo.Push(_state);
        _state = _redo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// For each page of the result: true when it should say where it came from, because it has a twin (the page was
    /// duplicated) or the user moved it and it is not in its place in the original order. Pages that merely shifted
    /// because others were removed or moved past them are not marked.
    /// </summary>
    public bool[] OriginMarks()
    {
        var pages = _state.Pages;
        var count = pages.Length;
        var marks = new bool[count];

        // The pages that stand where they belong are the longest run in the original order that keeps every page not
        // moved by hand (those never change places among themselves) and as many of the moved ones as fit. A page that
        // was not moved counts for more than all the moved ones together.
        var best = new int[count];
        var previous = new int[count];
        var end = -1;
        for (var i = 0; i < count; i++)
        {
            var weight = _state.Moved.Contains(pages[i].Id) ? 1 : count + 1;
            best[i] = weight;
            previous[i] = -1;
            for (var j = 0; j < i; j++)
            {
                if (pages[j].Source <= pages[i].Source && best[j] + weight > best[i])
                {
                    best[i] = best[j] + weight;
                    previous[i] = j;
                }
            }
            if (end < 0 || best[i] > best[end])
                end = i;
        }
        Array.Fill(marks, true);
        for (var k = end; k >= 0; k = previous[k])
            marks[k] = false;

        var seen = new HashSet<int>();
        var twins = new HashSet<int>();
        foreach (var page in pages)
        {
            if (!seen.Add(page.Source))
                twins.Add(page.Source);
        }
        for (var i = 0; i < count; i++)
        {
            if (twins.Contains(pages[i].Source))
                marks[i] = true;
        }
        return marks;
    }

    private void Commit(State next)
    {
        _undo.Push(_state);
        _redo.Clear();
        _state = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ImmutableArray<PageEdit> Untouched() =>
        Enumerable.Range(0, OriginalCount).Select(i => new PageEdit(i, i, 0)).ToImmutableArray();

    private static void Swap(PageEdit[] pages, bool[] selected, int a, int b)
    {
        (pages[a], pages[b]) = (pages[b], pages[a]);
        (selected[a], selected[b]) = (selected[b], selected[a]);
    }
}
