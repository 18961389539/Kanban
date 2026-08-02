using System.Globalization;
using System.Windows.Data;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 报警级别转中文文本：High=高, Medium=中, Low=低。用于列表项右侧级别徽标。
/// </summary>
public class AlarmLevelToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AlarmLevel level)
        {
            return level switch
            {
                AlarmLevel.High => "高",
                AlarmLevel.Medium => "中",
                AlarmLevel.Low => "低",
                _ => string.Empty,
            };
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
