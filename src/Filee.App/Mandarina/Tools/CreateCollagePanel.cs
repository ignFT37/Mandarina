// Mandarina: COLLAGE: two to thirty photos in one picture. Pick the arrangement (grid, row, column, featured), put the
// photos in order with the up / down buttons, then set the spacing, the rounded corners, the background colour, the canvas
// shape and the size. Every photo is cropped around its centre to fill its cell. Each photo is read once into a small
// working copy, so the live preview never touches the disk again; Apply re-reads the originals and writes ONE picture
// beside the first photo ("first collage.jpg", or .png when some photo has transparency). The cells come from
// CollageLayout, the picture work from ImageCompose.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Mandarina.Panels;
using ImageMagick;
using Rectangle = System.Drawing.Rectangle;

namespace Filee.App.Mandarina.Tools;

/// <summary>The shape of the canvas.</summary>
public enum CollageAspect
{
    /// <summary>Whatever suits the layout (see <see cref="CollageLayout.AutoRatio"/>).</summary>
    Auto,
    Square,
    Standard,
    Wide,
    Tall,
}

/// <summary>The longer side of the picture: 1080, 2048 or 3000 px.</summary>
public enum CollageSize
{
    Small,
    Medium,
    Large,
}

/// <summary>Everything the picture depends on, copied off the panel so a render can run on another thread.</summary>
internal sealed record CollageOptions(CollageStyle Layout, double Spacing, double Corners, string Background, CollageAspect Aspect, CollageSize Size)
{
    public int LongSide => Size switch
    {
        CollageSize.Small => 1080,
        CollageSize.Large => 3000,
        _ => 2048,
    };

    /// <summary>Canvas width / height, or null to let the layout decide.</summary>
    public double? Ratio => Aspect switch
    {
        CollageAspect.Square => 1,
        CollageAspect.Standard => 4.0 / 3,
        CollageAspect.Wide => 16.0 / 9,
        CollageAspect.Tall => 9.0 / 16,
        _ => null,
    };
}

/// <summary>One photo of the collage, as a row of the order list: thumbnail, name, up / down / remove.</summary>
public sealed partial class CollageItem : ObservableObject, IDisposable
{
    private readonly CreateCollagePanel _owner;
    private MagickImage? _work;
    private volatile bool _disposed;

