using System.Collections.ObjectModel;
using System.Text;
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

public partial class AlarmQueryViewModel : ObservableObject
{
    private readonly IAlarmHistoryService _historyService;

    [ObservableProperty]
    private ObservableCollection<AlarmEventRecord> _alarmEvents = new();

    [ObservableProperty]
    private int _alarmTriggerCount;

    [ObservableProperty]
    private int _alarmRecoverCount;

    [ObservableProperty]
    private int _alarmPendingCount;

    [ObservableProperty]
    private PlotModel? _alarmChart;

    [ObservableProperty]
    private string? _alarmInsight;

    public List<string> LastQueryAlarmNames { get; private set; } = new();
    public string? QueryError { get; private set; }

    public AlarmQueryViewModel(IAlarmHistoryService historyService)
    {
        _historyService = historyService;
    }

    public (int TotalCount, int TotalPages) Query(
        string? deviceId, DateTime from, DateTime to, string? shiftName, string? alarmName,
        int currentPage, int pageSize)
    {
        AlarmEvents.Clear();
        AlarmTriggerCount = 0; AlarmRecoverCount = 0; AlarmPendingCount = 0;
        AlarmInsight = null;
        AlarmChart = null;
        LastQueryAlarmNames.Clear();
        QueryError = null;

        if (deviceId == null)
            return (0, 0);

        try
        {
            var list = QueryAlarmEvents(from, to, deviceId, shiftName);

            if (alarmName != null)
                list = list.Where(e => e.AlarmName == alarmName).ToList();

            var totalCount = list.Count;
            var totalPages = HistoryQueryHelper.CalcTotalPages(totalCount, pageSize);

            var pageItems = HistoryQueryHelper.PageItems(list, currentPage, pageSize);
            foreach (var item in pageItems)
                AlarmEvents.Add(item);

            AlarmTriggerCount = list.Count(e => e.EventType == AlarmEventType.Triggered);
            AlarmRecoverCount = list.Count(e => e.EventType == AlarmEventType.Recovered);

            // 待恢复计数：最近事件为 Triggered 的报警。
            // 注意：最近事件为 ShiftChange 的报警表示"班次切换后已重新开始计时"，
            // 不应算作待恢复（否则跨班次的报警会被双重计数）。
            AlarmPendingCount = list
                .GroupBy(e => new { e.DeviceId, e.AlarmId })
                .Count(g => g.OrderByDescending(e => e.EventTime).First().EventType == AlarmEventType.Triggered);

            LastQueryAlarmNames = list
                .Where(e => !string.IsNullOrEmpty(e.AlarmName))
                .Select(e => e.AlarmName!)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            // 班次切换事件（ShiftChange）表示"报警在新班次重新开始计时"，不是物理恢复。
            // 仅 Recovered 事件计入时长差分，避免跨班次报警被错误压缩为"在班次切换点恢复"。
            // 贪心配对（审查修复 2026-08-13）：每条 Recovered 与其前最近一条**未配对** Triggered 配对。
            // 原实现每个 Triggered 找其后第一条 Recovered——T1→T2→R 序列中 T1/T2 共用同一条 R，
            // T2（实际尚待恢复）被误算为已恢复，平均时长系统性偏低。
            var alarmStats = list
                .Where(e => e.EventType == AlarmEventType.Triggered)
                .GroupBy(e => e.AlarmName)
                .Select(g =>
                {
                    var triggers = g.OrderBy(e => e.EventTime).ToList();
                    var paired = new HashSet<AlarmEventRecord>();
                    List<double> durations = [];
                    foreach (var rec in list
                                 .Where(e => e.EventType == AlarmEventType.Recovered)
                                 .OrderBy(e => e.EventTime))
                    {
                        var prev = triggers
                            .Where(t => t.DeviceId == rec.DeviceId && t.AlarmId == rec.AlarmId
                                        && t.EventTime < rec.EventTime && !paired.Contains(t))
                            .OrderByDescending(t => t.EventTime)
                            .FirstOrDefault();
                        if (prev == null) continue;
                        paired.Add(prev);
                        durations.Add((rec.EventTime - prev.EventTime).TotalMinutes);
                    }
                    return (
                        AlarmName: g.Key,
                        TriggerCount: triggers.Count,
                        AvgDurationMin: durations.Count > 0 ? durations.Average() : 0
                    );
                })
                .OrderByDescending(x => x.TriggerCount);
            AlarmChart = ChartService.BuildAlarmChart(alarmStats);

            // 待恢复持续时长排行的基准时间：查询区间终点，若终点在未来则截到当前时刻。
            // 历史"待恢复"概念应基于查询窗口的视角，而非物理当下。
            var effectiveTo = HistoryQueryHelper.ClampToNow(to);
            AlarmInsight = BuildAlarmInsight(alarmStats, AlarmTriggerCount, list, effectiveTo);

            return (totalCount, totalPages);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "报警查询失败");
            QueryError = string.Format(Strings.F129, ex.Message);
            return (0, 0);
        }
    }

    private List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId, string? shiftName)
        => _historyService is IHistoryQueryExecutor strict
            ? strict.QueryAlarmEventsStrict(from, to, deviceId, shiftName)
            : _historyService.QueryAlarmEvents(from, to, deviceId, shiftName);

    public void Reset()
    {
        AlarmEvents.Clear();
        AlarmTriggerCount = 0; AlarmRecoverCount = 0; AlarmPendingCount = 0;
        AlarmChart = null;
        AlarmInsight = null;
        LastQueryAlarmNames.Clear();
    }

    public string? BuildCsv()
    {
        if (AlarmEvents.Count == 0) return null;

        var rows = AlarmEvents.Select(e => new AlarmCsvRow
        {
            Timestamp = e.EventTime,
            DeviceId = e.DeviceId,
            DeviceName = e.DeviceName,
            AlarmId = e.AlarmId,
            AlarmName = e.AlarmName,
            PlcAddress = e.PlcAddress,
            EventType = e.EventType,
            EventTypeText = HistoryQueryHelper.GetEventTypeText(e.EventType)
        }).ToList();

        return HistoryQueryHelper.BuildCsv(rows,
            $"# 触发：{AlarmTriggerCount} 次，恢复：{AlarmRecoverCount} 次，待恢复：{AlarmPendingCount} 条",
            $"# {AlarmInsight ?? Strings.M176}");
    }

    private static string? BuildAlarmInsight(
        IEnumerable<(string AlarmName, int TriggerCount, double AvgDurationMin)> stats,
        int totalTriggers,
        List<AlarmEventRecord> allEvents,
        DateTime effectiveTo)
    {
        var list = stats.ToList();
        if (list.Count == 0 || totalTriggers <= 0) return null;

        var top3 = list.Take(3).ToList();
        var parts = top3.Select(t =>
        {
            var ratio = (double)t.TriggerCount / totalTriggers;
            return string.Format(Strings.F040, t.AlarmName, t.TriggerCount, ratio);
        });
        var topInsight = string.Format(Strings.F242, string.Join(" / ", parts));

        var sb = new StringBuilder(topInsight);

        var correlation = BuildAlarmCorrelationInsight(allEvents);
        if (correlation != null) sb.Append('\n').Append(correlation);

        var pending = BuildPendingAlarmDurationInsight(allEvents, effectiveTo);
        if (pending != null) sb.Append('\n').Append(pending);

        return sb.ToString();
    }

    /// <summary>
    /// 待恢复持续时长排行：识别"最后一条事件为 Triggered 且其后无 Recovered"的报警，
    /// 按从触发到 effectiveTo 的持续时长降序，取 Top 3 报告。
    /// 同一 (DeviceId, AlarmId) 只取最近一次 Triggered 作为计时起点，
    /// 避免同一报警多次触发造成重复计数。
    /// </summary>
    private static string? BuildPendingAlarmDurationInsight(List<AlarmEventRecord> allEvents, DateTime effectiveTo)
    {
        var triggers = allEvents.Where(e => e.EventType == AlarmEventType.Triggered).ToList();
        var recovers = allEvents.Where(e => e.EventType == AlarmEventType.Recovered).ToList();

        List<(string AlarmName, DateTime TriggerTime, double Minutes)> pending = [];
        foreach (var g in triggers.GroupBy(e => new { e.DeviceId, e.AlarmId, e.AlarmName }))
        {
            // 取该报警在窗口内最后一次 Triggered 事件作为计时起点
            var lastTrigger = g.OrderByDescending(e => e.EventTime).First();
            // 校验其后是否出现 Recovered（窗口内）：若有则不再算待恢复
            var hasRecoverAfter = recovers.Any(r =>
                r.DeviceId == g.Key.DeviceId
                && r.AlarmId == g.Key.AlarmId
                && r.EventTime > lastTrigger.EventTime);
            if (hasRecoverAfter) continue;

            var minutes = (effectiveTo - lastTrigger.EventTime).TotalMinutes;
            if (minutes > 0)
                pending.Add((g.Key.AlarmName ?? "?", lastTrigger.EventTime, minutes));
        }

        if (pending.Count == 0) return null;

        var top = pending.OrderByDescending(x => x.Minutes).Take(3).ToList();
        var parts = top.Select(p =>
        {
            var dur = p.Minutes >= 60
                ? $"{p.Minutes / 60.0:F1}h"
                : $"{p.Minutes:F0}min";
            return string.Format(Strings.F034, p.AlarmName, dur, p.TriggerTime);
        });
        return string.Format(Strings.F044, string.Join(" / ", parts));
    }

    /// <summary>
    /// 时序关联分析：检测"连锁触发"——同一时间窗口（默认 5 分钟）内多个不同报警先后触发。
    /// 生产现场中连锁触发往往同源（如温度升高→压力下降→振动加剧），识别后有助于快速定位根因。
    /// 仅当同一组合在窗口内出现 ≥ 2 次时报告，避免偶发误报。
    /// </summary>
    private static string? BuildAlarmCorrelationInsight(List<AlarmEventRecord> allEvents,
        TimeSpan? window = null)
    {
        var correlationWindow = window ?? TimeSpan.FromMinutes(5);
        var triggers = allEvents
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .OrderBy(e => e.EventTime)
            .ToList();
        if (triggers.Count < 2) return null;

        // 滑动窗口扫描：对每个触发事件，找其后 window 内触发的"不同报警名"
        List<List<string>> chains = [];
        for (int i = 0; i < triggers.Count; i++)
        {
            var seed = triggers[i];
            List<string> chain = [seed.AlarmName ?? "?"];
            for (int j = i + 1; j < triggers.Count; j++)
            {
                if (triggers[j].EventTime - seed.EventTime > correlationWindow) break;
                var name = triggers[j].AlarmName ?? "?";
                if (!chain.Contains(name)) chain.Add(name);
            }
            if (chain.Count >= 2) chains.Add(chain);
        }

        if (chains.Count == 0) return null;

        // 按报警名组合分组，统计频次，取最高频
        var groups = chains
            .GroupBy(c => string.Join("→", c))
            .Select(g => (Pattern: g.Key, Count: g.Count()))
            .Where(x => x.Count >= 2)  // 仅在出现 ≥ 2 次时报告，避免偶发
            .OrderByDescending(x => x.Count)
            .ToList();
        if (groups.Count == 0) return null;

        var top = groups.First();
        return string.Format(Strings.F052, top.Pattern, top.Count);
    }

    private class AlarmCsvRow
    {
        [Name("事件时间")] public DateTime Timestamp { get; set; }
        [Name("设备ID")] public string? DeviceId { get; set; }
        [Name("设备名称")] public string? DeviceName { get; set; }
        [Name("报警ID")] public string? AlarmId { get; set; }
        [Name("报警名称")] public string? AlarmName { get; set; }
        [Name("PLC地址")] public string? PlcAddress { get; set; }
        [Name("事件类型")] public AlarmEventType EventType { get; set; }
        [Name("事件类型文本")] public string? EventTypeText { get; set; }
    }
}
