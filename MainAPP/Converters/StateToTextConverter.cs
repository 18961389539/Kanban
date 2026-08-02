using System.Globalization;
using System.Windows.Data;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 设备状态码转文本（参见 DeviceStatus 常量）
/// </summary>
public class StateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int state)
        {
            return state switch
            {
                (int)DeviceStatus.Unknown => "初始",
                (int)DeviceStatus.Running => "运行",
                (int)DeviceStatus.Alarm => "报警",
                (int)DeviceStatus.Paused => "待机",
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
