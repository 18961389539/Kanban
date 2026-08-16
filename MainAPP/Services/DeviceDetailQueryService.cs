using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.ViewModels;
using OxyPlot;

namespace MainAPP.Services;

/// <summary>
/// 设备详情页的历史查询服务：把「最近报警」与「按小时产量」两条查询管线从 DeviceDetailViewModel 抽离。
/// 方法为同步计算（查询 + 聚合 + 图表构建），异步与取消由调用方（ViewModel）负责。
/// </summary>
public static class DeviceDetailQueryService
{
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
        var from = now.AddHours(-rangeHours);
        var logs = historyService.QueryProductionLogs(from, now, deviceId);

        var buckets = HistoryQueryHelper.BuildHourlyBuckets(from, now);
        if (buckets.Length == 0) return null;

        var okCumulative = new int[buckets.Length];
        var ngCumulative = new int[buckets.Length];
        var hasSample = new bool[buckets.Length];
        foreach (var log in logs.OrderBy(log => log.Timestamp))
        {
            var idx = HistoryQueryHelper.GetBucketIndex(buckets, log.Timestamp);
            if (idx >= 0 && idx < buckets.Length)
            {
                okCumulative[idx] = log.OkProduction;
                ngCumulative[idx] = log.NgProduction;
                hasSample[idx] = true;
            }
        }

        // 缺少采集记录的小时沿用上一条累计值，避免后续差分把空桶当成归零。
        var lastOk = 0;
        var lastNg = 0;
        for (int i = 0; i < buckets.Length; i++)
        {
            if (hasSample[i])
            {
                lastOk = okCumulative[i];
                lastNg = ngCumulative[i];
            }
            else
            {
                okCumulative[i] = lastOk;
                ngCumulative[i] = lastNg;
            }
        }

        // 首桶基线：查窗口前最后一条快照作基线，避免把窗口开始前的历史产量计入第一个小时。
        var baseline = historyService.QueryProductionLogs(from.AddHours(-24), from, deviceId).LastOrDefault();
        var okDiff = DiffCumulative(okCumulative, baseline?.OkProduction ?? 0);
        var ngDiff = DiffCumulative(ngCumulative, baseline?.NgProduction ?? 0);

        return ChartService.BuildHourlyProductionBarChart(buckets, okDiff, ngDiff, targetCycle);
    }

    /// <summary>累计值转增量：后一桶减前一桶，负数置零（班次切换重置场景）；首桶扣 baseline。</summary>
    private static int[] DiffCumulative(int[] cumulative, int baseline)
    {
        if (cumulative.Length == 0) return cumulative;
        var result = new int[cumulative.Length];
        result[0] = Math.Max(0, cumulative[0] - baseline);
        for (int i = 1; i < cumulative.Length; i++)
        {
            result[i] = Math.Max(0, cumulative[i] - cumulative[i - 1]);
        }
        return result;
    }
}
