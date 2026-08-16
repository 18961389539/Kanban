using System.Globalization;
using System.Windows.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// 报警级别转文本：High=高, Medium=中, Low=低（文案走多语言资源）。用于列表项右侧级别徽标。
/// </summary>
public class AlarmLevelToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AlarmLevel level)
        {
            return level switch
            {
                AlarmLevel.High => Strings.Level_High,
                AlarmLevel.Medium => Strings.Level_Medium,
                AlarmLevel.Low => Strings.Level_Low,
                _ => string.Empty,
            };
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
