using System;
using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// UTC 时间戳 → 本地时间展示（"MM-dd HH:mm"）；null / MinValue 返回空串。
/// 用户最后登录时间等以 DateTime.UtcNow 持久化，直接 StringFormat 会按 UTC 原样显示，
/// 需转本地时区后再格式化。
/// </summary>
public sealed class UtcToLocalStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt || dt == default) return string.Empty;
        return dt.ToLocalTime().ToString("MM-dd HH:mm", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("UtcToLocalStringConverter does not support ConvertBack");
}
