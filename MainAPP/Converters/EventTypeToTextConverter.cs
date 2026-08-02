using System.Globalization;
using System.Windows.Data;
using Kanban.Core.Entities;

namespace MainAPP.Converters;

/// <summary>
/// 报警事件类型转文本：1=触发, 2=恢复, 3=班次切换。
/// 主路径接收 <see cref="AlarmEventType"/> 枚举；保留 int 兼容旧绑定（如直接绑数据库原始字段）。
/// </summary>
public class EventTypeToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // AlarmEventType 枚举主路径
        if (value is AlarmEventType eventType)
        {
            return eventType switch
            {
                AlarmEventType.Triggered => "触发",
                AlarmEventType.Recovered => "恢复",
                AlarmEventType.ShiftChange => "班次切换",
                _ => "未知"
            };
        }

        // int 兜底（兼容旧绑定直接绑数据库原始字段）
        if (value is int i)
        {
            return i switch
            {
                1 => "触发",
                2 => "恢复",
                3 => "班次切换",
                _ => "未知"
            };
        }
        return "未知";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
