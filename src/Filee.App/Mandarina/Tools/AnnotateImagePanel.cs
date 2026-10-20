// Mandarina: ANNOTATE for one photo. Draw arrows, lines, rectangles, ellipses, freehand strokes, text and
// numbered callouts over the photo, move and resize them, undo anything; Apply saves "name annotated.png" beside the
// original (always a PNG; the original is never touched). The marks are placed in the original's pixels and drawn by
// AnnotationCanvas, on screen from a 1600 px preview and for the result from the full photo with the same code.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Mandarina.Panels;
using ImageMagick;

namespace Filee.App.Mandarina.Tools;

[ToolPanel("annotateImage")]
public sealed partial class AnnotateImagePanel : ToolPanel
{
    /// <summary>The saved picture's longer side is at most this many pixels (a bigger photo is saved scaled down).</summary>
    public const int MaxSide = 8000;

    private readonly CancellationTokenSource _loading = new();
    private bool _syncing;

    [ObservableProperty] private Bitmap? _photo;
    [ObservableProperty] private Size _imageSize;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private AnnotationTool _tool = AnnotationTool.Arrow;
    [ObservableProperty] private int _selectedId;
    [ObservableProperty] private Color _color = AnnotationPalette.Red;
    [ObservableProperty] private double _thickness = Annotation.DefaultThickness;
    [ObservableProperty] private bool _filled;
    [ObservableProperty] private double _fontSize = Annotation.DefaultFontSize;
    [ObservableProperty] private bool _hasPendingText;

    public AnnotateImagePanel(PanelContext context) : base(context)
    {
        Document.Changed += OnDocumentChanged;
        _ = LoadAsync();
    }

    public override Size PreferredSize => new(960, 700);

    /// <summary>The marks and their history.</summary>
    public AnnotationDocument Document { get; } = new();

    public bool CanUndo => Document.CanUndo;

    public bool CanRedo => Document.CanRedo;

    public bool HasSelection => SelectedId != 0;

    public bool HasMarks => Document.Count > 0;

    /// <summary>One line about what the current tool does.</summary>
    public string Hint => Context[$"panel.annotateImage.hint.{Tool.ToString().ToLowerInvariant()}"];

    // The style controls apply to the selected mark, or else to what the tool will draw; those that mean nothing for it are dimmed.
    private AnnotationKind? StyleKind => Document.Find(SelectedId)?.Kind ?? Annotation.KindOf(Tool);

    public bool ThicknessEnabled => StyleKind is not { } kind || Annotation.HasThickness(kind);

    public bool FillEnabled => StyleKind is not { } kind || Annotation.HasFill(kind);

    public bool FontSizeEnabled => StyleKind is not { } kind || Annotation.HasFontSize(kind);

    // A text still being typed counts: pressing Apply confirms it first, so the button must not look dead meanwhile.
    public override bool CanApply => !IsLoading && LoadError is null && (Document.Count > 0 || HasPendingText);

    public override bool HasUnsavedWork => Document.Count > 0 || HasPendingText;

    /// <summary>Raised when Apply starts: the view confirms the text being typed so it is saved too.</summary>
    public event EventHandler? FlushRequested;

    public override Control CreateView() => new AnnotateImageView { DataContext = this };

    private async Task LoadAsync()
    {
        try
        {
            var preview = await PanelImages.LoadAsync(Files[0], 1600, _loading.Token);
            ImageSize = new Size(preview.Width, preview.Height);
            Photo = preview.Bitmap;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "opening" for ever.
            LoadError = Context.Explain(ex);
        }
        IsLoading = false;
    }

    // ───────── Tool, selection and style ─────────

    partial void OnToolChanged(AnnotationTool value)
    {
        if (value != AnnotationTool.Select)
            SelectedId = 0;
        OnPropertyChanged(nameof(Hint));
        RaiseStyleState();
    }

    partial void OnSelectedIdChanged(int value)
    {
        SyncStyleFromSelection();
        OnPropertyChanged(nameof(HasSelection));
        RaiseStyleState();
    }

    partial void OnColorChanged(Color value) => Restyle(mark => mark with { Color = value }, "color");

    partial void OnThicknessChanged(double value) => Restyle(mark => mark.UsesThickness ? mark with { Thickness = value } : mark, "thickness");

    partial void OnFilledChanged(bool value) => Restyle(mark => mark.UsesFill ? mark with { Filled = value } : mark, "fill");

    partial void OnFontSizeChanged(double value) => Restyle(mark => mark.UsesFontSize ? mark with { FontSize = value } : mark, "size");

