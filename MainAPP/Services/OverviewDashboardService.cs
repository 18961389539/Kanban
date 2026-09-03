using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
using MainAPP.Resources;
using MainAPP.ViewModels;
using Serilog;

namespace MainAPP.Services;

/// <summary>
/// 概览页数据聚合服务（2026-09-02 从 OverviewViewModel.QueryData 拆分，P1-9）。
/// 负责在后台线程完成全厂产量/状态/报警数据的批量查询与聚合，返回纯数据结果；
/// WPF 层（ViewModel）只负责把结果映射为绑定属性与图表模型。
/// </summary>
public interface IOverviewDashboardService
{
    /// <summary>解析时间范围（导出命令与内部聚合共用单实现）。</summary>
    (DateTime From, DateTime To) ResolveTimeRange(
        OverviewTimeRange timeRange,
        IReadOnlyList<ShiftConfig> shifts,
        DateTime now);

    /// <summary>后台线程执行全量聚合（含批量查询），返回不可变结果。</summary>
    OverviewDashboardResult Build(
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, DeviceRuntime> runtimeMap,
        IReadOnlyList<ShiftConfig> shiftsSnapshot,
        OverviewTimeRange timeRange,
        DateTime now);
}

/// <summary>概览页时间桶粒度（天粒度桶的时间标签用 MM-dd 格式）。</summary>
public enum OverviewBucketSize
{
    Minute5,
    Hour,
    Day,
}

/// <summary>
/// 概览页聚合结果（纯数据，无 UI 依赖）。
/// 由 <see cref="OverviewDashboardService.Build"/> 在后台线程产出，
/// ViewModel 在 UI 线程消费并映射为可绑定属性/集合/图表。
/// </summary>
public sealed record OverviewDashboardResult(
    DateTime From,
    DateTime To,
    DateTime ComparisonFrom,
    DateTime ComparisonTo,
    string ComparisonLabel,
    OverviewBucketSize BucketSize,
    DateTime[] Buckets,
    int[] BucketOk,
    int[] BucketNg,
    IReadOnlyList<DeviceOverviewSummary> DeviceSummaries,
    IReadOnlyList<AlarmOverviewSummary> TopAlarms,
    IReadOnlyList<ReviewStatusSegment> StatusTimeline,
    IReadOnlyList<ShiftComparisonSummary> ShiftComparisons,
    IReadOnlyList<DefectParetoSummary> DefectParetos,
    IReadOnlyList<string> HealthIssues,
    IReadOnlyList<ReviewConclusion> ReviewConclusions,
    int TotalOk,
    int TotalNg,
    int AlarmCount,
    int PendingAlarmCount,
    double QualityRate,
    double Oee,
    double Performance,
    double Availability,
    double RunTimeHours,
    double PausedTimeHours,
    double AlarmDurationHours,
    double AvgTargetCycle,
    int TargetOutput,
    double OutputAchievementRate,
    int BaselineTotalOutput,
    double BaselineQualityRate,
    double BaselineOee,
    int OutputDelta,
    double QualityRateDelta,
    double OeeDelta,
    double TotalDowntimeHours,
    double AverageAlarmDurationMinutes,
    double MtbfHours,
    string PeakHour,
    int PeakHourOk,
    string ValleyHour,
    int ValleyHourOk,
    string LongestDowntimeDevice,
    string LongestDowntimeAlarm,
    double LongestDowntimeHours,
    string AvailabilityLossText,
    string PerformanceLossText,
    string QualityLossText,
    int HealthScore,
    string CurrentWorkOrderText,
    string CurrentProductText,
    string CurrentRecipeText);

/// <summary>
/// 概览页数据聚合实现（2026-09-02 从 OverviewViewModel 拆分，P1-9）。
/// 只做查询与计算，不触碰 UI 线程、不持有可绑定属性。
/// </summary>
public sealed class OverviewDashboardService : IOverviewDashboardService
{
    private readonly IProductionReviewDataService _dataService;
    private readonly IProductionReviewMetricsService _metricsService;
    private readonly IProductionReviewAnalysisService _analysisService;
    private readonly IDefectHistoryReader? _defectHistoryStore;

