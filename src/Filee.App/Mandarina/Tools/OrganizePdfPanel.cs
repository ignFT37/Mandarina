// Mandarina: ORGANIZE for PDFs. A grid of the pages as thumbnails: select some (click, Ctrl+click,
// Shift+click), turn, duplicate, remove or move them, undo, and Apply saves "name organized.pdf" beside the original with
// the pages in the order chosen. The edits live in PageEditor (no view, no PDF); this panel keeps the cells in step with
// it, loads the PDF in the background and draws the thumbnails lazily (PageThumbnailLoader). Apply copies the pages with
// PdfSharp (PdfPageOrganizer).

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("organizePDF")]
public sealed partial class OrganizePdfPanel : ToolPanel
{
    /// <summary>Cells put in the grid per turn of the interface while a PDF opens.</summary>
    private const int FillBatch = 12;

    private readonly CancellationTokenSource _loading = new();
    private readonly Dictionary<int, Bitmap> _bitmaps = [];
    private PageEditor _editor = new(0);
    private double[] _aspects = [];
    private PageThumbnailLoader? _thumbnails;

    /// <summary>The panel is not ready: the PDF is being read or the grid is being filled. Nothing can be edited yet.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>The PDF is being read (the grid is still empty).</summary>
    [ObservableProperty] private bool _isReading = true;

    [ObservableProperty] private string? _loadError;

    public OrganizePdfPanel(PanelContext context) : base(context)
    {
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(780, 688);

    public string FileName => Path.GetFileName(Files[0]);

    /// <summary>The cells of the grid, in the order of the result.</summary>
    public ObservableCollection<OrganizePage> Items { get; } = [];

    public bool HasSelection => _editor.SelectedCount > 0;

    public bool HasPages => _editor.Count > 0;

    public bool CanUndo => _editor.CanUndo;

    public bool CanRedo => _editor.CanRedo;

    /// <summary>The result differs from the original (the Reset button).</summary>
    public bool HasChanges => _editor.HasChanges;

    /// <summary>Every page was removed: nothing to save until one is brought back.</summary>
    public bool NoPagesLeft => !IsLoading && LoadError is null && _editor.Count == 0;

    /// <summary>"12 pages → 10".</summary>
    public string CountText => IsLoading || LoadError is not null
        ? ""
        : Context.Format(_editor.OriginalCount == 1 ? "panel.organizePDF.count_one" : "panel.organizePDF.count", _editor.OriginalCount, _editor.Count);

    public override bool CanApply => !IsLoading && LoadError is null && _editor.Count > 0 && _editor.HasChanges;

    public override bool HasUnsavedWork => _editor.HasChanges;

    public override Control CreateView() => new OrganizePdfView { DataContext = this };

    // ───────── Loading ─────────

    private async Task LoadAsync()
    {
        try
        {
            var path = Files[0];
            var token = _loading.Token;
            var overview = await Task.Run(() => PdfPageOrganizer.Read(path, token), token);
            token.ThrowIfCancellationRequested();
            IsReading = false;
            await StartAsync(overview, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (PdfPasswordException)
        {
            LoadError = Context["panel.organizePDF.protected"];
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "loading" for ever.
            LoadError = Context.Format("panel.organizePDF.unreadable", ex.Message);
        }
        IsReading = false;
        IsLoading = false;
        NotifyEditorState();
    }

    /// <summary>
    /// Puts the cells in the grid a few at a time, so a PDF of hundreds of pages never keeps the window busy for long;
    /// the thumbnails start arriving at once and the first rows are on screen right away.
    /// </summary>
    private async Task StartAsync(PdfOverview overview, CancellationToken token)
    {
        if (overview.PageCount == 0)
        {
            LoadError = Context["panel.organizePDF.no_pages"];
            return;
        }
        _aspects = overview.Aspects;
        _editor = new PageEditor(overview.PageCount);
        _editor.Changed += (_, _) => OnEditorChanged();
        if (overview.CanRender)
            _thumbnails = new PageThumbnailLoader(overview.Bytes, overview.PageCount, OnThumbnail);
        for (var i = 0; i < overview.PageCount; i++)
        {
            var cell = NewCell(_editor.Pages[i]);
            cell.Order = i + 1;
            Items.Add(cell);
            if ((i + 1) % FillBatch == 0 && i + 1 < overview.PageCount)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                token.ThrowIfCancellationRequested();
            }
        }
    }

    private OrganizePage NewCell(PageEdit page) =>
        new(page.Id, page.Source, _aspects[page.Source], Context.Format("panel.organizePDF.origin", page.Source + 1))
        {
            Thumbnail = _bitmaps.GetValueOrDefault(page.Source),
        };

    private void OnThumbnail(int source, Bitmap? bitmap)
    {
        if (bitmap is null)
            return;
        _bitmaps[source] = bitmap;
        foreach (var cell in Items)
        {
            if (cell.Source == source)
                cell.Thumbnail = bitmap;
        }
    }

    /// <summary>The view tells which pages are on screen; those are drawn first.</summary>
    public void PrioritizeThumbnails(IReadOnlyList<int> sources) => _thumbnails?.Prioritize(sources);

    partial void OnIsLoadingChanged(bool value) => NotifyCanApplyChanged();

    partial void OnLoadErrorChanged(string? value) => NotifyCanApplyChanged();

    // ───────── Editing (the buttons, the keys, the mouse) ─────────

    public void SelectOnly(OrganizePage page) => WhenIdle(() => _editor.SelectOnly(Items.IndexOf(page)));

    public void Toggle(OrganizePage page) => WhenIdle(() => _editor.Toggle(Items.IndexOf(page)));

    public void SelectRange(OrganizePage page) => WhenIdle(() => _editor.SelectRange(Items.IndexOf(page)));

    public void SelectAll() => WhenIdle(_editor.SelectAll);

    public void ClearSelection() => WhenIdle(_editor.ClearSelection);

    public void RotateLeft() => WhenIdle(() => _editor.Rotate(-1));

    public void RotateRight() => WhenIdle(() => _editor.Rotate(1));

    public void Duplicate() => WhenIdle(() => _editor.Duplicate());

    public void Remove() => WhenIdle(() => _editor.Remove());

    public void MoveBack() => WhenIdle(() => _editor.Move(-1));

    public void MoveForward() => WhenIdle(() => _editor.Move(1));

    /// <summary>Drag and drop: the selected pages go to the gap before position <paramref name="slot"/> (0..number of pages).</summary>
    public void MoveSelectionTo(int slot) => WhenIdle(() => _editor.MoveTo(slot));

    public void Undo() => WhenIdle(() => _editor.Undo());

    public void Redo() => WhenIdle(() => _editor.Redo());

    public void Reset() => WhenIdle(() => _editor.Reset());

    /// <summary>Dims the selected cells while they are being dragged.</summary>
    public void SetDragging(bool dragging)
    {
        foreach (var cell in Items)
            cell.IsGhost = dragging && cell.IsSelected;
    }

    private void WhenIdle(Action edit)
    {
        if (IsIdle && !IsLoading && LoadError is null)
            edit();
    }

    private void OnEditorChanged()
    {
        SyncCells();
        NotifyEditorState();
    }

    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(NoPagesLeft));
        OnPropertyChanged(nameof(CountText));
        NotifyCanApplyChanged();
    }

