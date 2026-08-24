using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>布尔反转：true → false，false/null → true。用于 IsEnabled 等负逻辑绑定。</summary>
public class InvertedBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}
