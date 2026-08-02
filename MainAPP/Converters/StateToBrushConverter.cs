using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Kanban.Core.Models;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 设备状态码转画刷颜色（参见 DeviceStatus 常量）
/// </summary>
public class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int state)
        {
            return state switch
            {
                (int)DeviceStatus.Running => FindBrush("StatusRunBrush"),
                (int)DeviceStatus.Alarm => FindBrush("StatusAlarmBrush"),
                (int)DeviceStatus.Paused => FindBrush("StatusPauseBrush"),
                _ => FindBrush("SecondaryBorderBrush")
            };
        }
        return FindBrush("SecondaryBorderBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static Brush FindBrush(string key)
    {
        // TryFindResource 在资源缺失时返回 null（不抛异常），FindResource 会抛 ResourceReferenceKeyNotFoundException。
        // 资源字典未完全加载或主题切换瞬间可能查不到，回退 Brushes.Gray 保证 UI 可用。
        return System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? Brushes.Gray;
    }
}