    public OverviewDashboardService(
        IProductionReviewDataService dataService,
        IProductionReviewMetricsService metricsService,
        IProductionReviewAnalysisService analysisService,
        IDefectHistoryReader? defectHistoryStore = null)
    {
        _dataService = dataService;
        _metricsService = metricsService;
        _analysisService = analysisService;
        _defectHistoryStore = defectHistoryStore;
    }

    /// <inheritdoc />
    public (DateTime From, DateTime To) ResolveTimeRange(
        OverviewTimeRange timeRange,
        IReadOnlyList<ShiftConfig> shifts,
        DateTime now)
        => timeRange switch
        {
            OverviewTimeRange.CurrentShift => ResolveCurrentShiftRange(shifts, now),
            OverviewTimeRange.PreviousShift => ResolvePreviousShiftRange(shifts, now),
            OverviewTimeRange.Today => (now.Date, now),
            OverviewTimeRange.Hour1 => (now.AddHours(-1), now),
            OverviewTimeRange.Hours8 => (now.AddHours(-8), now),
            OverviewTimeRange.Hours24 => (now.AddHours(-24), now),
            OverviewTimeRange.Days7 => (now.AddDays(-7), now),
            _ => (now.AddHours(-24), now),
        };

    /// <summary>
    /// 在后台线程执行全部查询：每台设备的生产/状态/报警数据，聚合后返回纯数据结果。
    /// 逻辑自 <see cref="OverviewViewModel.QueryData"/> 原样搬迁（2026-09-02），口径不变。
    /// </summary>
    public OverviewDashboardResult Build(
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, DeviceRuntime> runtimeMap,
        IReadOnlyList<ShiftConfig> shiftsSnapshot,
        OverviewTimeRange timeRange,
        DateTime now)
    {
        var (from, to) = ResolveTimeRange(timeRange, shiftsSnapshot, now);
        if (devices.Count == 0)
            throw new ArgumentException("Build 需要至少一台设备", nameof(devices));

        // 按小时桶聚合全厂产量趋势
        var bucketSize = GetBucketSize(timeRange);
        var buckets = _metricsService.BuildBuckets(from, to, (ProductionReviewBucketSize)bucketSize);
        var bucketOk = new int[buckets.Length];
        var bucketNg = new int[buckets.Length];

        List<DeviceOverviewSummary> deviceSummaries = [];

        int totalOk = 0, totalNg = 0;
        double totalRunSec = 0, totalAlarmSec = 0, totalPauseSec = 0;
        int totalAlarmCount = 0;
        int totalPendingAlarmCount = 0;
        string longestDowntimeDevice = string.Empty;
        string longestDowntimeAlarm = string.Empty;
        double maxDowntimeSec = 0;
        var selectedDevice = devices[0];

        // ── 批量查询：一次拉全量后内存分组，消除 foreach 内的 N+1 ──
        var deviceIds = devices.Select(d => d.Id).ToList();
        // 生产快照向窗口前扩展一天，用于跨班次窗口差分基线；状态/报警仍只查询窗口内数据。
        var swQuery = System.Diagnostics.Stopwatch.StartNew();
        var reviewData = _dataService.QueryWindow(from, to, deviceIds);
        swQuery.Stop();
        var prodLogsByDevice = reviewData.ProductionLogsByDevice;
        var statusByDevice = reviewData.StatusTransitionsByDevice;
        var alarmByDevice = reviewData.AlarmEventsByDevice;
        Log.Information("[耗时] QueryWindow {Elapsed}ms 生产={Prod} 状态={Status} 报警={Alarm}",
            swQuery.ElapsedMilliseconds,
            prodLogsByDevice.Values.Sum(v => v.Count),
            statusByDevice.Values.Sum(v => v.Count),
            alarmByDevice.Values.Sum(v => v.Count));

        var swDevice = System.Diagnostics.Stopwatch.StartNew();
        var swDelta = System.Diagnostics.Stopwatch.StartNew();
        var deltaMs = 0L;

        foreach (var device in devices)
        {
            // ── 生产快照 ──
            prodLogsByDevice.TryGetValue(device.Id, out var allProdLogs);
            allProdLogs ??= [];
            var prodLogs = allProdLogs
                .Where(p => p.Timestamp >= from && p.Timestamp <= to)
                .OrderBy(p => p.Timestamp)
                .ToList();
            var baselineCandidates = allProdLogs
                .Where(p => p.Timestamp < from)
                .ToList();
            var (devOk, devNg) = HistoryQueryHelper.SumWindowProduction(
                prodLogs, baselineCandidates, from);
            // 保留首页既有语义：单条快照且无可用同班次基线时，
            // 将该快照视为当前累计产量；多条快照和跨班次数据仍按差分计算。
            if (prodLogs.Count == 1
                && HistoryQueryHelper.FindBaselineBeforeWindow(baselineCandidates, prodLogs[0].ShiftName) == null)
            {
                devOk = Math.Max(0, prodLogs[0].OkProduction);
                devNg = Math.Max(0, prodLogs[0].NgProduction);
            }
            var (devHourlyOk, devHourlyNg) = _metricsService.BuildProductionDeltas(
                allProdLogs,
                from,
                buckets,
                (ProductionReviewBucketSize)bucketSize);
            swDelta.Stop();
            deltaMs += swDelta.ElapsedMilliseconds;
            swDelta.Restart();

            // 汇总到全厂桶
            for (int i = 0; i < buckets.Length; i++)
            {
                bucketOk[i] += devHourlyOk[i];
                bucketNg[i] += devHourlyNg[i];
            }

            totalOk += devOk;
            totalNg += devNg;

            // ── 状态时长 ──
            statusByDevice.TryGetValue(device.Id, out var statusTransitions);
            statusTransitions ??= [];
            // GetLatestStatusBefore 仍是逐设备查询（窗口前最后一条），保留不变（查询量小且难以批量化）
            var lastBefore = _dataService.GetLatestStatusBefore(device.Id, from);
            int initialState = lastBefore?.CurrentState ?? (int)DeviceStatus.Offline;
            var (runSec, alarmSec, pauseSec, _) = OeeCalculator.CalculateStateDurations(
                statusTransitions, from, to, initialState);
            totalRunSec += runSec;
            totalAlarmSec += alarmSec;
            totalPauseSec += pauseSec;

            // ── 报警事件 ──
            alarmByDevice.TryGetValue(device.Id, out var alarmEvents);
            alarmEvents ??= [];
            int devAlarmCount = alarmEvents.Count(e => e.EventType == AlarmEventType.Triggered);
            totalAlarmCount += devAlarmCount;

            // 待处理报警：最后一条是 Triggered 且无对应 Recovered
            var pendingCount = CountPendingAlarms(alarmEvents);
            totalPendingAlarmCount += pendingCount;

            // 最长停机报警（按报警 Id 分组，计算 Triggered 到 Recovered 的时长；未恢复按窗口终点截断）
            // 口径说明（2026-08-16）：此处刻意用「事件配对」而非状态段时长——本指标要回答"哪一条报警
            // 拖得最久"，需要把时长归因到具体报警名/设备，状态段时长只能给出聚合值无法归因。
            // 与 TotalDowntimeHours 等聚合指标（状态段口径，重叠报警不重复计时）口径不同：
            // 重叠报警下两者数值可能不一致，属预期而非 bug。
            var (topAlarmName, topDurationSec) = FindLongestAlarm(alarmEvents, to);
            if (topDurationSec > maxDowntimeSec)
            {
                maxDowntimeSec = topDurationSec;
                longestDowntimeDevice = device.Name;
                longestDowntimeAlarm = topAlarmName;
            }

            // 实时状态（从 RuntimeMap）
            int statusWord = (int)DeviceStatus.Offline;
            if (runtimeMap.TryGetValue(device.Id, out var rt))
                statusWord = rt.StatusWord;

            // OEE 计算
            double devQuality = OeeCalculator.CalculateQualityRate(devOk, devNg);
            int targetCycle = device.TargetCycle;
            double devPerformance = OeeCalculator.CalculatePerformanceRate(devOk, devNg, targetCycle, runSec);
            double devAvailability = OeeCalculator.CalculateAvailabilityRate(runSec, alarmSec);
            double devOee = OeeCalculator.CalculateOee(devQuality, devPerformance, devAvailability);

            deviceSummaries.Add(new DeviceOverviewSummary
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                StatusWord = statusWord,
                OkCount = devOk,
                NgCount = devNg,
                QualityRate = devQuality,
                Oee = devOee,
                RunTimeHours = runSec / 3600.0,
                PausedTimeHours = pauseSec / 3600.0,
                AlarmDurationHours = alarmSec / 3600.0,
                StatusDistributionChart = ChartService.BuildStatusDistributionBarChart(runSec, alarmSec, pauseSec),
                AlarmCount = devAlarmCount,
                TopAlarmName = topAlarmName,
                HourlyOk = devHourlyOk,
            });
        }

