using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GptPlusManager.Wpf.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var result = value is true;
        if (parameter is string text && text.Equals("Invert", StringComparison.OrdinalIgnoreCase)) result = !result;
        return result ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class PercentToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? $"{number:0}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// 依据可用宽度与断点在 1 / 2 列之间切换,并算出每个卡片的宽度。
/// </summary>
public sealed class ColumnWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double available) return 520d;
        var breakpoint = values[1] is double b ? b : 1120d;
        var columns = available < breakpoint ? 1 : 2;
        var gap = 16d;
        var width = (available - gap) / columns;
        return width < 360 ? 360d : width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
