using System.Globalization;
using System.Windows.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Resources;
using MainAPP.ViewModels;

namespace MainAPP.Converters;

/// <summary>
/// 设备状态码转文本（参见 DeviceStatus 常量）；文案走多语言资源。
/// </summary>
public class StateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is StatusTransitionRecord rec)
            return HistoryQueryHelper.GetStateText(rec.CurrentState, rec.OfflineCause);
        if (value is int state)
            return HistoryQueryHelper.GetStateText(state);
        return Strings.Status_Unknown;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
