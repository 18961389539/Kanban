using System;
using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// 用户时间戳 → 本地时间展示（"yyyy-MM-dd HH:mm"）；null / MinValue 返回空串。
/// 现网 users.json 可能仍存旧版 UTC 时间戳（Kind=Utc），ToLocalTime 对其正确转换；
/// 新版写入的本地时间（Kind=Local）经 ToLocalTime 为恒等转换，两种数据均可正确显示。
/// </summary>
public sealed class UtcToLocalStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt || dt == default) return string.Empty;
        return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("UtcToLocalStringConverter does not support ConvertBack");
}
