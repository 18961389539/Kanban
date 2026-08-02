using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 索引转Visibility转换器：当值等于ConverterParameter时显示，否则隐藏
/// </summary>
public class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string paramStr && int.TryParse(paramStr, out var param))
        {
            return index == param ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
