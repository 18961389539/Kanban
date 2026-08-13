using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 取字符串首字符，用于用户头像占位字（侧边栏工业风方形头像）。
/// 空值/空白输入返回空字符串；多语言场景下仅取首字符，不做双字缩写。
/// </summary>
public class FirstCharacterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        return string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim()[0].ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
