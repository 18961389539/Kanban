using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Kanban.Core.Models;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 设备列表项状态指示：根据 Device + DeviceRuntime 映射，实时返回设备运行/报警/待机/初始状态���应的画刷或文本。
/// 用于在设备列表中为每一项显示状态色点（运行绿 / 报警红 / 待机黄 / 初始灰），并支持 ToolTip 文本。
/// MultiBinding 输入：values[0]=Device，values[1]=IDictionary&lt;string,DeviceRuntime&gt;（设备运行时映射）。
/// parameter 传 "Text" 时返回状态文本，否则返回状态画刷。
/// </summary>
public class DeviceRuntimeStatusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var device = values.Length > 0 ? values[0] as Device : null;
        var map = values.Length > 1 ? values[1] as IDictionary<string, DeviceRuntime> : null;

        int status = (int)DeviceStatus.Unknown;
        if (device != null && map != null && map.TryGetValue(device.Id, out var rt))
            status = rt.StatusWord;

        if (parameter as string == "Text")
        {
            return status switch
            {
                (int)DeviceStatus.Running => "运行",
                (int)DeviceStatus.Alarm => "报警",
                (int)DeviceStatus.Paused => "待机",
                _ => "初始",
            };
        }

        return status switch
        {
            (int)DeviceStatus.Running => FindBrush("SuccessBrush"),
            (int)DeviceStatus.Alarm => FindBrush("DangerBrush"),
            (int)DeviceStatus.Paused => FindBrush("WarningBrush"),
            _ => FindBrush("SecondaryBorderBrush"),
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new System.NotSupportedException();

    private static Brush FindBrush(string key)
    {
        var app = System.Windows.Application.Current;
        var brush = app?.TryFindResource(key) as Brush;
        return brush ?? Brushes.Gray;
    }
}