        // ── 全厂 OEE 加权（用总产量和总时长计算，非各设备 OEE 平均）──
        double quality = OeeCalculator.CalculateQualityRate(totalOk, totalNg);
        // 性能率需要全厂 TargetCycle 加权，简化用所有设备 TargetCycle 的均值
        double avgTargetCycle = devices.Count > 0
            ? devices.Average(d => d.TargetCycle)
            : 0;
        double performance = OeeCalculator.CalculatePerformanceRate(totalOk, totalNg, (int)avgTargetCycle, totalRunSec);
        double availability = OeeCalculator.CalculateAvailabilityRate(totalRunSec, totalAlarmSec);
        double oee = OeeCalculator.CalculateOee(quality, performance, availability);
        var effectiveTo = to > now ? now : to;
        var targetOutput = (int)Math.Max(0, Math.Round(avgTargetCycle * Math.Max(0, (effectiveTo - from).TotalHours)));
        var outputAchievementRate = targetOutput > 0
            ? Math.Clamp((double)(totalOk + totalNg) / targetOutput, 0, 1)
            : 0;

        var comparisonRange = GetComparisonRange(timeRange, from, to, shiftsSnapshot, now);
        var baselineMetrics = _metricsService.CalculateRangeMetrics(
            devices[0],
            comparisonRange.From,
            comparisonRange.To);
        var baselineTotalOutput = baselineMetrics.Ok + baselineMetrics.Ng;

