using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Kanban.Core.Models;
using MainAPP.Models;

namespace MainAPP.Converters;

/// <summary>
/// 将设备与地址冲突摘要转换为标记可见性或提示文本。
/// </summary>
public sealed class DeviceAddressConflictConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not Device device || values[1] is not IReadOnlyDictionary<string, string> summaries)
            return parameter is "Text" ? string.Empty : Visibility.Collapsed;

        var hasConflict = summaries.TryGetValue(device.Id, out var summary);
        if (parameter is "Text")
            return hasConflict ? $"地址冲突：{summary}" : string.Empty;
        return hasConflict ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
