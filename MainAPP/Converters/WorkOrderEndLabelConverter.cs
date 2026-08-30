using System;
using System.Globalization;
using System.Windows.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Resources;

namespace MainAPP.Converters;

/// <summary>
/// 工单结束节点标签：Completed → "完成"，Aborted → "中止"。
/// 时间线卡片「实际结束」行的行标签用，避免在 XAML 内联两套状态分支。
/// </summary>
public sealed class WorkOrderEndLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is WorkOrderStatus.Aborted ? Strings.K776 : Strings.K775;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}