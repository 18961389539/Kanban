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
/// 复盘结论类型（方案 A 分类图标，2026-08-11）。
/// </summary>
public enum ReviewConclusionKind
{
    Quality,      // 良品率
    Oee,          // OEE
    Downtime,     // 最长停机
    BestShift,    // 最佳班次
    TopDefect,    // 头号缺陷
    AlarmCount,   // 报警汇总
    Info,         // 无数据/占位
}

/// <summary>复盘结论达标状态：达标 / 未达标 / 中性。</summary>
public enum ReviewConclusionMetState
{
    Met,
    NotMet,
    Neutral,
}

/// <summary>
/// 结构化复盘结论（方案 A，2026-08-11）：替代纯文本，携带类型与达标状态供 UI 分类渲染。
/// </summary>
public sealed class ReviewConclusion
{
    public string Text { get; init; } = string.Empty;
    public ReviewConclusionKind Kind { get; init; }
    public ReviewConclusionMetState Met { get; init; }
}

/// <summary>
/// Top 报警摘要：用于概览页右侧 Top 5 报警列表。
/// </summary>
public class AlarmOverviewSummary
{
    /// <summary>排行序号（1 起，按触发次数降序）。方案 A 排名徽章（2026-08-11）。</summary>
    public int Rank { get; set; }
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

    /// <summary>
    /// 累计时长显示文本：按量级自适应 秒/分钟/小时（2026-08-11）。
    /// 仿真/短报警持续数十秒，原 {0:F1}h 格式全部显示 0.0h 无意义。
    /// </summary>
    public string TotalDurationText
    {
        get
        {
            var minutes = TotalDurationHours * 60;
            if (minutes < 1) return $"{TotalDurationHours * 3600:F0}s";
            if (minutes < 60) return $"{minutes:F1} min";
            return $"{TotalDurationHours:F1}h";
        }
    }
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
/// 热力图时间桶（等宽格子）：用于概览页可视化设备状态演化。
/// SourceSegment 记录着色归属段，点击交互直接使用它，保证"所见即所点"。
/// </summary>
public sealed class HeatmapBucket
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int StatusWord { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string ToolTipText =>
        $"{StatusText}  {Start:MM-dd HH:mm}-{End:HH:mm}";
    /// <summary>时间轴刻度标签（按桶索引均匀取样，非刻度桶为 null），跨天窗口显示日期。</summary>
    public string? TickLabel { get; init; }
    /// <summary>着色归属段（桶内重叠最长的状态段）；无重叠（未知格）时为 null。</summary>
    public ReviewStatusSegment? SourceSegment { get; init; }
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