        // ── 峰值/谷值小时 ──
        FindPeakValleyHour(buckets, bucketOk, bucketSize == OverviewBucketSize.Day,
            out var peakHour, out int peakOk, out var valleyHour, out int valleyOk);

        // ── 单设备复盘分析：由应用服务计算，服务层返回领域结果，此处映射为绑定模型 ──
        // 只传所选设备报警（分析服务另有 DeviceId 强制过滤，双层防护防止全厂报警串入）
        prodLogsByDevice.TryGetValue(selectedDevice.Id, out var selectedProductionLogs);
        selectedProductionLogs ??= [];
        var analysis = _analysisService.Analyze(
            selectedDevice,
            statusByDevice.GetValueOrDefault(selectedDevice.Id) ?? [],
            alarmByDevice.GetValueOrDefault(selectedDevice.Id) ?? [],
            selectedProductionLogs,
            from,
            to,
            comparisonRange.From,
            comparisonRange.To);
        var topAlarms = analysis.Alarms.Select((item, index) => new AlarmOverviewSummary
        {
            Rank = index + 1, // 方案 A 排名徽章（2026-08-11）
            AlarmName = item.AlarmName,
            DeviceName = item.DeviceName,
            PlcAddress = item.PlcAddress,
            TriggerCount = item.TriggerCount,
            AverageIntervalMinutes = item.AverageIntervalMinutes,
            IsHighFrequency = item.IsHighFrequency,
            OutputBefore = item.OutputBefore,
            OutputAfter = item.OutputAfter,
            ShiftName = item.ShiftName,
            TotalDurationHours = item.TotalDurationHours,
        }).ToList();
        var statusTimeline = analysis.StatusTimeline.Select(item => new ReviewStatusSegment
        {
            Start = item.Start,
            End = item.End,
            StatusText = item.StatusText,
            StatusWord = item.StatusWord,
            OutputDelta = item.OutputDelta,
            AlarmCount = item.AlarmCount,
            HasNoOutput = item.HasNoOutput,
        }).ToList();
        var healthIssues = analysis.HealthIssues.ToList();
        var healthScore = analysis.HealthScore;
        var workOrderText = analysis.WorkOrderText;
        var productText = analysis.ProductText;
        var recipeText = analysis.RecipeText;

