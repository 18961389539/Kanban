using MainAPP.Entities;
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

    public (int[] Ok, int[] Ng) BuildProductionDeltas(
        IReadOnlyList<ProductionLog> allLogs,
        DateTime from,
        DateTime[] buckets,
        ProductionReviewBucketSize bucketSize)
    {
        var okDeltas = new int[buckets.Length];
        var ngDeltas = new int[buckets.Length];
        if (allLogs.Count == 0) return (okDeltas, ngDeltas);

        var groups = HistoryQueryHelper.SplitShiftInstances(
            allLogs.OrderBy(log => log.Timestamp).ToList());
        foreach (var group in groups)
        {
            for (var index = 0; index < group.Count; index++)
            {
                var current = group[index];
                if (current.Timestamp < from) continue;

                var previous = index > 0 ? group[index - 1] : null;
                var okDelta = previous == null
                    ? group.Count == 1 ? current.OkProduction : 0
                    : Math.Max(0, current.OkProduction - previous.OkProduction);
                var ngDelta = previous == null
                    ? group.Count == 1 ? current.NgProduction : 0
                    : Math.Max(0, current.NgProduction - previous.NgProduction);
                var bucket = FindBucketIndex(buckets, current.Timestamp, bucketSize);
                if (bucket < 0 || bucket >= buckets.Length) continue;
                okDeltas[bucket] += Math.Max(0, okDelta);
                ngDeltas[bucket] += Math.Max(0, ngDelta);
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
        foreach (var shift in shifts)
        {
            int shiftOk = 0;
            int shiftNg = 0;
            int shiftAlarmCount = 0;
            double runSeconds = 0;
            double alarmSeconds = 0;
            double targetSeconds = 0;

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

                if (!statusTransitionsByDevice.TryGetValue(device.Id, out var transitions)) continue;
                for (var day = windowFrom.Date.AddDays(-1); day <= windowTo.Date; day = day.AddDays(1))
                {
                    var shiftRange = shift.ResolveRange(day + shift.StartTime + TimeSpan.FromMinutes(1));
                    var rangeFrom = shiftRange.Start < windowFrom ? windowFrom : shiftRange.Start;
                    var rangeTo = shiftRange.End > windowTo ? windowTo : shiftRange.End;
                    if (rangeTo <= rangeFrom) continue;

                    var rangeTransitions = transitions
                        .Where(transition => transition.EventTime >= rangeFrom && transition.EventTime <= rangeTo)
                        .ToList();
                    var previous = transitions
                        .Where(transition => transition.EventTime < rangeFrom)
                        .OrderByDescending(transition => transition.EventTime)
                        .FirstOrDefault();
                    var initialState = previous?.CurrentState ?? (int)DeviceStatus.Unknown;
                    var durations = OeeCalculator.CalculateStateDurations(
                        rangeTransitions,
                        rangeFrom,
                        rangeTo,
                        initialState);
                    runSeconds += durations.RunTime;
                    alarmSeconds += durations.AlarmTime;
                    targetSeconds += device.TargetCycle * Math.Max(0, (rangeTo - rangeFrom).TotalHours);
                }
            }

            var quality = OeeCalculator.CalculateQualityRate(shiftOk, shiftNg);
            var performance = OeeCalculator.CalculatePerformanceRate(
                shiftOk,
                shiftNg,
                devices.FirstOrDefault()?.TargetCycle ?? 0,
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
                targetSeconds > 0 ? Math.Clamp(total / targetSeconds, 0, 1) : 0));
        }
        return result;
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
        var aligned = AlignToBucket(value, bucketSize);
        for (var index = 0; index < buckets.Count; index++)
        {
            if (buckets[index] == aligned) return index;
        }
        for (var index = 0; index < buckets.Count; index++)
        {
            if (buckets[index] >= aligned) return index;
        }
        return -1;
    }
}
