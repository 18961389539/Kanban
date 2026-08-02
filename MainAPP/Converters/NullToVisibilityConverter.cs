using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// null 与 Visibility 互转。
/// 默认行为：非 null → Visible，null → Collapsed。
/// 传入 ConverterParameter="Invert" 反转：null → Visible，非 null → Collapsed。
/// 用于"空态提示 vs 详情面板"切换场景。
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isInvert = parameter is string s && s.Equals("Invert", System.StringComparison.OrdinalIgnoreCase);
        var isNotNull = value != null;
        // 反转：null 显示（用于空态提示）；默认：非 null 显示（用于详情面板）
        var shouldVisible = isInvert ? !isNotNull : isNotNull;
        return shouldVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