        // ── 班次对比：复用已查的批量数据，按 ShiftName 内存分组，不再重新查询 ──
        var swShifts = System.Diagnostics.Stopwatch.StartNew();
        var shiftComparisons = _metricsService.BuildShiftComparisons(
                prodLogsByDevice,
                statusByDevice,
                alarmByDevice,
                devices,
                shiftsSnapshot, // P0-1 修复 2026-09-02：后台线程必须用锁内快照（调用方传入）
                from,
                to)
            .Select(item => new ShiftComparisonSummary
            {
                ShiftName = item.ShiftName,
                OkCount = item.OkCount,
                NgCount = item.NgCount,
                AlarmCount = item.AlarmCount,
                Oee = item.Oee,
                RunTimeHours = item.RunTimeHours,
                AlarmDurationHours = item.AlarmDurationHours,
                TargetAchievementRate = item.TargetAchievementRate,
            })
            .ToList();
        swShifts.Stop();
        Log.Information("[耗时] 班次对比 {Elapsed}ms", swShifts.ElapsedMilliseconds);

        // ── 缺陷帕累托：从内存 Defect.Count 快照聚合（无历史持久化，取当前累计值） ──
        var swDefect = System.Diagnostics.Stopwatch.StartNew();
        var defectParetos = BuildDefectParetos(devices[0], from, to);
        var reviewConclusions = BuildReviewConclusions(
            totalOk, totalNg, totalAlarmCount, maxDowntimeSec,
            longestDowntimeDevice, longestDowntimeAlarm,
            shiftComparisons, defectParetos, quality, oee);
        swDefect.Stop();
        Log.Information("[耗时] 缺陷帕累托+结论 {Elapsed}ms", swDefect.ElapsedMilliseconds);

        swDevice.Stop();
        Log.Information("[耗时] 设备循环 {Elapsed}ms（其中 BuildProductionDeltas 累计 {Delta}ms）",
            swDevice.ElapsedMilliseconds, deltaMs);