    /// <summary>Brings the cells in line with the editor: takes out the removed pages, puts in the copies, moves the rest.</summary>
    private void SyncCells()
    {
        var pages = _editor.Pages;
        var kept = pages.Select(page => page.Id).ToHashSet();
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(Items[i].Id))
                Items.RemoveAt(i);
        }
        for (var i = 0; i < pages.Count; i++)
        {
            if (i < Items.Count && Items[i].Id == pages[i].Id)
                continue;
            var at = -1;
            for (var j = i + 1; j < Items.Count && at < 0; j++)
            {
                if (Items[j].Id == pages[i].Id)
                    at = j;
            }
            if (at >= 0)
                Items.Move(at, i);
            else
                Items.Insert(i, NewCell(pages[i]));
        }
        RefreshCells();
    }

    /// <summary>Order, turn, selection and origin note of every cell.</summary>
    private void RefreshCells()
    {
        var pages = _editor.Pages;
        var marks = _editor.OriginMarks();
        for (var i = 0; i < pages.Count; i++)
        {
            var cell = Items[i];
            cell.Order = i + 1;
            cell.Turn = pages[i].Turn;
            cell.IsSelected = _editor.IsSelected(i);
            cell.ShowOrigin = marks[i];
        }
    }

    // ───────── Apply ─────────

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var source = Files[0];
        var pages = _editor.Pages.ToArray();
        var original = _editor.OriginalCount;
        var output = Context.OutputFor(source, "organizePDF", Path.GetExtension(source).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? null : ".pdf");
        try
        {
            await Task.Run(() => PdfPageOrganizer.Write(source, output, pages, original, progress, cancel), cancel);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or PanelException))
        {
            // PdfSharp's own exceptions are not ones the window knows how to show; say what happened in plain words.
            throw new PanelException(ex is PdfChangedException ? Context["panel.organizePDF.changed"] : ex.Message, ex);
        }
        return [output];
    }

    public override void Dispose()
    {
        _loading.Cancel();
        _thumbnails?.Dispose();
        // The pictures are native memory (a 300-page PDF has ~40 MB of them): free it now, not when the garbage collector
        // gets round to the finalizers.
        foreach (var cell in Items)
            cell.Thumbnail = null;
        foreach (var bitmap in _bitmaps.Values)
            bitmap.Dispose();
        _bitmaps.Clear();
        base.Dispose();
    }
}
