using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Models;
using MainAPP.ViewModels;

namespace MainAPP.Services;

public enum ProductionReviewBucketSize
{
    Minute5,
    Hour,
    Day,
}

public sealed record ProductionReviewRangeMetrics(
    int Ok,
    int Ng,
    double QualityRate,
    double Oee,
    double RunTimeHours,
    double AlarmDurationHours,
    double TargetAchievementRate);

public sealed record ProductionReviewShiftComparison(
    string ShiftName,
    int OkCount,
    int NgCount,
    int AlarmCount,
    double Oee,
    double RunTimeHours,
    double AlarmDurationHours,
    double TargetAchievementRate);

public interface IProductionReviewMetricsService
{
    ProductionReviewRangeMetrics CalculateRangeMetrics(Device device, DateTime from, DateTime to);

    DateTime[] BuildBuckets(DateTime from, DateTime to, ProductionReviewBucketSize bucketSize);

    (int[] Ok, int[] Ng) BuildProductionDeltas(
        IReadOnlyList<ProductionLog> allLogs,
        DateTime from,
        DateTime[] buckets,
        ProductionReviewBucketSize bucketSize);

    IReadOnlyList<ProductionReviewShiftComparison> BuildShiftComparisons(
        IReadOnlyDictionary<string, List<ProductionLog>> productionLogsByDevice,
        IReadOnlyDictionary<string, List<StatusTransitionRecord>> statusTransitionsByDevice,
        IReadOnlyDictionary<string, List<AlarmEventRecord>> alarmEventsByDevice,
        IReadOnlyList<Device> devices,
        IReadOnlyList<ShiftConfig> shifts,
        DateTime windowFrom,
        DateTime windowTo);
}

/// <summary>
/// 生产复盘指标计算服务。只负责历史数据到指标 DTO 的转换，不依赖 WPF 状态。
/// </summary>
public sealed class ProductionReviewMetricsService : IProductionReviewMetricsService
{
    private readonly IProductionReviewDataService _reviewDataService;

    public ProductionReviewMetricsService(IProductionReviewDataService reviewDataService)
    {
        _reviewDataService = reviewDataService;
    }

    public ProductionReviewRangeMetrics CalculateRangeMetrics(Device device, DateTime from, DateTime to)
    {
        var rangeData = _reviewDataService.QueryDeviceRange(device.Id, from, to);
        var logs = rangeData.ProductionLogs
            .Where(log => log.Timestamp >= from && log.Timestamp <= to)
            .ToList();
        var baseline = rangeData.ProductionLogs
            .Where(log => log.Timestamp < from)
            .ToList();
        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(logs, baseline, from);
        if (logs.Count == 1 && HistoryQueryHelper.FindBaselineBeforeWindow(baseline, logs[0].ShiftName) == null)
        {
            ok = Math.Max(0, logs[0].OkProduction);
            ng = Math.Max(0, logs[0].NgProduction);
        }

        var initialState = rangeData.LatestStatusBefore?.CurrentState ?? (int)DeviceStatus.Unknown;
        var durations = OeeCalculator.CalculateStateDurations(
            rangeData.StatusTransitions,
            from,
            to,
            initialState);
        var quality = OeeCalculator.CalculateQualityRate(ok, ng);
        var performance = OeeCalculator.CalculatePerformanceRate(ok, ng, device.TargetCycle, durations.RunTime);
        var availability = OeeCalculator.CalculateAvailabilityRate(durations.RunTime, durations.AlarmTime);
        var effectiveTo = to < DateTime.Now ? to : DateTime.Now;
        var target = device.TargetCycle * Math.Max(0, (effectiveTo - from).TotalHours);
        var achievement = target > 0 ? Math.Clamp((ok + ng) / target, 0, 1) : 0;

        return new ProductionReviewRangeMetrics(
            ok,
            ng,
            quality,
            OeeCalculator.CalculateOee(quality, performance, availability),
            durations.RunTime / 3600.0,
            durations.AlarmTime / 3600.0,
            achievement);
    }

    public DateTime[] BuildBuckets(DateTime from, DateTime to, ProductionReviewBucketSize bucketSize)
    {
        List<DateTime> buckets = [];
        var current = AlignToBucket(from, bucketSize);
        while (current <= to)
        {
            buckets.Add(current);
            current = AdvanceBucket(current, bucketSize);
        }
        return buckets.ToArray();
    }

