using System.Windows;
using System.Windows.Controls;

namespace GptPlusManager.Wpf.Infrastructure;

/// <summary>
/// A variable-height responsive grid. It fills the viewport width and switches between
/// one and two columns without relying on fixed item widths or an infinite ScrollViewer measure.
/// </summary>
public sealed class ResponsiveGridPanel : Panel
{
    public static readonly DependencyProperty BreakpointProperty = DependencyProperty.Register(
        nameof(Breakpoint), typeof(double), typeof(ResponsiveGridPanel),
        new FrameworkPropertyMetadata(1120d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveGridPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Breakpoint { get => (double)GetValue(BreakpointProperty); set => SetValue(BreakpointProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? Math.Max(ActualWidth, 720)
            : availableSize.Width;
        var columns = width < Breakpoint ? 1 : 2;
        var itemWidth = Math.Max(360, (width - Gap * (columns - 1)) / columns);
        var totalHeight = 0d;

        for (var row = 0; row * columns < InternalChildren.Count; row++)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= InternalChildren.Count) break;
                var child = InternalChildren[index];
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            totalHeight += rowHeight;
            if ((row + 1) * columns < InternalChildren.Count) totalHeight += Gap;
        }
        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = finalSize.Width < Breakpoint ? 1 : 2;
        var itemWidth = Math.Max(360, (finalSize.Width - Gap * (columns - 1)) / columns);
        var y = 0d;

        for (var row = 0; row * columns < InternalChildren.Count; row++)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= InternalChildren.Count) break;
                rowHeight = Math.Max(rowHeight, InternalChildren[index].DesiredSize.Height);
            }
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= InternalChildren.Count) break;
                var x = column * (itemWidth + Gap);
                InternalChildren[index].Arrange(new Rect(x, y, itemWidth, rowHeight));
            }
            y += rowHeight + Gap;
        }
        return finalSize;
    }
}