    internal CollageItem(string path, CreateCollagePanel owner)
    {
        Path = path;
        _owner = owner;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>True once the photo has a transparent part (known when it has loaded); the collage is then saved as PNG.</summary>
    public bool HasTransparency { get; private set; }

    /// <summary>The working copy (upright, sRGB, at most 960 px) the preview is drawn from; null until loaded. Touch it only holding the panel's gate.</summary>
    internal MagickImage? Work => _work;

    [ObservableProperty] private Bitmap? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _isFailed;

    public bool IsPending => !IsLoaded && !IsFailed;

    internal void Attach(MagickImage work, Bitmap thumbnail, bool transparent)
    {
        if (_disposed)
        {
            work.Dispose();
            thumbnail.Dispose();
            return;
        }
        lock (_owner.Gate)
            _work = work;
        HasTransparency = transparent;
        Thumbnail = thumbnail;
        IsLoaded = true;
    }

    internal void RefreshMoves()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner.Move(this, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner.Move(this, 1);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    private bool CanMoveUp() => _owner.Items.IndexOf(this) > 0;

    private bool CanMoveDown() => _owner.Items.IndexOf(this) is var index && index >= 0 && index < _owner.Items.Count - 1;

    public void Dispose()
    {
        _disposed = true;
        MagickImage? work;
        lock (_owner.Gate)
        {
            work = _work;
            _work = null;
        }
        work?.Dispose();
        Thumbnail?.Dispose();
    }
}

[ToolPanel("createCollage")]
public sealed partial class CreateCollagePanel : ToolPanel
{
    /// <summary>The longer side of the live preview's canvas.</summary>
    private const int PreviewSide = 800;

    /// <summary>The working copy of each photo: large enough for the biggest preview cell.</summary>
    private const int WorkSide = 960;

    private const int ThumbnailSide = 128;

    /// <summary>The colour of a cell whose photo has not loaded (or can't be read).</summary>
    private const string PlaceholderColor = "#E6E0E8";

    private readonly CancellationTokenSource _loading = new();
    private readonly string[] _initialOrder;
    private CancellationTokenSource? _previewing;
    private int _requested;
    private int _settled;
    private volatile bool _disposed;

    [ObservableProperty] private CollageStyle _layout = CollageStyle.Grid;
    [ObservableProperty] private double _spacing = 12;
    [ObservableProperty] private double _corners = 10;
    [ObservableProperty] private string _backgroundColor = "#FFFFFF";
    [ObservableProperty] private CollageAspect _aspect = CollageAspect.Auto;
    [ObservableProperty] private CollageSize _size = CollageSize.Medium;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string? _previewError;
    [ObservableProperty] private bool _isLoading = true;

    public CreateCollagePanel(PanelContext context) : base(context)
    {
        foreach (var file in Files.Take(CollageLayout.MaxPhotos))
            Items.Add(new CollageItem(file, this));
        _initialOrder = [.. Items.Select(item => item.Path)];
        RefreshItems();
        Items.CollectionChanged += OnItemsChanged;
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(940, 624);

    /// <summary>The photos in collage order (the first is the large one of the featured layout, the top left cell otherwise).</summary>
    public ObservableCollection<CollageItem> Items { get; } = [];

    /// <summary>Guards the photos' working copies: a render clones them under it, removing or closing frees them under it.</summary>
    internal object Gate { get; } = new();

    /// <summary>True when the preview on screen shows the collage as it is now (none is waiting or being drawn); tests wait on it.</summary>
    public bool PreviewIsCurrent => _settled == _requested;

    public string PhotoCountText => Items.Count == 1 ? Context["panel.createCollage.count_one"] : Context.Format("panel.createCollage.count", Items.Count);

    /// <summary>What is wrong with the selection, if anything: too few photos, or some were left out.</summary>
    public string? Hint =>
        Items.Count < CollageLayout.MinPhotos ? Context["panel.createCollage.need_two"]
        : Files.Count > CollageLayout.MaxPhotos ? Context.Format("panel.createCollage.truncated", CollageLayout.MaxPhotos)
        : null;

    public override bool CanApply => Items.Count >= CollageLayout.MinPhotos && !IsLoading;

    /// <summary>The photos were reordered or some were taken out.</summary>
    public override bool HasUnsavedWork => !Items.Select(item => item.Path).SequenceEqual(_initialOrder);

    public override Control CreateView() => new CreateCollageView { DataContext = this };

    [RelayCommand]
    private void SetLayout(CollageStyle layout) => Layout = layout;

    [RelayCommand]
    private void SetAspect(CollageAspect aspect) => Aspect = aspect;

    [RelayCommand]
    private void SetSize(CollageSize size) => Size = size;

    // ───────── The photos ─────────

    /// <summary>Moves <paramref name="item"/> one place earlier (-1) or later (+1) in the collage.</summary>
    public void Move(CollageItem item, int step)
    {
        var from = Items.IndexOf(item);
        var to = from + step;
        if (from >= 0 && to >= 0 && to < Items.Count)
            Items.Move(from, to);
    }

    /// <summary>Takes <paramref name="item"/> out of the collage (the file itself is untouched).</summary>
    public void Remove(CollageItem item)
    {
        if (Items.Remove(item))
            item.Dispose();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshItems();
        RequestPreview();
    }

    /// <summary>Brings everything that depends on the list up to date.</summary>
    private void RefreshItems()
    {
        foreach (var item in Items)
            item.RefreshMoves();
        IsLoading = Items.Any(item => item.IsPending);
        OnPropertyChanged(nameof(PhotoCountText));
        OnPropertyChanged(nameof(Hint));
        NotifyCanApplyChanged();
    }

    private async Task LoadAsync()
    {
        using var slots = new SemaphoreSlim(3);
        await Task.WhenAll(Items.ToArray().Select(item => LoadItemAsync(item, slots)));
    }

    private async Task LoadItemAsync(CollageItem item, SemaphoreSlim slots)
    {
        try
        {
            await slots.WaitAsync(_loading.Token);
            try
            {
                var (work, thumbnail, transparent) = await Task.Run(() => LoadPhoto(item.Path), _loading.Token);
                item.Attach(work, thumbnail, transparent);
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Fire-and-forget: a photo that can't be read must show as such, or the panel would wait on it for ever.
            item.IsFailed = true;
        }
        RefreshItems();
        RequestPreview();
    }

    private static (MagickImage Work, Bitmap Thumbnail, bool Transparent) LoadPhoto(string path)
    {
        var work = PanelImages.Read(path);
        try
        {
            PanelImages.Fit(work, WorkSide);
            ImageCompose.ToSrgb(work);
            using var small = new MagickImage(work);
            PanelImages.Fit(small, ThumbnailSide);
            return (work, PanelImages.ToBitmap(small), ImageCompose.HasTransparency(work));
        }
        catch
        {
            work.Dispose();
            throw;
        }
    }

    // ───────── Live preview ─────────

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Layout) or nameof(Spacing) or nameof(Corners) or nameof(BackgroundColor) or nameof(Aspect) or nameof(Size))
            RequestPreview();
    }

    private CollageOptions Snapshot() => new(Layout, Spacing, Corners, BackgroundColor, Aspect, Size);

    /// <summary>Redraws the preview shortly: each call cancels the one before, so dragging a slider only draws where it rests.</summary>
    private void RequestPreview()
    {
        if (_disposed)
            return;
        _previewing?.Cancel();
        var cancel = (_previewing = new CancellationTokenSource()).Token;
        _ = ShowPreviewAsync(++_requested, Snapshot(), Items.ToArray(), cancel);
    }

    private async Task ShowPreviewAsync(int generation, CollageOptions options, CollageItem[] items, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(60, cancel);
            var bitmap = await Task.Run(() => DrawPreview(options, items, cancel), cancel);
            if (cancel.IsCancellationRequested || _disposed)
            {
                bitmap.Dispose();
                return;
            }
            var old = Preview;
            Preview = bitmap;
            PreviewError = null;
            _settled = generation;
            if (old is not null)
                Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Fire-and-forget: say what went wrong instead of leaving a stale preview with no explanation.
            PreviewError = Context.Explain(ex);
            _settled = generation;
        }
    }

