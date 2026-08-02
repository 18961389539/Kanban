using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MainAPP.Converters;

public sealed class PageSelectionVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        return values.Length >= 2 && values[0] is int selectedIndex && values[1] is int pageIndex && selectedIndex == pageIndex
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}