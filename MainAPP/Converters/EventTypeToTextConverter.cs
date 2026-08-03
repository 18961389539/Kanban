using System.Globalization;
using System.Windows.Data;
using Kanban.Core.Entities;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// 报警事件类型转文本：1=触发, 2=恢复, 3=班次切换（文案走多语言资源）。
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
                AlarmEventType.Triggered => Strings.EventType_Triggered,
                AlarmEventType.Recovered => Strings.EventType_Recovered,
                AlarmEventType.ShiftChange => Strings.EventType_ShiftChange,
                _ => Strings.Status_Unknown
            };
        }

        // int 兜底（兼容旧绑定直接绑数据库原始字段）
        if (value is int i)
        {
            return i switch
            {
                1 => Strings.EventType_Triggered,
                2 => Strings.EventType_Recovered,
                3 => Strings.EventType_ShiftChange,
                _ => Strings.Status_Unknown
            };
        }
        return Strings.Status_Unknown;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
