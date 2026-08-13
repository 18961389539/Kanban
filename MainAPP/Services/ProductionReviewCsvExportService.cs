using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
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
        builder.AppendLine(Strings.M239);
        builder.AppendLine(string.Format("{0},{1},{2}",
            Strings.F187.Split('：')[0] + "：",
            CsvUtil.Escape(data.From.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            CsvUtil.Escape(data.To.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))));
        builder.AppendLine(string.Format("{0},{1}",
            Strings.F122.Split('：')[0] + "：",
            CsvUtil.Escape(data.ShiftName)));
        builder.AppendLine();
        builder.AppendLine(Strings.M240);
        builder.AppendLine(string.Format("{0},{1}", Strings.M241, data.TotalOk));
        builder.AppendLine(string.Format("{0},{1}", Strings.M242, data.TotalNg));
        builder.AppendLine(string.Format("{0},{1}", Strings.M238, data.QualityRate.ToString("P1", CultureInfo.InvariantCulture)));
        builder.AppendLine(string.Format("OEE,{0}", data.Oee.ToString("P1", CultureInfo.InvariantCulture)));
        builder.AppendLine(string.Format("{0},{1:F2}h", Strings.M244, data.RunTimeHours));
        builder.AppendLine(string.Format("{0},{1:F2}h", Strings.M245, data.PausedTimeHours));
        builder.AppendLine(string.Format("{0},{1:F2}h", Strings.M246, data.AlarmDurationHours));
        builder.AppendLine(string.Format("{0},{1}", Strings.M247, data.AlarmCount));
        builder.AppendLine();
        builder.AppendLine(Strings.M248);
        builder.AppendLine(Strings.M249);
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
        builder.AppendLine(Strings.M250);
        builder.AppendLine(Strings.M251);
        foreach (var shift in data.Shifts)
        {
            builder.AppendLine(string.Join(",",
                CsvUtil.Escape(shift.ShiftName), shift.OkCount, shift.NgCount, shift.TotalCount,
                shift.OkRatio.ToString("P1", CultureInfo.InvariantCulture), shift.AlarmCount, shift.AlarmRate.ToString("P1", CultureInfo.InvariantCulture)));
        }
        builder.AppendLine();
        builder.AppendLine(Strings.M252);
        builder.AppendLine(Strings.M253);
        foreach (var alarm in data.TopAlarms)
        {
            builder.AppendLine(string.Join(",",
                CsvUtil.Escape(alarm.AlarmName), CsvUtil.Escape(alarm.DeviceName),
                alarm.TriggerCount, string.Format("{0:F2}h", alarm.TotalDurationHours)));
        }
        return builder.ToString();
    }

}
