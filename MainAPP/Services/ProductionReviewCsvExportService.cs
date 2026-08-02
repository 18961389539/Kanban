using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using System.Globalization;
using System.Text;
using MainAPP.ViewModels;

namespace MainAPP.Services;

public interface IProductionReviewCsvExportService
{
    string Build(ProductionReviewCsvData data);
}

public sealed record ProductionReviewCsvData(
    DateTime From,
    DateTime To,
    string ShiftName,
    int TotalOk,
    int TotalNg,
    double QualityRate,
    double Oee,
    double RunTimeHours,
    double PausedTimeHours,
    double AlarmDurationHours,
    int AlarmCount,
    IReadOnlyList<DeviceOverviewSummary> Devices,
    IReadOnlyList<ShiftComparisonSummary> Shifts,
    IReadOnlyList<AlarmOverviewSummary> TopAlarms);

public sealed class ProductionReviewCsvExportService : IProductionReviewCsvExportService
{
    public string Build(ProductionReviewCsvData data)
    {
        var builder = new StringBuilder();
        builder.AppendLine("生产复盘报表");
        builder.AppendLine($"统计范围,{Escape(data.From.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))},{Escape(data.To.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}");
        builder.AppendLine($"当前班次,{Escape(data.ShiftName)}");
        builder.AppendLine();
        builder.AppendLine("指标,数值");
        builder.AppendLine($"总合格产量,{data.TotalOk}");
        builder.AppendLine($"总不良产量,{data.TotalNg}");
        builder.AppendLine($"良品率,{data.QualityRate:P1}");
        builder.AppendLine($"OEE,{data.Oee:P1}");
        builder.AppendLine($"运行时长,{data.RunTimeHours:F2}h");
        builder.AppendLine($"待机时长,{data.PausedTimeHours:F2}h");
        builder.AppendLine($"报警时长,{data.AlarmDurationHours:F2}h");
        builder.AppendLine($"报警次数,{data.AlarmCount}");
        builder.AppendLine();
        builder.AppendLine("设备明细");
        builder.AppendLine("设备,合格产量,不良产量,良品率,OEE,运行时长,待机时长,报警时长,报警次数,主要报警");
        foreach (var device in data.Devices)
        {
            builder.AppendLine(string.Join(",",
                Escape(device.DeviceName), device.OkCount, device.NgCount,
                $"{device.QualityRate:P1}", $"{device.Oee:P1}",
                $"{device.RunTimeHours:F2}h", $"{device.PausedTimeHours:F2}h",
                $"{device.AlarmDurationHours:F2}h", device.AlarmCount,
                Escape(device.TopAlarmName)));
        }
        builder.AppendLine();
        builder.AppendLine("班次对比");
        builder.AppendLine("班次,合格产量,不良产量,总产量,良品率,报警次数,报警密度");
        foreach (var shift in data.Shifts)
        {
            builder.AppendLine(string.Join(",",
                Escape(shift.ShiftName), shift.OkCount, shift.NgCount, shift.TotalCount,
                $"{shift.OkRatio:P1}", shift.AlarmCount, $"{shift.AlarmRate:P1}"));
        }
        builder.AppendLine();
        builder.AppendLine("Top 报警");
        builder.AppendLine("报警名称,设备,触发次数,累计时长");
        foreach (var alarm in data.TopAlarms)
        {
            builder.AppendLine(string.Join(",",
                Escape(alarm.AlarmName), Escape(alarm.DeviceName),
                alarm.TriggerCount, $"{alarm.TotalDurationHours:F2}h"));
        }
        return builder.ToString();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
