using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CsvHelper.Configuration.Attributes;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using OxyPlot;
using Serilog;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

public partial class StatusQueryViewModel : ObservableObject
{
    private readonly IStatusTransitionHistoryService _historyService;

    /// <summary>最近一次查询的全量结果（按时间降序缓存，供翻页内存分页复用，避免每次翻页重新全量查询）。</summary>
    private List<StatusTransitionRecord> _allTransitions = [];

    [ObservableProperty]
    private ObservableCollection<StatusTransitionRecord> _statusTransitions = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTimeFormatted))]
    private double _runTimeSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlarmTimeFormatted))]
    private double _alarmTimeSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PausedTimeFormatted))]
    private double _pausedTimeSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineTimeFormatted))]
    private double _offlineTimeSeconds;

    /// <summary>运行时长格式化（Xd Yh / Xh Ym / Xm），便于人眼阅读。</summary>
    public string RunTimeFormatted => FormatDuration(RunTimeSeconds);
    /// <summary>报警时长格式化。</summary>
    public string AlarmTimeFormatted => FormatDuration(AlarmTimeSeconds);
    /// <summary>暂停时长格式化。</summary>
    public string PausedTimeFormatted => FormatDuration(PausedTimeSeconds);
    /// <summary>离线时长格式化（仅统计，不参与 OEE）。</summary>
    public string OfflineTimeFormatted => FormatDuration(OfflineTimeSeconds);

    /// <summary>秒数 → "Xd Yh" / "Xh Ym" / "Xm" 格式（与旧实现逐分支等价，委托跨进程单源，
    /// 避免与 HomeViewModel 等处的时长口径分叉）。</summary>
    private static string FormatDuration(double seconds)
        => Kanban.Contracts.Formatting.DurationFormatter.FormatCompact(seconds);

    [ObservableProperty]
    private PlotModel? _statusChart;

    [ObservableProperty]
    private PlotModel? _statusBarChart;

    [ObservableProperty]
    private PlotModel? _statusGanttChart;

    [ObservableProperty]
    private string? _statusInsight;

    public string? QueryError { get; private set; }

    public StatusQueryViewModel(IStatusTransitionHistoryService historyService)
    {
        _historyService = historyService;
    }

    public (int TotalCount, int TotalPages) Query(
        string? deviceId, DateTime from, DateTime to, string? shiftName, int currentPage, int pageSize)
    {
        StatusTransitions.Clear();
        RunTimeSeconds = 0; AlarmTimeSeconds = 0; PausedTimeSeconds = 0; OfflineTimeSeconds = 0;
        StatusInsight = null;
        QueryError = null;

        if (deviceId == null)
        {
            StatusChart = null; StatusBarChart = null; StatusGanttChart = null;
            return (0, 0);
        }

        try
        {
            var allInRange = QueryStatusTransitions(deviceId, from, to, shiftName);

            // 缓存降序全量（ThenBy Id 稳定次级键）用于翻页；时长/Gantt 计算仍用原始升序 allInRange
            _allTransitions = allInRange.OrderByDescending(s => s.EventTime).ThenByDescending(s => s.Id).ToList();

            var totalCount = allInRange.Count;
            var totalPages = HistoryQueryHelper.CalcTotalPages(totalCount, pageSize);

            Page(currentPage, pageSize);

            int initialState = 1;
            var lastBefore = GetLatestStatusBefore(deviceId, from, shiftName);
            if (lastBefore != null)
                initialState = lastBefore.CurrentState;

            var effectiveTo = HistoryQueryHelper.ClampToNow(to);

            var durations = OeeCalculator.CalculateStateDurations(allInRange, from, effectiveTo, initialState);
            RunTimeSeconds = durations.RunTime;
            AlarmTimeSeconds = durations.AlarmTime;
            PausedTimeSeconds = durations.PausedTime;
            OfflineTimeSeconds = durations.OfflineTime;

            var dailyDurations = BuildDailyDurations(allInRange, from, effectiveTo, initialState);
            StatusChart = ChartService.BuildStatusChart(dailyDurations);
            StatusBarChart = ChartService.BuildStatusBarChart(dailyDurations);
            StatusGanttChart = ChartService.BuildStatusGanttChart(
                BuildGanttSegments(allInRange, from, effectiveTo, initialState));

            StatusInsight = BuildStatusInsight(allInRange, from, to, initialState);

            return (totalCount, totalPages);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "状态查询失败");
            QueryError = string.Format(Strings.F165, ex.Message);
            return (0, 0);
        }
    }

    /// <summary>翻页：从缓存全量结果内存分页，不重新查询（时长/图表不变）。</summary>
    public void Page(int currentPage, int pageSize)
    {
        var pageItems = HistoryQueryHelper.PageItems(_allTransitions, currentPage, pageSize);
        StatusTransitions.Clear();
        foreach (var item in pageItems)
            StatusTransitions.Add(item);
    }

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
        StatusTransitions.Clear();
        _allTransitions = [];
        RunTimeSeconds = 0; AlarmTimeSeconds = 0; PausedTimeSeconds = 0; OfflineTimeSeconds = 0;
        StatusChart = null; StatusBarChart = null; StatusGanttChart = null;
        StatusInsight = null;
    }

    public string? BuildCsv() => BuildCsvCore(StatusTransitions);

    /// <summary>导出全部筛选结果（跨页合并，供导出范围选择"全量"时调用）。</summary>
    public string? BuildCsvAll() => BuildCsvCore(_allTransitions);

    private string? BuildCsvCore(IReadOnlyList<StatusTransitionRecord> source)
    {
        if (source.Count == 0) return null;

        var rows = source.Select(s => new StatusCsvRow
        {
            Timestamp = s.EventTime,
            DeviceId = s.DeviceId,
            DeviceName = s.DeviceName,
            PrevState = s.PreviousState,
            CurrState = s.CurrentState,
            PrevStateText = HistoryQueryHelper.GetStateText(s.PreviousState),
            CurrStateText = HistoryQueryHelper.GetStateText(s.CurrentState, s.OfflineCause)
        }).ToList();

        return HistoryQueryHelper.BuildCsv(rows,
            string.Format(Strings.M354, RunTimeSeconds / 3600, AlarmTimeSeconds / 3600, PausedTimeSeconds / 3600),
            $"# {StatusInsight ?? Strings.M176}");
    }

    private static string? BuildStatusInsight(List<StatusTransitionRecord> transitions,
        DateTime from, DateTime to, int initialState)
    {
        var segments = BuildGanttSegments(transitions, from, to, initialState).ToList();
        if (segments.Count == 0) return null;

        // 阈值定义（与 HistoryQueryViewModel.NgAlarmThreshold / LowPerformanceThreshold 对齐，
        // 复用既有"报警/性能"阈值语义，避免新增未使用的常量）
        const double LongAlarmThresholdMin = 30;        // 单次报警 > 30 分钟视为长报警
        const double HighPauseRatioThreshold = 0.20;    // 暂停/报警占比 > 20% 视为异常

        List<string> parts = [];

        // 最长运行段
        var runSegments = segments.Where(s => s.State == 1).ToList();
        if (runSegments.Count > 0)
        {
            var longestRun = runSegments.MaxBy(s => s.End - s.Start);
            var minutes = (longestRun.End - longestRun.Start).TotalMinutes;
            if (minutes > 0)
                parts.Add(string.Format(Strings.F140, minutes, longestRun.Start, longestRun.End));
        }

        // 最长报警段（含长报警阈值检测）
        var alarmSegments = segments.Where(s => s.State == 2).ToList();
        if (alarmSegments.Count > 0)
        {
            var longestAlarm = alarmSegments.MaxBy(s => s.End - s.Start);
            var minutes = (longestAlarm.End - longestAlarm.Start).TotalMinutes;
            if (minutes > 0)
            {
                var prefix = minutes > LongAlarmThresholdMin ? Strings.M177 : Strings.M178;
                parts.Add(string.Format(Strings.F035, prefix, minutes, longestAlarm.Start, longestAlarm.End));
            }
        }

        // 最长暂停段
        var pauseSegments = segments.Where(s => s.State == 3).ToList();
        if (pauseSegments.Count > 0)
        {
            var longestPause = pauseSegments.MaxBy(s => s.End - s.Start);
            var minutes = (longestPause.End - longestPause.Start).TotalMinutes;
            if (minutes > 0)
                parts.Add(string.Format(Strings.F139, minutes, longestPause.Start, longestPause.End));
        }

        // 占比异常检测：总报警/暂停时长 / 窗口时长 > 阈值时主动提示
        var totalSpan = (to - from).TotalSeconds;
        if (totalSpan > 0)
        {
            var alarmRatio = alarmSegments.Sum(s => (s.End - s.Start).TotalSeconds) / totalSpan;
            var pauseRatio = pauseSegments.Sum(s => (s.End - s.Start).TotalSeconds) / totalSpan;
            if (pauseRatio > HighPauseRatioThreshold)
                parts.Add(string.Format(Strings.F045, pauseRatio, HighPauseRatioThreshold));
            else if (alarmRatio > HighPauseRatioThreshold)
                parts.Add(string.Format(Strings.F049, alarmRatio, HighPauseRatioThreshold));
        }

        return parts.Count > 0 ? string.Join("，", parts) : null;
    }

    private static IEnumerable<(DateTime Date, double RunHours, double AlarmHours, double PauseHours)>
        BuildDailyDurations(List<StatusTransitionRecord> transitions, DateTime from, DateTime to, int initialState)
    {
        List<(DateTime Start, DateTime End, int State)> rawSegments = [];
        var currentState = initialState;
        var segStart = from;
        foreach (var t in transitions)
        {
            if (t.EventTime < from) continue;
            if (t.EventTime > segStart)
                rawSegments.Add((segStart, t.EventTime, currentState));
            currentState = t.CurrentState;
            segStart = t.EventTime;
        }
        if (segStart < to)
            rawSegments.Add((segStart, to, currentState));

        Dictionary<DateTime, (double Run, double Alarm, double Pause)> byDay = [];
        foreach (var seg in rawSegments)
        {
            var cursor = seg.Start;
            while (cursor < seg.End)
            {
                var nextMidnight = cursor.Date.AddDays(1);
                var end = seg.End < nextMidnight ? seg.End : nextMidnight;
                var secs = (end - cursor).TotalSeconds;
                if (secs > 0)
                {
                    var day = cursor.Date;
                    var acc = byDay.TryGetValue(day, out var a) ? a : (0, 0, 0);
                    if (seg.State == (int)DeviceStatus.Running) acc.Run += secs;
                    else if (seg.State == (int)DeviceStatus.Alarm) acc.Alarm += secs;
                    else if (seg.State == (int)DeviceStatus.Paused) acc.Pause += secs;
                    byDay[day] = acc;
                }
                cursor = nextMidnight;
            }
        }

        return byDay
            .OrderBy(kv => kv.Key)
            .Select(kv => (
                Date: kv.Key,
                RunHours: kv.Value.Run / 3600.0,
                AlarmHours: kv.Value.Alarm / 3600.0,
                PauseHours: kv.Value.Pause / 3600.0
            ));
    }

    internal static IEnumerable<(DateTime Start, DateTime End, int State)>
        BuildGanttSegments(List<StatusTransitionRecord> transitions, DateTime from, DateTime to, int initialState)
    {
        var currentState = initialState;
        var segStart = from;

        foreach (var t in transitions)
        {
            if (t.EventTime <= from) continue;
            if (t.EventTime > to) break;

            if (t.EventTime > segStart)
                yield return (segStart, t.EventTime, currentState);

            currentState = t.CurrentState;
            segStart = t.EventTime;
        }

        if (segStart < to)
            yield return (segStart, to, currentState);
    }

    private class StatusCsvRow
    {
        [Name("事件时间")] public DateTime Timestamp { get; set; }
        [Name("设备ID")] public string? DeviceId { get; set; }
        [Name("设备名称")] public string? DeviceName { get; set; }
        [Name("前一状态")] public int PrevState { get; set; }
        [Name("当前状态")] public int CurrState { get; set; }
        [Name("前一状态文本")] public string? PrevStateText { get; set; }
        [Name("当前状态文本")] public string? CurrStateText { get; set; }
    }
}
