using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CsvHelper.Configuration.Attributes;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using OxyPlot;
using Serilog;

namespace MainAPP.ViewModels;

public partial class OeeQueryViewModel : ObservableObject
{
    private readonly IHistoryService _historyService;
    private readonly DeviceRepository _deviceRepository;
    private readonly AppSettings _appSettings;

    [ObservableProperty]
    private double _oeeQualityRate;

    [ObservableProperty]
    private double _oeePerformanceRate;

    [ObservableProperty]
    private double _oeeAvailabilityRate;

    [ObservableProperty]
    private double _oeeValue;

    [ObservableProperty]
    private int _oeeOkProduction;

    [ObservableProperty]
    private int _oeeNgProduction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OeeRunTimeHours))]
    private double _oeeRunTime;

    [ObservableProperty]
    private double _oeeAlarmTime;

    /// <summary>
    /// 运行时长（小时），用于 UI 显示。与 OverviewView.RunTimeHours 单位一致，
    /// 避免查询页用秒、概览页用小时造成同一指标跨页单位冲突。
    /// 源值 OeeRunTime 为秒，此处除以 3600 转换。
    /// </summary>
    public double OeeRunTimeHours => OeeRunTime / 3600.0;

    [ObservableProperty]
    private int _oeeTargetCycle;

    [ObservableProperty]
    private ObservableCollection<ShiftOeeRecord> _oeeShiftDetails = new();

    [ObservableProperty]
    private PlotModel? _oeeChart;

    [ObservableProperty]
    private PlotModel? _oeeTrendChart;

    [ObservableProperty]
    private PlotModel? _oeeShiftBarChart;

    [ObservableProperty]
    private string? _oeeInsight;

    public string? QueryError { get; private set; }

    public OeeQueryViewModel(IHistoryService historyService,
        DeviceRepository deviceRepository, AppSettings appSettings)
    {
        _historyService = historyService;
        _deviceRepository = deviceRepository;
        _appSettings = appSettings;
    }

    public (int TotalCount, int TotalPages) Query(
        string? deviceId, DateTime from, DateTime to, string? shiftName)
    {
        OeeQualityRate = 0; OeePerformanceRate = 0; OeeAvailabilityRate = 0; OeeValue = 0;
        OeeOkProduction = 0; OeeNgProduction = 0; OeeRunTime = 0; OeeAlarmTime = 0; OeeTargetCycle = 0;
        OeeInsight = null;
        OeeShiftDetails.Clear();
        QueryError = null;

        if (deviceId == null)
        {
            OeeChart = null; OeeTrendChart = null; OeeShiftBarChart = null;
            return (0, 0);
        }

        try
        {
            var device = _deviceRepository.Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device == null) return (0, 0);
            OeeTargetCycle = device.TargetCycle;

            int okProd = 0, ngProd = 0;
            var inRange = QueryProductionLogs(from, to, deviceId, shiftName);

            if (inRange.Count > 0)
            {
                // 窗口基准：窗口起点之前、同班次实例的累计值，用于窗口差分（产量与时间同口径）
                var baselineCandidates = QueryProductionLogs(from.AddDays(-1), from, deviceId, null);

                (okProd, ngProd) = HistoryQueryHelper.SumWindowProduction(inRange, baselineCandidates, from);
            }
            OeeOkProduction = okProd;
            OeeNgProduction = ngProd;

            var transitions = QueryStatusTransitions(deviceId, from, to, shiftName);
            int initialState = 1;
            var lastBefore = GetLatestStatusBefore(deviceId, from, shiftName);
            if (lastBefore != null)
                initialState = lastBefore.CurrentState;

            var effectiveTo = HistoryQueryHelper.ClampToNow(to);
            var durations = OeeCalculator.CalculateStateDurations(transitions, from, effectiveTo, initialState);
            OeeRunTime = durations.RunTime;
            OeeAlarmTime = durations.AlarmTime;

            OeeQualityRate = OeeCalculator.CalculateQualityRate(okProd, ngProd);
            OeePerformanceRate = OeeCalculator.CalculatePerformanceRate(okProd, ngProd, device.TargetCycle, durations.RunTime);
            OeeAvailabilityRate = OeeCalculator.CalculateAvailabilityRate(durations.RunTime, durations.AlarmTime);
            OeeValue = OeeCalculator.CalculateOee(OeeQualityRate, OeePerformanceRate, OeeAvailabilityRate);

            OeeChart = ChartService.BuildOeeChart(OeeQualityRate, OeePerformanceRate, OeeAvailabilityRate, OeeValue);

            var perShiftOee = ComputePerShiftOee(deviceId, device.TargetCycle, transitions, initialState, from, to);
            OeeTrendChart = ChartService.BuildOeeTrendChart(
                perShiftOee.Select(s => (s.ShiftTime, s.Oee, s.ShiftName)));
            OeeShiftBarChart = ChartService.BuildOeeShiftBarChart(
                perShiftOee.Select(s => (s.ShiftName, s.Oee)));

            OeeShiftDetails.Clear();
            foreach (var s in perShiftOee)
                OeeShiftDetails.Add(s);

            OeeInsight = BuildOeeInsight(OeeQualityRate, OeePerformanceRate, OeeAvailabilityRate, perShiftOee);

            return (1, 1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OEE 查询失败: {Message}", ex.Message);
            QueryError = $"OEE 历史查询失败：{ex.Message}";
            return (0, 0);
        }
    }

    private List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId, string? shiftName)
        => _historyService is IHistoryQueryExecutor strict
            ? strict.QueryProductionLogsStrict(from, to, deviceId, shiftName)
            : _historyService.QueryProductionLogs(from, to, deviceId, shiftName);

    private List<StatusTransitionRecord> QueryStatusTransitions(string deviceId, DateTime from, DateTime to, string? shiftName)
        => _historyService is IHistoryQueryExecutor strict
            ? strict.QueryStatusTransitionsStrict(deviceId, from, to, shiftName)
            : _historyService.QueryStatusTransitions(deviceId, from, to, shiftName);

    private StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName)
        => _historyService is IHistoryQueryExecutor strict
            ? strict.GetLatestStatusBeforeStrict(deviceId, before, shiftName)
            : _historyService.GetLatestStatusBefore(deviceId, before, shiftName);

    public void Reset()
    {
        OeeQualityRate = 0; OeePerformanceRate = 0; OeeAvailabilityRate = 0; OeeValue = 0;
        OeeOkProduction = 0; OeeNgProduction = 0; OeeRunTime = 0; OeeAlarmTime = 0; OeeTargetCycle = 0;
        OeeChart = null; OeeTrendChart = null; OeeShiftBarChart = null;
        OeeInsight = null;
        OeeShiftDetails.Clear();
    }

    public string? BuildCsv(string? deviceId)
    {
        if (OeeValue == 0 && OeeOkProduction == 0) return null;

        List<OeeCsvRow> rows = [
            new() { Metric = "C良品率", Value = OeeQualityRate.ToString("F4", CultureInfo.InvariantCulture) },
            new() { Metric = "B性能达标率", Value = OeePerformanceRate.ToString("F4", CultureInfo.InvariantCulture) },
            new() { Metric = "A时间稼动率", Value = OeeAvailabilityRate.ToString("F4", CultureInfo.InvariantCulture) },
            new() { Metric = "OEE综合", Value = OeeValue.ToString("F4", CultureInfo.InvariantCulture) },
            new() { Metric = "OK产量", Value = OeeOkProduction.ToString(CultureInfo.InvariantCulture) },
            new() { Metric = "NG产量", Value = OeeNgProduction.ToString(CultureInfo.InvariantCulture) },
            new() { Metric = "运行时长(s)", Value = OeeRunTime.ToString("F0", CultureInfo.InvariantCulture) },
            new() { Metric = "报警时长(s)", Value = OeeAlarmTime.ToString("F0", CultureInfo.InvariantCulture) },
            new() { Metric = "目标节拍(件/小时)", Value = OeeTargetCycle.ToString(CultureInfo.InvariantCulture) }
        ];

        return HistoryQueryHelper.BuildCsv(rows,
            $"# 设备：{deviceId}",
            $"# {OeeInsight ?? "无洞察"}");
    }

    private List<ShiftOeeRecord> ComputePerShiftOee(
        string deviceId, int targetCycle,
        List<StatusTransitionRecord> allTransitions, int initialInitialState,
        DateTime fromDate, DateTime toDate)
    {
        List<ShiftOeeRecord> result = [];
        var now = DateTime.Now;

        var allProd = QueryProductionLogs(fromDate, toDate, deviceId, null);

        var shiftGroups = HistoryQueryHelper.SplitShiftInstances(allProd);

        var sortedTrans = allTransitions.OrderBy(t => t.EventTime).ToList();

        foreach (var group in shiftGroups)
        {
            var firstLog = group.First();
            var lastLog = group.Last();
            var shiftName = firstLog.ShiftName;

            var shiftConfig = _appSettings.Shifts?.FirstOrDefault(s => s.Name == shiftName);
            DateTime shiftFrom, shiftTo;
            if (shiftConfig != null)
            {
                var (start, end) = shiftConfig.ResolveRange(firstLog.Timestamp);
                shiftFrom = start;
                shiftTo = end;
                if (shiftFrom < fromDate) shiftFrom = fromDate;
                if (shiftTo > toDate) shiftTo = toDate;
                if (shiftTo > now) shiftTo = now;
            }
            else
            {
                shiftFrom = firstLog.Timestamp;
                shiftTo = lastLog.Timestamp;
                if (shiftTo > now) shiftTo = now;
            }

            int okBase = 0, ngBase = 0;
            if (group.First().Timestamp <= shiftFrom)
            {
                // 窗口起点恰有快照：直接以该快照累计值作基准
                okBase = group.First().OkProduction;
                ngBase = group.First().NgProduction;
            }
            else
            {
                // 窗口起点在班次实例内部：取窗口前最近、且属同一班次实例的快照累计值
                var baseLog = HistoryQueryHelper.FindBaselineBeforeWindow(
                    QueryProductionLogs(shiftFrom.AddDays(-1), shiftFrom, deviceId, null),
                    shiftName);
                if (baseLog == null)
                {
                    // 基准缺失：旧实现记 0 导致整班次产量被算进窗口、OEE 虚高。
                    // 改为回退到班次实例内首条快照累计值（只统计窗口可见部分），
                    // 与 SumWindowProduction 的处理保持一致。
                    okBase = group.First().OkProduction;
                    ngBase = group.First().NgProduction;
                }
                else
                {
                    okBase = baseLog.OkProduction;
                    ngBase = baseLog.NgProduction;
                }
            }
            int ok = Math.Max(0, lastLog.OkProduction - okBase);
            int ng = Math.Max(0, lastLog.NgProduction - ngBase);

            var subTrans = sortedTrans
                .Where(t => t.EventTime >= shiftFrom && t.EventTime <= shiftTo)
                .ToList();
            int initState = 1;
            var prevBefore = sortedTrans.LastOrDefault(t => t.EventTime < shiftFrom);
            if (prevBefore != null)
                initState = prevBefore.CurrentState;
            else if (shiftFrom <= fromDate) initState = initialInitialState;

            var durations = OeeCalculator.CalculateStateDurations(subTrans, shiftFrom, shiftTo, initState);

            double quality = OeeCalculator.CalculateQualityRate(ok, ng);
            double perf = OeeCalculator.CalculatePerformanceRate(ok, ng, targetCycle, durations.RunTime);
            double avail = OeeCalculator.CalculateAvailabilityRate(durations.RunTime, durations.AlarmTime);
            double oee = OeeCalculator.CalculateOee(quality, perf, avail);

            result.Add(new ShiftOeeRecord(shiftFrom, shiftName, quality, perf, avail, oee));
        }

        return result;
    }

    private static string? BuildOeeInsight(double q, double p, double a, List<ShiftOeeRecord> perShiftOee)
    {
        if (q == 0 && p == 0 && a == 0) return null;

        var items = new[]
        {
            (Name: "C良品率", Value: q),
            (Name: "B性能达标率", Value: p),
            (Name: "A时间稼动率", Value: a)
        };
        var min = items.MinBy(x => x.Value);
        var max = items.MaxBy(x => x.Value);

        string main;
        if (min.Value < HistoryQueryViewModel.LowPerformanceThreshold)
            main = $"⚠ 瓶颈因子：{min.Name} 仅 {min.Value:P1}，是 OEE 的主要拖累项";
        else if (min.Value < max.Value - 0.1)
            main = $"📊 {max.Name} 表现最佳 ({max.Value:P1})，{min.Name} 偏低 ({min.Value:P1})";
        else
            main = $"✓ 三项指标均衡（{q:P0} / {p:P0} / {a:P0}），OEE = {q * p * a:P1}";

        var shiftInsight = BuildShiftComparisonInsight(perShiftOee);
        return shiftInsight == null ? main : $"{main}\n{shiftInsight}";
    }

    /// <summary>
    /// 班次对比洞察：识别表现明显落后的班次。
    /// 至少 2 个班次实例才有对比意义；以平均 OEE 为基准，找出最低班次，
    /// 若与最高班次差距 ≥ 10 个百分点，则定位拖累项（Q/P/A 中低于均值最多的因子）并给出提示。
    /// </summary>
    private static string? BuildShiftComparisonInsight(List<ShiftOeeRecord> perShiftOee)
    {
        if (perShiftOee == null || perShiftOee.Count < 2) return null;

        var worst = perShiftOee.MinBy(s => s.Oee);
        var best = perShiftOee.MaxBy(s => s.Oee);
        if (worst == null || best == null) return null;
        if (worst.Oee >= best.Oee) return null;

        var gap = best.Oee - worst.Oee;
        if (gap < 0.10) return null;  // 差距 < 10pp 视为正常波动，不报告

        // 定位 worst 班次的拖累项：与 best 班次的 Q/P/A 比较，差距最大的因子即主因
        var factors = new[]
        {
            (Name: "C良品率", Diff: worst.Quality - best.Quality, Worst: worst.Quality, Best: best.Quality),
            (Name: "B性能达标率", Diff: worst.Performance - best.Performance, Worst: worst.Performance, Best: best.Performance),
            (Name: "A时间稼动率", Diff: worst.Availability - best.Availability, Worst: worst.Availability, Best: best.Availability)
        };
        var drag = factors.MinBy(f => f.Diff);

        var timeLabel = worst.ShiftTime.ToString("MM-dd HH:mm");
        var dragHint = drag!.Diff < -0.05
            ? $"，{drag.Name} 是主要拖累项（{drag.Worst:P0} vs {drag.Best:P0}）"
            : "";
        return $"🔻 差班次：{worst.ShiftName} @ {timeLabel}（OEE {worst.Oee:P0}），" +
               $"低于最佳班次 {gap:P0}{dragHint}";
    }

    private class OeeCsvRow
    {
        [Name("指标")] public string? Metric { get; set; }
        [Name("值")] public string? Value { get; set; }
    }
}