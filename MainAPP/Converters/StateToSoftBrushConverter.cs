using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Kanban.Core.Models;

namespace MainAPP.Converters;

/// <summary>
/// 设备状态码转软色画刷（15% 透明度变体，用于状态药丸等浅色底）
/// </summary>
public class StateToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int state)
        {
            return state switch
            {
                (int)DeviceStatus.Running => FindBrush("StatusRunSoftBrush"),
                (int)DeviceStatus.Alarm => FindBrush("StatusAlarmSoftBrush"),
                (int)DeviceStatus.Paused => FindBrush("StatusPauseSoftBrush"),
                _ => FindBrush("StatusIdleSoftBrush")
            };
        }
        return FindBrush("StatusIdleSoftBrush");
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
