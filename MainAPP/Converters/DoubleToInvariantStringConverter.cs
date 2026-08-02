using System;
using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// double ↔ 不变字符串（InvariantCulture）互转。
/// 用于设置页“界面字号”ComboBox：SelectedValue 绑定 double 类型的 UiScale，
/// 而 ComboBoxItem.Tag 为字符串（"1" / "1.15" / "1.3"）。
/// 双向转换保证初始项能正确匹配选中（否则 1.0 与 "1" 不等导致初始无选中）。
/// </summary>
public class DoubleToInvariantStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d.ToString(CultureInfo.InvariantCulture);
        return "1";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return 1.0;
    }
}
