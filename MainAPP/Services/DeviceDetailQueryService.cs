using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Kanban.Contracts.Metrics;
using MainAPP.Models;
using OxyPlot;

namespace MainAPP.Services;

/// <summary>
/// 设备详情页的历史查询服务：把「最近报警」与「按小时产量」两条查询管线从 DeviceDetailViewModel 抽离。
/// 方法为同步计算（查询 + 聚合 + 图表构建），异步与取消由调用方（ViewModel）负责。
/// </summary>
public static class DeviceDetailQueryService
{
    /// <summary>按小时产量差分序列（桶起始时刻 + OK/NG 增量），供柱图与本班小时板共用。</summary>
    public readonly record struct HourlyProductionSeries(DateTime[] Buckets, int[] OkDiff, int[] NgDiff);

    /// <summary>查询指定设备指定时间范围内的报警事件（时间倒序）。</summary>
    public static List<AlarmEventRecord> QueryRecentAlarms(
        IHistoryService historyService,
        string deviceId,
        DateTime start,
        DateTime end)
        => historyService.QueryAlarmEvents(start, end, deviceId)
            .OrderByDescending(r => r.EventTime)
            .ToList();

    /// <summary>
    /// 构建指定设备最近 rangeHours 小时的按小时产量柱状图（累计值差分 + 窗口前基线）。
    /// 无可用桶时返回 null（由调用方显示空状态）。
    /// </summary>
    public static PlotModel? BuildHourlyProductionChart(
        IHistoryService historyService,
        string deviceId,
        int targetCycle,
        int rangeHours,
        DateTime now)
    {
        var series = QueryHourlySeries(historyService, deviceId, now.AddHours(-rangeHours), now);
        if (series.Buckets.Length == 0) return null;
        return ChartService.BuildHourlyProductionBarChart(series.Buckets, series.OkDiff, series.NgDiff, targetCycle);
    }

    /// <summary>
    /// 当前班次逐小时计划板：计划 = 额定产能按该小时重叠分钟折算，实际 = 与柱图同一套 OK 差分。
    /// </summary>
    public static IReadOnlyList<HourBucketItem> BuildHourByHourBoard(
        IHistoryService historyService,
        string deviceId,
        DateTime shiftStart,
        DateTime shiftEnd,
        DateTime now,
        int targetPcsPerHour)
    {
        var queryTo = now < shiftEnd ? now : shiftEnd;
        var okByHour = new Dictionary<DateTime, int>();
        if (queryTo > shiftStart)
        {
            var series = QueryHourlySeries(historyService, deviceId, shiftStart, queryTo);
            for (int i = 0; i < series.Buckets.Length; i++)
            {
                var ok = i < series.OkDiff.Length ? Math.Max(0, series.OkDiff[i]) : 0;
                okByHour[series.Buckets[i]] = ok;
            }
        }

        return HourByHourBoardBuilder.Build(shiftStart, shiftEnd, now, targetPcsPerHour, okByHour);
    }

    /// <summary>
    /// 指定窗口内按小时产量差分（累计值差分 + 窗口前基线）。
    /// 与设备详情小时柱图同一套口径，小时板必须复用，避免两套算法。
    /// </summary>
    public static HourlyProductionSeries QueryHourlySeries(
        IProductionHistoryReader historyService,
        string deviceId,
        DateTime from,
        DateTime to)
    {
        var logs = historyService.QueryProductionLogs(from, to, deviceId);
        // 首桶基线：查窗口前最后一条快照作基线，避免把窗口开始前的历史产量计入第一个小时。
        var baseline = historyService.QueryProductionLogs(from.AddHours(-24), from, deviceId).LastOrDefault();
        var series = HourlyProductionDiff.FromSamples(
            logs.Select(log => (log.Timestamp, log.OkProduction, log.NgProduction)),
            from, to,
            baseline?.OkProduction ?? 0,
            baseline?.NgProduction ?? 0);
        return new HourlyProductionSeries(series.Hours, series.OkDiff, series.NgDiff);
    }
}
