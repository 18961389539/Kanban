using MainAPP.Resources;
using MainAPP.Services;
using OxyPlot;

namespace MainAPP.ViewModels;

/// <summary>
/// 概览页时间范围枚举。
/// </summary>
public enum OverviewTimeRange
{
    CurrentShift,
    PreviousShift,
    Today,
    Hour1,
    Hours8,
    Hours24,
    Days7,
}

/// <summary>
/// 设备概览明细（一行）：用于概览页底部紧凑表格。
/// </summary>
public class DeviceOverviewSummary
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int StatusWord { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public double OkRatio => TotalCount > 0 ? (double)OkCount / TotalCount : 0;
    public double NgRatio => TotalCount > 0 ? (double)NgCount / TotalCount : 0;
    public int TotalCount => OkCount + NgCount;
    public double QualityRate { get; set; }
    public double Oee { get; set; }
    public double RunTimeHours { get; set; }
    public double PausedTimeHours { get; set; }
    public double AlarmDurationHours { get; set; }
    public PlotModel StatusDistributionChart { get; set; } = ChartService.BuildStatusDistributionBarChart(0, 0, 0);
    public int AlarmCount { get; set; }
    public string TopAlarmName { get; set; } = string.Empty;
    public IReadOnlyList<int> HourlyOk { get; set; } = Array.Empty<int>();
}

/// <summary>
/// Top 报警摘要：用于概览页右侧 Top 5 报警列表。
/// </summary>
public class AlarmOverviewSummary
{
    public string AlarmName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    public int TriggerCount { get; set; }
    public int RepeatCount => Math.Max(0, TriggerCount - 1);
    public double AverageIntervalMinutes { get; set; }
    public bool IsHighFrequency { get; set; }
    public int OutputBefore { get; set; }
    public int OutputAfter { get; set; }
    public int OutputDelta => OutputAfter - OutputBefore;
    public string ShiftName { get; set; } = string.Empty;
    public double TotalDurationHours { get; set; }
}

/// <summary>
/// 状态时间线分段（一段连续状态）：用于概览页状态时间线图表。
/// </summary>
public sealed class ReviewStatusSegment
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public int StatusWord { get; init; }
    public int OutputDelta { get; init; }
    public int AlarmCount { get; init; }
    public bool HasNoOutput { get; init; }
    public double DurationMinutes => Math.Max(0, (End - Start).TotalMinutes);
    public string TimeRangeText => $"{Start:HH:mm:ss} - {End:HH:mm:ss}";
    public string DurationText => DurationMinutes >= 60
        ? $"{DurationMinutes / 60:F1} h"
        : $"{DurationMinutes:F0} min";
    public string OutputText => string.Format(Strings.F061, OutputDelta, AlarmCount);
}

/// <summary>
/// 缺陷集中度摘要：用于概览页缺陷分布（按设备聚合）。
/// </summary>
public sealed class DefectConcentrationSummary
{
    public string DefectName { get; init; } = string.Empty;
    public string ShiftName { get; init; } = string.Empty;
    public string TimeRangeText { get; init; } = string.Empty;
    public int Count { get; init; }
    public double Share { get; init; }
}

/// <summary>
/// 班次对比摘要：用于概览页班次对比图。
/// </summary>
public class ShiftComparisonSummary
{
    public string ShiftName { get; set; } = string.Empty;
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int TotalCount => OkCount + NgCount;
    public int AlarmCount { get; set; }
    public double OkRatio => TotalCount > 0 ? (double)OkCount / TotalCount : 0;
    public double NgRatio => TotalCount > 0 ? (double)NgCount / TotalCount : 0;
    public double AlarmRate => TotalCount > 0 ? (double)AlarmCount / TotalCount : 0;
    public double Oee { get; set; }
    public double RunTimeHours { get; set; }
    public double AlarmDurationHours { get; set; }
    public double TargetAchievementRate { get; set; }
}

/// <summary>
/// 缺陷帕累托摘要（一行）：用于概览页缺陷帕累托图。
/// 按缺陷名称聚合数量，按数量降序排列，累计占比用于帕累托折线。
/// </summary>
public class DefectParetoSummary
{
    public string DefectName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int Count { get; set; }
    public double CumulativePercent { get; set; }
}
