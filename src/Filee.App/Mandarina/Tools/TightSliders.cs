// Mandarina: Fluent's horizontal Slider keeps 15 px free above and below its track for tick marks, so every slider row is
// 50 px high even though these panels' sliders have no ticks. The Background and Collage panels hold up to six of them next
// to a preview, so their views call TightSliders.Apply on themselves: the free rows go away (and the views override
// SliderHorizontalHeight to 22) and a slider row is 22 px high.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

namespace Filee.App.Mandarina.Tools;

internal static class TightSliders
{
    /// <summary>Takes the tick-mark rows out of every Slider inside <paramref name="root"/>, as soon as each applies its template.</summary>
    public static void Apply(ILogical root)
    {
        foreach (var slider in root.GetLogicalDescendants().OfType<Slider>())
            slider.TemplateApplied += OnTemplateApplied;
    }

    private static void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<Grid>("HorizontalTemplate") is { RowDefinitions.Count: 3 } grid)
            grid.RowDefinitions[0].Height = grid.RowDefinitions[2].Height = new GridLength(0);
    }
}
