// Mandarina: the Float action (keep files in a floating shelf, then drag them into another app).
// A small always-on-top shelf near where the wheel was. Drag a file out of it into any app; drag the shelf by its
// title and it sticks to the edge of the screen it is let go near (see ShelfDock), staying there as it grows; × closes it.
// Floating more files adds them to the shelf that is already open.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Filee.Core.Localization;

namespace Filee.App.Mandarina;

internal sealed class FloatShelfWindow : Window
{
    private static FloatShelfWindow? _open;
    private static readonly HashSet<string> Previewable = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".ico" };

    /// <summary>The transparent margin around the card, in DIP: room for its shadow (and what "flush with the edge" ignores).</summary>
    private const double CardMargin = 12;

    private readonly WrapPanel _items = new() { MaxWidth = 3 * 92 };
    private readonly List<string> _paths = [];
    private readonly DispatcherTimer _settle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private ShelfEdges _docked;
    private bool _grabbed;

    /// <summary>The shelf that is open, if any (tests and the "add to the open shelf" rule).</summary>
    internal static FloatShelfWindow? Current => _open;

    /// <summary>The edges the shelf is docked to.</summary>
    internal ShelfEdges Docked => _docked;

    /// <summary>Shows the shelf (or the one already open) with <paramref name="paths"/> on it.</summary>
    public static void Open(IReadOnlyList<string> paths, ILocalizer loc, PixelPoint near)
    {
        if (_open is null)
        {
            _open = new FloatShelfWindow(loc);
            _open.Closed += (_, _) => _open = null;
            _open.Position = new PixelPoint(near.X + 24, near.Y - 60);
            _open.Show();
        }
        foreach (var path in paths)
            _ = _open.AddAsync(path);
    }

    private FloatShelfWindow(ILocalizer loc)
    {
        Title = "Mandarina";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var title = new TextBlock
        {
            Text = loc["wheel.float.title"],
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x48, 0x40)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "×",
            FontSize = 14,
            Padding = new Thickness(7, 0, 7, 1),
            MinHeight = 0,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Close();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 0, 0, 8), Background = Brushes.Transparent };
        header.Children.Add(title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            // BeginMoveDrag returns at once (the system moves the window), so the end of the drag is seen as the position
            // settling: the shelf docks a moment after it stops moving. Until it has been grabbed once it never docks by itself.
            _grabbed = true;
            BeginMoveDrag(e);
        };
        PositionChanged += (_, _) =>
        {
            if (_grabbed)
            {
                _settle.Stop();
                _settle.Start();
            }
        };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            DockToNearEdges();
        };
        Resized += (_, _) => KeepDocked();
        Closed += (_, _) => _settle.Stop();

        Content = new Border
        {
            Margin = new Thickness(CardMargin),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xF8, 0xF2)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x5D, 0x66, 0x66)),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 6 18 0 #33000000"),
            Child = new StackPanel { Children = { header, _items } },
        };
    }

    /// <summary>The window where it is, in pixels, and the usable area of the screen it is on (null before it has a screen).</summary>
    private (PixelRect Window, PixelRect Area, double Scaling)? Placement()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
            return null;
        var size = PixelSize.FromSize(ClientSize, RenderScaling);
        return (new PixelRect(Position, size), screen.WorkingArea, RenderScaling);
    }

    /// <summary>Sticks the card to the edges of the screen it was let go near.</summary>
    internal void DockToNearEdges()
    {
        if (Placement() is not { } place)
            return;
        var (position, edges) = ShelfDock.Snap(place.Window, place.Area, place.Scaling, CardMargin);
        _docked = edges;
        if (position != Position)
            Position = position;
    }

    /// <summary>The shelf grew with a new file: it keeps to its edges (and out of the way of the taskbar).</summary>
    private void KeepDocked()
    {
        if (Placement() is not { } place)
            return;
        var position = ShelfDock.Keep(place.Window, place.Area, _docked, place.Scaling, CardMargin);
        if (position != Position)
            Position = position;
    }

    private async Task AddAsync(string path)
    {
        if (_paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;
        IStorageItem? item = Directory.Exists(path)
            ? await StorageProvider.TryGetFolderFromPathAsync(path)
            : await StorageProvider.TryGetFileFromPathAsync(path);
        if (item is null)
            return;
        _paths.Add(path);
        _items.Children.Add(Tile(path, item));
    }

    private Control Tile(string path, IStorageItem item)
    {
        var folder = Directory.Exists(path);
        Control picture = Preview(path) is { } bitmap
            ? new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
            : new TextBlock
            {
                Text = folder ? "📁" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                FontSize = folder ? 26 : 13,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0x5B, 0x28)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        var tile = new StackPanel
        {
            Width = 84,
            Margin = new Thickness(4),
            Background = Brushes.Transparent,
            Children =
            {
                new Border
                {
                    Width = 64,
                    Height = 64,
                    CornerRadius = new CornerRadius(10),
                    ClipToBounds = true,
                    Background = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0)),
                    Child = picture,
                },
                new TextBlock
                {
                    Text = Path.GetFileName(path.TrimEnd('\\', '/')),
                    FontSize = 11,
                    MaxWidth = 84,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0x2B, 0x29)),
                },
            },
        };
        ToolTip.SetTip(tile, path);
        tile.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
                return;
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(item));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        };
        return tile;
    }

    private static Bitmap? Preview(string path)
    {
        if (!Previewable.Contains(Path.GetExtension(path)))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 128, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
