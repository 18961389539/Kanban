using System;
using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 相等比较 Converter，同时实现 <see cref="IValueConverter"/> 和 <see cref="IMultiValueConverter"/>。
/// <para>单值用法（IValueConverter）：比较绑定值与 <c>ConverterParameter</c>，相等返回 true。
/// 用于 RadioButton/CheckBox 的 IsChecked 绑定，如工单状态筛选 chip。</para>
/// <para>多值用法（IMultiValueConverter）：<c>values[0] == values[1]</c> 时返回 true。
/// 用于设备卡片选中态：当卡片对应设备 Id 等于当前选中设备 Id 时高亮。</para>
/// </summary>
public class EqualityConverter : IValueConverter, IMultiValueConverter
{
    /// <summary>单值转换：绑定值与 ConverterParameter 相等时返回 true。</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null && parameter == null) return true;
        if (value == null || parameter == null) return false;
        return Equals(value, parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return false;

        var a = values[0];
        var b = values[1];

        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return Equals(a, b);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