        return new OverviewDashboardResult(
            From: from,
            To: to,
            ComparisonFrom: comparisonRange.From,
            ComparisonTo: comparisonRange.To,
            ComparisonLabel: comparisonRange.Label,
            BucketSize: bucketSize,
            Buckets: buckets,
            BucketOk: bucketOk,
            BucketNg: bucketNg,
            DeviceSummaries: deviceSummaries,
            TopAlarms: topAlarms,
            StatusTimeline: statusTimeline,
            ShiftComparisons: shiftComparisons,
            DefectParetos: defectParetos,
            HealthIssues: healthIssues,
            ReviewConclusions: reviewConclusions,
            TotalOk: totalOk,
            TotalNg: totalNg,
            AlarmCount: totalAlarmCount,
            PendingAlarmCount: totalPendingAlarmCount,
            QualityRate: quality,
            Oee: oee,
            Performance: performance,
            Availability: availability,
            RunTimeHours: totalRunSec / 3600.0,
            PausedTimeHours: totalPauseSec / 3600.0,
            AlarmDurationHours: totalAlarmSec / 3600.0,
            AvgTargetCycle: avgTargetCycle,
            TargetOutput: targetOutput,
            OutputAchievementRate: outputAchievementRate,
            BaselineTotalOutput: baselineTotalOutput,
            BaselineQualityRate: baselineMetrics.QualityRate,
            BaselineOee: baselineMetrics.Oee,
            OutputDelta: totalOk + totalNg - baselineTotalOutput,
            QualityRateDelta: quality - baselineMetrics.QualityRate,
            OeeDelta: oee - baselineMetrics.Oee,
            // 聚合停机/报警指标统一用状态段口径（CalculateStateDurations：重叠报警不重复计时）。
            // 注意与 LongestDowntimeHours（事件配对口径，需归因到具体报警名）不同，见设备循环内注释。
            TotalDowntimeHours: (totalAlarmSec + totalPauseSec) / 3600.0,
            AverageAlarmDurationMinutes: totalAlarmCount > 0
                ? totalAlarmSec / totalAlarmCount / 60.0
                : 0,
            MtbfHours: totalAlarmCount > 0
                ? totalRunSec / totalAlarmCount / 3600.0
                : totalRunSec / 3600.0,
            PeakHour: peakHour,
            PeakHourOk: peakOk,
            ValleyHour: valleyHour,
            ValleyHourOk: valleyOk,
            LongestDowntimeDevice: longestDowntimeDevice,
            LongestDowntimeAlarm: longestDowntimeAlarm,
            LongestDowntimeHours: maxDowntimeSec / 3600.0,
            AvailabilityLossText: string.Format(Strings.F079, availability, (1 - availability)),
            PerformanceLossText: string.Format(Strings.F124, performance, (1 - performance)),
            QualityLossText: string.Format(Strings.F193, quality, (1 - quality)),
            HealthScore: healthScore,
            CurrentWorkOrderText: workOrderText,
            CurrentProductText: productText,
            CurrentRecipeText: recipeText);
    }

    // ──────────── 时间范围与桶 ────────────

    private (DateTime From, DateTime To, string Label) GetComparisonRange(
        OverviewTimeRange timeRange,
        DateTime from,
        DateTime to,
        IReadOnlyList<ShiftConfig> shifts,
        DateTime now)
    {
        var duration = to - from;
        return timeRange switch
        {
            OverviewTimeRange.CurrentShift => GetPreviousShiftComparison(shifts, now),
            OverviewTimeRange.PreviousShift => (from - duration, from, Strings.M_EarlierShift),
            OverviewTimeRange.Today => (from.AddDays(-1), from, Strings.K257),
            OverviewTimeRange.Hour1 => (from.AddHours(-1), from, Strings.M301),
            OverviewTimeRange.Hours8 => (from.AddHours(-8), from, Strings.M302),
            OverviewTimeRange.Hours24 => (from.AddDays(-1), from, Strings.M300),
            OverviewTimeRange.Days7 => (from.AddDays(-7), from, Strings.M303),
            _ => (from - duration, from, Strings.M058),
        };
    }

    private static (DateTime From, DateTime To, string Label) GetPreviousShiftComparison(
        IReadOnlyList<ShiftConfig> shifts, DateTime now)
    {
        var (current, currentIndex) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (current == null || shifts.Count == 0)
            return (now.AddHours(-24), now, Strings.M300);
        var currentRange = current.ResolveRange(now);
        var previousIndex = (currentIndex - 1 + shifts.Count) % shifts.Count;
        var previous = shifts[previousIndex];
        var range = previous.ResolveRange(currentRange.Start.AddMinutes(-1));
        return (range.Start, range.End, string.Format(Strings.F055, previous.Name));
    }

    private static (DateTime From, DateTime To) ResolveCurrentShiftRange(
        IReadOnlyList<ShiftConfig> shifts, DateTime now)
    {
        var (shift, _) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (shift == null) return (now.AddHours(-24), now);
        var range = shift.ResolveRange(now);
        return (range.Start, now);
    }

    private static (DateTime From, DateTime To) ResolvePreviousShiftRange(
        IReadOnlyList<ShiftConfig> shifts, DateTime now)
    {
        var (current, currentIndex) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (current == null || shifts.Count == 0)
            return (now.AddHours(-24), now);

        var currentRange = current.ResolveRange(now);
        var previousIndex = (currentIndex - 1 + shifts.Count) % shifts.Count;
        var previous = shifts[previousIndex];
        var previousRange = previous.ResolveRange(currentRange.Start.AddMinutes(-1));
        return previousRange;
    }

    /// <summary>桶大小：1h→5分钟，8h/24h→1小时，7d→1天。</summary>
    private static OverviewBucketSize GetBucketSize(OverviewTimeRange timeRange)
    {
        return timeRange switch
        {
            OverviewTimeRange.Hour1 => OverviewBucketSize.Minute5,
            OverviewTimeRange.Hours8 => OverviewBucketSize.Hour,
            OverviewTimeRange.Hours24 => OverviewBucketSize.Hour,
            OverviewTimeRange.Days7 => OverviewBucketSize.Day,
            _ => OverviewBucketSize.Hour,
        };
    }

    // ──────────── 报警 / 峰值 / 帕累托 / 结论（纯函数辅助） ────────────

    private static int CountPendingAlarms(List<AlarmEventRecord> events)
    {
        // 按 AlarmId 分组，最后一条是 Triggered 且无 Recovered → 待处理
        return events
            .GroupBy(e => e.AlarmId)
            .Count(g => g.OrderByDescending(e => e.EventTime).First().EventType == AlarmEventType.Triggered);
    }

    /// <summary>
    /// 计算 Triggered→Recovered 配对时长：按事件顺序扫描，每个 Triggered 配下一个 Recovered，算时长。
    /// P2-13 口径修复：① 未恢复 Triggered 用 min(now, windowTo) 截断（历史窗口不再算到"现在"）；
    /// ② 每个 Recovered 只消费一次（连续 T1,T2,R1 时 T1 配 R1、T2 未恢复按截断值，避免重复配对高估）。
    /// </summary>
    private static Dictionary<string, double> PairAlarmDurations(
        IEnumerable<AlarmEventRecord> events,
        Func<AlarmEventRecord, string> keySelector,
        DateTime windowTo)
    {
        return events
            .GroupBy(keySelector)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var sorted = g.OrderBy(e => e.EventTime).ToList();
                    double total = 0;
                    var consumed = new HashSet<AlarmEventRecord>();
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (sorted[i].EventType != AlarmEventType.Triggered) continue;
                        // 找下一个未消费的 Recovered；未恢复则截断到 min(now, windowTo)
                        DateTime end = windowTo < DateTime.Now ? windowTo : DateTime.Now;
                        for (int j = i + 1; j < sorted.Count; j++)
                        {
                            if (sorted[j].EventType == AlarmEventType.Recovered && consumed.Add(sorted[j]))
                            {
                                end = sorted[j].EventTime;
                                break;
                            }
                        }
                        total += (end - sorted[i].EventTime).TotalSeconds;
                    }
                    return total;
                });
    }

    /// <summary>
    /// 找持续时间最长的报警（按 AlarmName 分组取 Triggered→Recovered 配对总时长最大者）。
    /// 复用 PairAlarmDurations 避免 FindLongestAlarm 与 CalcAlarmDurationHours 重复实现配对逻辑。
    /// </summary>
    private static (string Name, double Seconds) FindLongestAlarm(List<AlarmEventRecord> events, DateTime windowTo)
    {
        if (events.Count == 0) return (string.Empty, 0);
        var durations = PairAlarmDurations(events, e => e.AlarmName, windowTo);
        if (durations.Count == 0) return (string.Empty, 0);
        var max = durations.Aggregate((a, b) => a.Value >= b.Value ? a : b);
        return (max.Key, max.Value);
    }

    // ──────────── 峰值/谷值 ────────────

    private static void FindPeakValleyHour(DateTime[] buckets, int[] okCounts, bool dayBuckets,
        out string peakHour, out int peakOk, out string valleyHour, out int valleyOk)
    {
        peakHour = "—"; valleyHour = "—";
        peakOk = 0; valleyOk = 0;
        if (buckets.Length == 0) return;

        int maxIdx = 0, minIdx = 0;
        for (int i = 1; i < okCounts.Length; i++)
        {
            if (okCounts[i] > okCounts[maxIdx]) maxIdx = i;
            if (okCounts[i] < okCounts[minIdx]) minIdx = i;
        }
        peakOk = okCounts[maxIdx];
        valleyOk = okCounts[minIdx];
        // 审查修复 2026-08-13：天粒度桶（近 7 天）的时间分量恒为 00:00——"HH:mm" 标签全部显示 00:00，
        // 用户无法知道峰值是哪一天；按桶粒度区分格式
        var format = dayBuckets ? "MM-dd" : "HH:mm";
        peakHour = buckets[maxIdx].ToString(format);
        valleyHour = buckets[minIdx].ToString(format);
    }

    /// <summary>
    /// 从内存中的 Defect.Count 聚合缺陷帕累托数据。
    /// 由于缺陷计数无历史持久化，此处取当前设备配置中各缺陷的累计 Count 快照。
    /// 按数量降序排列并计算累计占比。
    /// </summary>
    private List<DefectParetoSummary> BuildDefectParetos(Device device, DateTime from, DateTime to)
    {
        if (_defectHistoryStore == null)
            return [];

        var snapshots = _defectHistoryStore.QueryWindowBounds(from, to, device.Id);
        var list = new List<DefectParetoSummary>();
        foreach (var group in snapshots.GroupBy(snapshot => new { snapshot.DefectId, snapshot.ShiftName }))
        {
            var ordered = group.OrderBy(snapshot => snapshot.Timestamp).ToList();
            var window = ordered.Where(snapshot => snapshot.Timestamp >= from && snapshot.Timestamp <= to).ToList();
            if (window.Count == 0) continue;
            var first = window[0];
            var last = window[^1];
            var baseline = ordered.LastOrDefault(snapshot => snapshot.Timestamp < from);
            // 口径统一（2026-08-16）：一律按窗口内增量算——
            // 有窗口前基线用 末值-基线（完整窗口增量）；无基线用 窗口内末值-首值；
            // 窗口内仅一条且无基线时增量无法推算，取 0（原实现把累计值当增量，会高估）。
            var count = baseline != null
                ? Math.Max(0, last.Count - baseline.Count)
                : Math.Max(0, last.Count - first.Count);
            if (count <= 0) continue;
            list.Add(new DefectParetoSummary
            {
                DefectName = last.DefectName,
                DeviceName = last.DeviceName,
                Count = count,
            });
        }

        // 按数量降序取 Top3（2026-08-10 用户要求：卡片最多显示 3 条，突出头部缺陷）
        list = list.OrderByDescending(d => d.Count).Take(3).ToList();
        var total = list.Sum(d => d.Count);
        if (total <= 0) return list;

        double cum = 0;
        foreach (var d in list)
        {
            cum += d.Count;
            d.CumulativePercent = cum * 100.0 / total;
        }
        return list;
    }

    private static List<ReviewConclusion> BuildReviewConclusions(
        int totalOk,
        int totalNg,
        int totalAlarmCount,
        double longestDowntimeSec,
        string longestDowntimeDevice,
        string longestDowntimeAlarm,
        IReadOnlyList<ShiftComparisonSummary> shifts,
        IReadOnlyList<DefectParetoSummary> defects,
        double quality,
        double oee)
    {
        const double qualityTarget = KpiThresholds.QualityGood;
        const double oeeTarget = KpiThresholds.OeeGood;

        List<ReviewConclusion> result = [];
        var total = totalOk + totalNg;
        if (total == 0 && totalAlarmCount == 0)
            return [new ReviewConclusion { Text = Strings.M114, Kind = ReviewConclusionKind.Info, Met = ReviewConclusionMetState.Neutral }];

        if (total > 0)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F194, quality, (quality >= qualityTarget ? Strings.M112 : Strings.M113), qualityTarget),
                Kind = ReviewConclusionKind.Quality,
                Met = quality >= qualityTarget ? ReviewConclusionMetState.Met : ReviewConclusionMetState.NotMet,
            });
        }

        if (oee > 0)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F006, oee, (oee >= oeeTarget ? Strings.M112 : Strings.M113), oeeTarget),
                Kind = ReviewConclusionKind.Oee,
                Met = oee >= oeeTarget ? ReviewConclusionMetState.Met : ReviewConclusionMetState.NotMet,
            });
        }

        if (longestDowntimeSec > 0)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F138, longestDowntimeSec / 3600.0, longestDowntimeDevice, longestDowntimeAlarm),
                Kind = ReviewConclusionKind.Downtime,
                Met = ReviewConclusionMetState.Neutral,
            });
        }

        var topShift = shifts.Where(s => s.TotalCount > 0).OrderByDescending(s => s.TotalCount).FirstOrDefault();
        if (topShift != null)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F041, topShift.ShiftName, topShift.TotalCount, topShift.OkRatio),
                Kind = ReviewConclusionKind.BestShift,
                Met = ReviewConclusionMetState.Neutral,
            });
        }

        var topDefect = defects.FirstOrDefault();
        if (topDefect != null)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F059, topDefect.DefectName, topDefect.DeviceName, topDefect.Count, topDefect.CumulativePercent),
                Kind = ReviewConclusionKind.TopDefect,
                Met = ReviewConclusionMetState.Neutral,
            });
        }

        if (totalAlarmCount > 0 && result.Count < 5)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F123, totalAlarmCount),
                Kind = ReviewConclusionKind.AlarmCount,
                Met = ReviewConclusionMetState.Neutral,
            });
        }

        return result.Take(5).ToList();
    }
}
