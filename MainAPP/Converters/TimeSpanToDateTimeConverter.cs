using System.Globalization;
using System.Windows.Data;

namespace MainAPP.Converters;

/// <summary>
/// TimeSpan（仅时分部分）与 DateTime? 之间的双向转换。
/// 用于 hc:DateTimePicker 绑定 TimeSpan 字段：UI 显示当天 + 该时刻，回写只保留时间部分。
/// 转换方向：
///   Convert: TimeSpan → DateTime?（今天 + 该时刻，Today.Add(timeSpan)）
///   ConvertBack: DateTime? → TimeSpan（取 TimeOfDay；null 返回 TimeSpan.Zero）
/// </summary>
public class TimeSpanToDateTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return DateTime.Today.Add(ts);
        return DateTime.Today;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
            return dt.TimeOfDay;
        return TimeSpan.Zero;
    }
}
