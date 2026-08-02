using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// TimeSpan 与字符串 "HH:mm" 之间的双向转换。
/// </summary>
public class TimeSpanToTimeStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}";
        return "00:00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        if (TimeSpan.TryParse(text, out var ts))
            return ts;
        // 兼容 "HH:mm" 格式
        var parts = text.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
            return new TimeSpan(h, m, 0);
        return TimeSpan.Zero;
    }
}
