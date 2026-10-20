// Mandarina: lays out the fields of the METADATA panel in three columns. Each field says how many columns it takes
// (MetadataField.Columns: the description and the title take all three); they flow from left to right and wrap.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Filee.App.Mandarina.Tools;

public sealed class MetadataFlowPanel : Panel
{
    private const int Columns = 3;
    private const double ColumnGap = 12;
    private const double RowGap = 4;

    private static int SpanOf(Control child)
    {
        var content = child is ContentPresenter presenter ? presenter.Content : child.DataContext;
        return content is MetadataField field ? Math.Clamp(field.Columns, 1, Columns) : 1;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        var column = (width - (Columns - 1) * ColumnGap) / Columns;
        double height = 0;
        double rowHeight = 0;
        var used = 0;
        foreach (var child in Children)
        {
            var span = SpanOf(child);
            if (used + span > Columns)
            {
                height += rowHeight + RowGap;
                rowHeight = 0;
                used = 0;
            }
            child.Measure(new Size(span * column + (span - 1) * ColumnGap, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            used += span;
        }
        return new Size(width, height + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var column = (finalSize.Width - (Columns - 1) * ColumnGap) / Columns;
        double y = 0;
        double rowHeight = 0;
        var used = 0;
        foreach (var child in Children)
        {
            var span = SpanOf(child);
            if (used + span > Columns)
            {
                y += rowHeight + RowGap;
                rowHeight = 0;
                used = 0;
            }
            var x = used * (column + ColumnGap);
            var width = span * column + (span - 1) * ColumnGap;
            child.Arrange(new Rect(x, y, width, child.DesiredSize.Height));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            used += span;
        }
        return finalSize;
    }
}