    /// <summary>
    /// 产量趋势分桶（与 <c>HistoryQueryHelper.SumWindowProduction</c> 同源统一，2026-08-26 P1）：
    /// 按班次实例分组后做「实例基线差分」——实例窗口内首条增量 = 相对实例基线（窗口起点快照 /
    /// 窗口前同实例末条 / 实例首条），后续条目相邻差分，各桶之和严格等于 SumWindowProduction 总产量。
    /// 修复点：旧实现组内相邻差分会把「窗口起点快照相对窗口前快照」的增量（发生在窗口外）计入首桶，
    /// 且组内单条取累计值（起点累计≠窗口产量），与总产量不一致。
    /// 整窗口仅 1 条快照时按显示语义取累计值（与 OverviewViewModel 特判一致）。
    /// </summary>
    public (int[] Ok, int[] Ng) BuildProductionDeltas(
        IReadOnlyList<ProductionLog> allLogs,
        DateTime from,
        DateTime[] buckets,
        ProductionReviewBucketSize bucketSize)
    {
        var okDeltas = new int[buckets.Length];
        var ngDeltas = new int[buckets.Length];
        if (allLogs.Count == 0) return (okDeltas, ngDeltas);

        // 整窗口仅 1 条快照：与 SumWindowProduction 调用方"单条快照 = 当前累计"显示语义一致
        var winLogs = allLogs.Where(log => log.Timestamp >= from).ToList();
        if (winLogs.Count == 1)
        {
            var bucket = FindBucketIndex(buckets, winLogs[0].Timestamp, bucketSize);
            if (bucket >= 0 && bucket < buckets.Length)
            {
                okDeltas[bucket] = Math.Max(0, winLogs[0].OkProduction);
                ngDeltas[bucket] = Math.Max(0, winLogs[0].NgProduction);
            }
            return (okDeltas, ngDeltas);
        }

        var groups = HistoryQueryHelper.SplitShiftInstances(
            allLogs.OrderBy(log => log.Timestamp).ToList());
        foreach (var group in groups)
        {
            var winPart = group.Where(log => log.Timestamp >= from).ToList();
            if (winPart.Count == 0) continue;
            var baseRec = HistoryQueryHelper.ResolveInstanceBase(group, winPart, from);
            for (var index = 0; index < group.Count; index++)
            {
                var current = group[index];
                if (current.Timestamp < from) continue;
                int okDelta, ngDelta;
                if (current == winPart[0])
                {
                    // 实例窗口内首条：相对实例基线（跨窗口边界增量不再计入）
                    okDelta = Math.Max(0, current.OkProduction - baseRec.OkProduction);
                    ngDelta = Math.Max(0, current.NgProduction - baseRec.NgProduction);
                }
                else
                {
                    var previous = group[index - 1];
                    okDelta = Math.Max(0, current.OkProduction - previous.OkProduction);
                    ngDelta = Math.Max(0, current.NgProduction - previous.NgProduction);
                }
                var bucket = FindBucketIndex(buckets, current.Timestamp, bucketSize);
                if (bucket >= 0 && bucket < buckets.Length)
                {
                    okDeltas[bucket] += okDelta;
                    ngDeltas[bucket] += ngDelta;
                }
            }
        }
        return (okDeltas, ngDeltas);
    }

