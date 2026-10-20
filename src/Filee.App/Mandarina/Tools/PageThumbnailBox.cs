// Mandarina: the picture of one page in the ORGANIZE grid: the page turned by the user's rotation, or a grey stand-in of
// the same shape until PDFium has drawn it. The box is always as tall as a slot, so the numbers under the pages line
// up, and as wide as the (turned) page needs; the page is centred in it.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Filee.App.Mandarina.Tools;

public sealed class PageThumbnailBox : Control
{
    /// <summary>Height of a slot; a page is fitted into a square of this side.</summary>
    public const double SlotHeight = 150;

    private const double MinimumSlotWidth = 44;
    private const double CornerRadius = 3;
    private const double DefaultAspect = 0.7071;

    private static readonly IBrush Paper = Brushes.White;
    private static readonly IBrush StandIn = new SolidColorBrush(Color.FromRgb(0xE4, 0xDD, 0xE8));
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromRgb(0xD3, 0xCB, 0xD8)), 1);
    private static readonly BoxShadows Shadow = new(new BoxShadow
    {
        OffsetX = 0,
        OffsetY = 1.5,
        Blur = 5,
        Color = Color.FromArgb(0x33, 0x30, 0x10, 0x40),
    });

    /// <summary>The page as the PDF shows it (null while it is being drawn).</summary>
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<PageThumbnailBox, IImage?>(nameof(Source));

    /// <summary>The user's rotation in degrees clockwise: 0, 90, 180 or 270.</summary>
    public static readonly StyledProperty<int> TurnProperty =
        AvaloniaProperty.Register<PageThumbnailBox, int>(nameof(Turn));

    /// <summary>Width ÷ height of the page before the turn.</summary>
    public static readonly StyledProperty<double> AspectProperty =
        AvaloniaProperty.Register<PageThumbnailBox, double>(nameof(Aspect), DefaultAspect);

    static PageThumbnailBox()
    {
        AffectsMeasure<PageThumbnailBox>(TurnProperty, AspectProperty);
        AffectsRender<PageThumbnailBox>(SourceProperty, TurnProperty, AspectProperty);
    }

    public PageThumbnailBox()
    {
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int Turn
    {
        get => GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    public double Aspect
    {
        get => GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    private int Quarters => (Turn / 90 % 4 + 4) % 4;

    /// <summary>The page as shown: it fits the square slot, then turns.</summary>
    private Size PageSize()
    {
        var aspect = Aspect is > 0.02 and < 50 ? Aspect : DefaultAspect;
        var (width, height) = aspect <= 1 ? (SlotHeight * aspect, SlotHeight) : (SlotHeight, SlotHeight / aspect);
        return Quarters % 2 == 1 ? new Size(height, width) : new Size(width, height);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(PageSize().Width, MinimumSlotWidth), SlotHeight);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = PageSize();
        var page = new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        var shape = new RoundedRect(page, CornerRadius);
        if (Source is not { } image)
        {
            context.DrawRectangle(StandIn, Edge, shape);
            return;
        }

        context.DrawRectangle(Paper, null, shape, Shadow);
        // The picture is the page as the PDF shows it: draw it upright in a box of its own shape, then turn that about
        // the middle of the slot's page.
        var (width, height) = Quarters % 2 == 1 ? (size.Height, size.Width) : (size.Width, size.Height);
        var turn = Matrix.CreateTranslation(-width / 2, -height / 2)
                   * Matrix.CreateRotation(Quarters * Math.PI / 2)
                   * Matrix.CreateTranslation(page.Center.X, page.Center.Y);
        using (context.PushClip(shape))
        using (context.PushTransform(turn))
            context.DrawImage(image, new Rect(0, 0, width, height));
        context.DrawRectangle(null, Edge, shape);
    }
}
