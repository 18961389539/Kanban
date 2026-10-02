using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using System.Globalization;
using System.Text;
using MainAPP.ViewModels;
using Kanban.Analysis;
using MainAPP.Resources;

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
        builder.AppendLine(Strings.Msg_ProductionReviewReport);
        builder.AppendLine(string.Format("{0},{1},{2}",
            Strings.Csv_LabelRange,
            CsvUtil.Escape(data.From.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            CsvUtil.Escape(data.To.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))));
        builder.AppendLine(string.Format("{0},{1}",
            Strings.Csv_LabelShift,
            CsvUtil.Escape(data.ShiftName)));
        builder.AppendLine();
        builder.AppendLine(Strings.Msg_MetricValue);
        builder.AppendLine(string.Format("{0},{1}", Strings.Msg_TotalOK, data.TotalOk));
        builder.AppendLine(string.Format("{0},{1}", Strings.Msg_TotalNG, data.TotalNg));
        builder.AppendLine(string.Format("{0},{1}", Strings.Msg_QualityRate, data.QualityRate.ToString("P1", CultureInfo.InvariantCulture)));
        builder.AppendLine(string.Format("OEE,{0}", data.Oee.ToString("P1", CultureInfo.InvariantCulture)));
        builder.AppendLine(string.Format("{0},{1:F2}h", Strings.Msg_RunTime, data.RunTimeHours));
        builder.AppendLine(string.Format("{0},{1:F2}h", Strings.Msg_IdleTime, data.PausedTimeHours));
        builder.AppendLine(string.Format("{0},{1:F2}h", Strings.Msg_AlarmTime, data.AlarmDurationHours));
        builder.AppendLine(string.Format("{0},{1}", Strings.Msg_AlarmCount2, data.AlarmCount));
        builder.AppendLine();
        builder.AppendLine(Strings.Msg_DeviceDetails);
        builder.AppendLine(Strings.Msg_DeviceOKNGQualityOEERun);
        foreach (var device in data.Devices)
        {
            builder.AppendLine(string.Join(",",
                CsvUtil.Escape(device.DeviceName), device.OkCount, device.NgCount,
                device.QualityRate.ToString("P1", CultureInfo.InvariantCulture), device.Oee.ToString("P1", CultureInfo.InvariantCulture),
                string.Format("{0:F2}h", device.RunTimeHours), string.Format("{0:F2}h", device.PausedTimeHours),
                string.Format("{0:F2}h", device.AlarmDurationHours), device.AlarmCount,
                CsvUtil.Escape(device.TopAlarmName)));
        }
        builder.AppendLine();
        builder.AppendLine(Strings.Msg_ShiftComparison);
        builder.AppendLine(Strings.Msg_ShiftOKNGTotalQualityAlarms);
        foreach (var shift in data.Shifts)
        {
            builder.AppendLine(string.Join(",",
                CsvUtil.Escape(shift.ShiftName), shift.OkCount, shift.NgCount, shift.TotalCount,
                shift.OkRatio.ToString("P1", CultureInfo.InvariantCulture), shift.AlarmCount, shift.AlarmRate.ToString("P1", CultureInfo.InvariantCulture)));
        }
        builder.AppendLine();
        builder.AppendLine(Strings.Msg_TopAlarms);
        builder.AppendLine(Strings.Msg_AlarmDeviceCountDuration);
        foreach (var alarm in data.TopAlarms)
        {
            builder.AppendLine(string.Join(",",
                CsvUtil.Escape(alarm.AlarmName), CsvUtil.Escape(alarm.DeviceName),
                alarm.TriggerCount, string.Format("{0:F2}h", alarm.TotalDurationHours)));
        }
        return builder.ToString();
    }

}