    private Bitmap DrawPreview(CollageOptions options, CollageItem[] items, CancellationToken cancel)
    {
        var copies = new List<MagickImage?>(items.Length);
        try
        {
            lock (Gate)
            {
                foreach (var item in items)
                    copies.Add(item.Work is { } work ? new MagickImage(work) : null);
            }
            var (width, height) = CollageLayout.CanvasSize(options.Ratio ?? CollageLayout.AutoRatio(options.Layout, items.Length), PreviewSide);
            // Spacing and corners are pixels of the finished picture: scale them down with the canvas.
            var scale = (double)PreviewSide / options.LongSide;
            using var canvas = ImageCompose.Solid((uint)width, (uint)height, ImageCompose.ParseColor(options.Background));
            var cells = CollageLayout.Compute(items.Length, options.Layout, width, height, (int)Math.Round(options.Spacing * scale));
            for (var i = 0; i < cells.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var photo = copies[i] ?? ImageCompose.Solid((uint)cells[i].Width, (uint)cells[i].Height, ImageCompose.ParseColor(PlaceholderColor));
                copies[i] = null;
                DrawCell(canvas, photo, cells[i], options.Corners * scale);
            }
            return PanelImages.ToBitmap(canvas);
        }
        finally
        {
            foreach (var copy in copies)
                copy?.Dispose();
        }
    }

    // ───────── The picture ─────────

    /// <summary>Crops <paramref name="photo"/> around its centre to fill <paramref name="cell"/>, rounds it and lays it on <paramref name="canvas"/>. Disposes the photo.</summary>
    private static void DrawCell(MagickImage canvas, MagickImage photo, Rectangle cell, double radius)
    {
        using (photo)
        {
            ImageCompose.Resize(photo, (uint)cell.Width, (uint)cell.Height, ComposeFit.Cover);
            ImageCompose.ToSrgb(photo);
            ImageCompose.RoundCorners(photo, radius);
            canvas.Composite(photo, cell.X, cell.Y, CompositeOperator.Over);
        }
    }

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var paths = Items.Select(item => item.Path).ToArray();
        if (paths.Length < CollageLayout.MinPhotos)
            throw new PanelException(Context["panel.createCollage.need_two"]);
        var options = Snapshot();
        // A photo with transparency (a cut-out, a logo) is why the picture is a PNG; otherwise a JPG is far smaller.
        var output = Context.OutputFor(paths[0], "createCollage", Items.Any(item => item.HasTransparency) ? ".png" : ".jpg");
        try
        {
            await Task.Run(() => RenderFinal(paths, options, output, progress, cancel), cancel);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        progress.Report(1);
        return [output];
    }

    private void RenderFinal(string[] paths, CollageOptions options, string output, IProgress<double> progress, CancellationToken cancel)
    {
        var (width, height) = CollageLayout.CanvasSize(options.Ratio ?? CollageLayout.AutoRatio(options.Layout, paths.Length), options.LongSide);
        var cells = CollageLayout.Compute(paths.Length, options.Layout, width, height, (int)Math.Round(options.Spacing));
        using var canvas = ImageCompose.Solid((uint)width, (uint)height, ImageCompose.ParseColor(options.Background));
        for (var i = 0; i < paths.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            MagickImage photo;
            try
            {
                photo = PanelImages.Read(paths[i]);
            }
            catch (MagickException)
            {
                throw new PanelException(Context.Format("panel.createCollage.unreadable", Path.GetFileName(paths[i])));
            }
            DrawCell(canvas, photo, cells[i], options.Corners);
            progress.Report((i + 1.0) / (paths.Length + 1));
        }
        canvas.Alpha(AlphaOption.Off);
        PanelImages.Save(canvas, output);
    }

    public override void Dispose()
    {
        _disposed = true;
        _loading.Cancel();
        _previewing?.Cancel();
        foreach (var item in Items.ToArray())
            item.Dispose();
        Preview?.Dispose();
        base.Dispose();
    }
}
