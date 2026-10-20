using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Filee.App.Mandarina.Tools;

public partial class AnnotateImageView : UserControl
{
    private AnnotateImagePanel? _panel;
    private TopLevel? _window;

    public AnnotateImageView() => InitializeComponent();

    /// <summary>The canvas, for tests.</summary>
    internal AnnotationCanvas Surface => Board;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_panel is not null)
        {
            _panel.PropertyChanged -= OnPanelChanged;
            _panel.FlushRequested -= OnFlushRequested;
        }
        _panel = DataContext as AnnotateImagePanel;
        if (_panel is null)
            return;
        BuildSwatches(_panel);
        _panel.FlushRequested += OnFlushRequested;
        Board.TextPlaceholder = _panel.Context["panel.annotateImage.text_hint"];
        _panel.PropertyChanged += OnPanelChanged;
        ShowTool();
        ShowColor();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Any press in the window ends the text being typed first (the Apply button is not inside this view).
        _window = TopLevel.GetTopLevel(this);
        _window?.AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _window?.RemoveHandler(PointerPressedEvent, OnWindowPointerPressed);
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Board.IsEditing && !Board.IsFromEditor(e.Source))
            Board.CommitEditor();
    }

    private void OnFlushRequested(object? sender, EventArgs e) => Board.CommitEditor();

    private void OnPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnnotateImagePanel.Tool))
            ShowTool();
        else if (e.PropertyName == nameof(AnnotateImagePanel.Color))
            ShowColor();
    }

    private void BuildSwatches(AnnotateImagePanel panel)
    {
        Swatches.Children.Clear();
        foreach (var (name, color) in AnnotationPalette.All)
        {
            var swatch = new ToggleButton { Background = new SolidColorBrush(color), Tag = color };
            swatch.Classes.Add("swatch");
            ToolTip.SetTip(swatch, panel.Context[$"panel.annotateImage.color.{name}"]);
            swatch.Click += OnSwatchClick;
            Swatches.Children.Add(swatch);
        }
    }

    /// <summary>Exactly the current tool's button is down.</summary>
    private void ShowTool()
    {
        foreach (var button in ToolList.Children.OfType<ToggleButton>())
            button.IsChecked = Equals(button.Tag, _panel?.Tool);
    }

    private void ShowColor()
    {
        foreach (var swatch in Swatches.Children.OfType<ToggleButton>())
            swatch.IsChecked = swatch.Tag is Color color && color == _panel?.Color;
    }

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (_panel is not null && sender is ToggleButton { Tag: AnnotationTool tool })
            _panel.Tool = tool;
        ShowTool();   // pressing the tool in use must not lift it
    }

    private void OnSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (_panel is not null && sender is ToggleButton { Tag: Color color })
            _panel.Color = color;
        ShowColor();
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => _panel?.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => _panel?.Redo();

    private void OnDeleteSelected(object? sender, RoutedEventArgs e) => _panel?.DeleteSelected();

    private void OnClearAll(object? sender, RoutedEventArgs e) => _panel?.ClearAll();

    /// <summary>Ctrl+Z, Ctrl+Y, Delete and Escape work with the focus on any button or slider too, not only on the photo.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Source is not TextBox && Board.HandleKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }
}
