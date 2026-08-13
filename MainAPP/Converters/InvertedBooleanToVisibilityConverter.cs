using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 布尔 → Visibility 反转：true → Collapsed，false/null → Visible。
/// 用于"无数据时显示空状态提示"等负逻辑场景。
/// </summary>
public class InvertedBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