    partial void OnIsLoadingChanged(bool value) => NotifyCanApplyChanged();

    partial void OnLoadErrorChanged(string? value) => NotifyCanApplyChanged();

    partial void OnHasPendingTextChanged(bool value) => NotifyCanApplyChanged();

    private void RaiseStyleState()
    {
        OnPropertyChanged(nameof(ThicknessEnabled));
        OnPropertyChanged(nameof(FillEnabled));
        OnPropertyChanged(nameof(FontSizeEnabled));
    }

    /// <summary>A style control moved: the selected mark follows. Dragging a slider is one undo step.</summary>
    private void Restyle(Func<Annotation, Annotation> edit, string key)
    {
        if (_syncing || SelectedId == 0 || Document.Find(SelectedId) is not { } current)
            return;
        Document.Replace(edit(current), $"{key}:{current.Id}");
    }

    /// <summary>Selecting a mark (or undoing a style change) shows its style in the controls.</summary>
    private void SyncStyleFromSelection()
    {
        if (Document.Find(SelectedId) is not { } mark)
            return;
        _syncing = true;
        try
        {
            Color = mark.Color;
            if (mark.UsesThickness)
                Thickness = mark.Thickness;
            if (mark.UsesFill)
                Filled = mark.Filled;
            if (mark.UsesFontSize)
                FontSize = mark.FontSize;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        NotifyCanApplyChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasMarks));
        if (SelectedId != 0 && Document.Find(SelectedId) is null)
            SelectedId = 0;
        else
            SyncStyleFromSelection();
    }

    // ───────── Actions of the buttons ─────────

    public void Undo() => Document.Undo();

    public void Redo() => Document.Redo();

    public void DeleteSelected()
    {
        if (SelectedId == 0)
            return;
        var id = SelectedId;
        SelectedId = 0;
        Document.Remove(id);
    }

    public void ClearAll()
    {
        SelectedId = 0;
        Document.Clear();
    }

    // ───────── Saving ─────────

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        FlushRequested?.Invoke(this, EventArgs.Empty);
        var marks = Document.Items;
        if (marks.IsEmpty)
            throw new PanelException(Context["panel.saved_none"]);
        var source = Files[0];
        var original = ImageSize;
        var output = Context.OutputFor(source, "annotateImage", ".png");
        try
        {
            progress.Report(0.05);
            // The photo at its own size (or scaled down to MaxSide): decoding is slow, so off the UI thread.
            var full = await Task.Run(() => ReadFull(source, original, cancel), cancel);
            progress.Report(0.4);
            RenderTargetBitmap drawn;
            try
            {
                // Drawing needs the UI thread; it is the same code the canvas runs on screen.
                drawn = AnnotationCanvas.RenderResult(full, original, marks);
            }
            finally
            {
                full.Dispose();   // a big photo is hundreds of MB: let go of it before encoding
            }
            using (drawn)
            {
                progress.Report(0.55);
                await Task.Run(() =>
                {
                    using var stream = File.Create(output);
                    drawn.Save(stream, new PngBitmapEncoderOptions());
                }, cancel);
            }
            progress.Report(1);
        }
        catch
        {
            MediaTools.TryDelete(output);
            throw;
        }
        return [output];
    }

    private Bitmap ReadFull(string path, Size expected, CancellationToken cancel)
    {
        using var image = PanelImages.Read(path);
        if (image.Width != (uint)expected.Width || image.Height != (uint)expected.Height)
            throw new PanelException(Context["panel.annotateImage.changed"]);
        cancel.ThrowIfCancellationRequested();
        var longer = Math.Max(image.Width, image.Height);
        if (longer > MaxSide)
        {
            var factor = (double)MaxSide / longer;
            image.Resize(new MagickGeometry((uint)Math.Round(image.Width * factor), (uint)Math.Round(image.Height * factor)) { IgnoreAspectRatio = true });
        }
        cancel.ThrowIfCancellationRequested();
        // The result is a PNG written by Avalonia, which carries no profile: a wide-gamut photo (Display P3 from an iPhone, in a
        // HEIC or a JPEG) has to be in sRGB by then, or every viewer would read its pixels as sRGB and show it washed out.
        ImageCompose.ToSrgb(image);
        return PanelImages.ToBitmap(image);
    }

    public override void Dispose()
    {
        _loading.Cancel();
        Document.Changed -= OnDocumentChanged;
        var photo = Photo;
        Photo = null;
        photo?.Dispose();
        base.Dispose();
    }
}