    public IReadOnlyList<ProductionReviewShiftComparison> BuildShiftComparisons(
        IReadOnlyDictionary<string, List<ProductionLog>> productionLogsByDevice,
        IReadOnlyDictionary<string, List<StatusTransitionRecord>> statusTransitionsByDevice,
        IReadOnlyDictionary<string, List<AlarmEventRecord>> alarmEventsByDevice,
        IReadOnlyList<Device> devices,
        IReadOnlyList<ShiftConfig> shifts,
        DateTime windowFrom,
        DateTime windowTo)
    {
        if (shifts.Count == 0) return [];

        List<ProductionReviewShiftComparison> result = [];
        // 节拍口径与主 OEE 一致（OverviewViewModel 用 devices.Average，P2-16 统一）
        var avgTargetCycle = devices.Count > 0 ? devices.Average(d => d.TargetCycle) : 0;

        // 每设备排序一次，供各班次×天区间复用（P2-16：原来每区间重复 Where+OrderByDescending 线性扫描）
        var sortedTransitionsByDevice = statusTransitionsByDevice.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<StatusTransitionRecord>)pair.Value.OrderBy(t => t.EventTime).ToList());

        foreach (var shift in shifts)
        {
            int shiftOk = 0;
            int shiftNg = 0;
            int shiftAlarmCount = 0;
            double runSeconds = 0;
            double alarmSeconds = 0;
            // 目标产量（件）：avgTargetCycle(件/秒) × 时段时长(秒)，与达成率分母同量纲——
            // 命名曾误作 targetSeconds（2026-08-16 修正）。
            double targetOutput = 0;

            foreach (var device in devices)
            {
                if (productionLogsByDevice.TryGetValue(device.Id, out var logs) && logs.Count > 0)
                {
                    var inWindow = logs
                        .Where(log => log.Timestamp >= windowFrom && log.ShiftName == shift.Name)
                        .ToList();
                    var baseline = logs.Where(log => log.Timestamp < windowFrom).ToList();
                    var (ok, ng) = HistoryQueryHelper.SumWindowProduction(inWindow, baseline, windowFrom);
                    shiftOk += ok;
                    shiftNg += ng;
                }

                if (alarmEventsByDevice.TryGetValue(device.Id, out var alarms))
                {
                    shiftAlarmCount += alarms.Count(alarm =>
                        alarm.ShiftName == shift.Name && alarm.EventType == AlarmEventType.Triggered);
                }

                if (!sortedTransitionsByDevice.TryGetValue(device.Id, out var transitions)) continue;
                for (var day = windowFrom.Date.AddDays(-1); day <= windowTo.Date; day = day.AddDays(1))
                {
                    var shiftRange = shift.ResolveRange(day + shift.StartTime + TimeSpan.FromMinutes(1));
                    var rangeFrom = shiftRange.Start < windowFrom ? windowFrom : shiftRange.Start;
                    var rangeTo = shiftRange.End > windowTo ? windowTo : shiftRange.End;
                    if (rangeTo <= rangeFrom) continue;

                    // 已排序列表：用二分定位区间起点，只遍历区间内事件（避免全量 Where）
                    var startIdx = LowerBound(transitions, rangeFrom);
                    var rangeTransitions = new List<StatusTransitionRecord>();
                    for (var i = startIdx; i < transitions.Count && transitions[i].EventTime <= rangeTo; i++)
                        rangeTransitions.Add(transitions[i]);
                    var previous = startIdx > 0 ? transitions[startIdx - 1] : null;
                    var initialState = previous?.CurrentState ?? (int)DeviceStatus.Unknown;
                    var durations = OeeCalculator.CalculateStateDurations(
                        rangeTransitions,
                        rangeFrom,
                        rangeTo,
                        initialState);
                    runSeconds += durations.RunTime;
                    alarmSeconds += durations.AlarmTime;
                    targetOutput += avgTargetCycle * Math.Max(0, (rangeTo - rangeFrom).TotalHours);
                }
            }

            var quality = OeeCalculator.CalculateQualityRate(shiftOk, shiftNg);
            // (int)avgTargetCycle：CalculatePerformanceRate 形参为 int（节拍为整数值域），
            // 均值小数部分截断，误差 <1 件/秒，可接受。
            var performance = OeeCalculator.CalculatePerformanceRate(
                shiftOk,
                shiftNg,
                (int)avgTargetCycle,
                runSeconds);
            var availability = OeeCalculator.CalculateAvailabilityRate(runSeconds, alarmSeconds);
            var total = shiftOk + shiftNg;
            result.Add(new ProductionReviewShiftComparison(
                shift.Name,
                shiftOk,
                shiftNg,
                shiftAlarmCount,
                OeeCalculator.CalculateOee(quality, performance, availability),
                runSeconds / 3600.0,
                alarmSeconds / 3600.0,
                targetOutput > 0 ? Math.Clamp(total / targetOutput, 0, 1) : 0));
        }
        return result;
    }

    /// <summary>已按 EventTime 升序排序的列表上做二分：返回第一个 EventTime &gt;= target 的索引（无则 Count）。</summary>
    private static int LowerBound(IReadOnlyList<StatusTransitionRecord> sorted, DateTime target)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid].EventTime < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static DateTime AlignToBucket(DateTime value, ProductionReviewBucketSize bucketSize) => bucketSize switch
    {
        ProductionReviewBucketSize.Minute5 => new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute / 5 * 5, 0),
        ProductionReviewBucketSize.Hour => new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0),
        ProductionReviewBucketSize.Day => value.Date,
        _ => new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0),
    };

    private static DateTime AdvanceBucket(DateTime value, ProductionReviewBucketSize bucketSize) => bucketSize switch
    {
        ProductionReviewBucketSize.Minute5 => value.AddMinutes(5),
        ProductionReviewBucketSize.Hour => value.AddHours(1),
        ProductionReviewBucketSize.Day => value.AddDays(1),
        _ => value.AddHours(1),
    };

    private static int FindBucketIndex(
        IReadOnlyList<DateTime> buckets,
        DateTime value,
        ProductionReviewBucketSize bucketSize)
    {
        if (buckets.Count == 0) return -1;
        var aligned = AlignToBucket(value, bucketSize);
        // 二分查找（审查修复 2026-08-13）：原两轮线性扫描 O(n)——2 天 5 分钟桶（577 桶）× 万级日志
        // ≈ 千万次比较。桶虽等距对齐，但 Day 桶跨夏令时会出现 23/25 小时桶，算术秒差索引会错位，
        // 二分保持 O(log n) 且 DST 安全。语义不变：精确命中优先，否则返回首个 >= aligned 的桶（插入点）。
        int lo = 0, hi = buckets.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var cmp = buckets[mid].CompareTo(aligned);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return lo < buckets.Count ? lo : -1;
    }
}
