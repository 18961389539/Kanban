using System.Globalization;
using System.Windows.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// 设备状态码转文本（参见 DeviceStatus 常量）；文案走多语言资源。
/// </summary>
public class StateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int state)
        {
            return state switch
            {
                (int)DeviceStatus.Unknown => Strings.Status_Initial,
                (int)DeviceStatus.Running => Strings.Status_Running,
                (int)DeviceStatus.Alarm => Strings.Status_Alarm,
                (int)DeviceStatus.Paused => Strings.Status_Paused,
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
