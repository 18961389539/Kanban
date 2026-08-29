using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

public interface IProductionReviewStatusTimelineService
{
    IReadOnlyList<ReviewStatusSegmentData> Build(
        string deviceId,
        IReadOnlyList<StatusTransitionRecord> transitions,
        IReadOnlyList<AlarmEventRecord> alarms,
        IReadOnlyList<ProductionLog> productionLogs,
        int initialState,
        DateTime from,
        DateTime to);
}

public sealed class ProductionReviewStatusTimelineService : IProductionReviewStatusTimelineService
{
    /// <summary>状态段最短时长：低于该值的抖动状态并入前一段，避免时间线空洞与噪声段。</summary>
    private const double MinSegmentMinutes = 0.1;

    public IReadOnlyList<ReviewStatusSegmentData> Build(
        string deviceId,
        IReadOnlyList<StatusTransitionRecord> transitions,
        IReadOnlyList<AlarmEventRecord> alarms,
        IReadOnlyList<ProductionLog> productionLogs,
        int initialState,
        DateTime from,
        DateTime to)
    {
        var ordered = transitions
            .Where(t => t.DeviceId == deviceId && t.EventTime >= from && t.EventTime <= to)
            .OrderBy(t => t.EventTime)
            .ToList();

        // 预排序一次：每段的产量差分二分定位（2026-08-11 性能修复，原每段全量扫描 1.3 万条 → 3.4s）
        var sortedLogs = productionLogs.OrderBy(log => log.Timestamp).ToList();

        // 报警按设备过滤 + 按时间预排序，配合游标统计使每段摊还 O(1)，
        // 避免 O(段数×报警数) 全量扫描（报警多 + 7 天窗口时开销显著）。
        var orderedAlarms = alarms
            .Where(a => a.EventType == AlarmEventType.Triggered && a.DeviceId == deviceId)
            .OrderBy(a => a.EventTime)
            .ToList();

        var segments = new List<ReviewStatusSegmentData>();
        var cursor = from;
        var state = initialState;
        int alarmCursor = 0;
        foreach (var transition in ordered)
        {
            AppendOrMerge(segments, orderedAlarms, sortedLogs, state, cursor, transition.EventTime, ref alarmCursor);
            cursor = transition.EventTime;
            state = transition.CurrentState;
        }
        if (to > cursor)
            AppendOrMerge(segments, orderedAlarms, sortedLogs, state, cursor, to, ref alarmCursor);
        return segments;
    }

    /// <summary>
    /// 追加 [start, end) 状态段；时长不足最短阈值的抖动区间并入前一段并重算其产量/报警，
    /// 保证时间线连续无空洞（旧实现直接丢弃短段会在热力图留下"幽灵"未知格）。
    /// </summary>
    private static void AppendOrMerge(
        List<ReviewStatusSegmentData> segments,
        IReadOnlyList<AlarmEventRecord> orderedAlarms,
        IReadOnlyList<ProductionLog> sortedLogs,
        int state,
        DateTime start,
        DateTime end,
        ref int alarmCursor)
    {
        if ((end - start).TotalMinutes < MinSegmentMinutes)
        {
            if (segments.Count > 0)
            {
                var prev = segments[^1];
                segments[^1] = CreateSegment(prev.StatusWord, prev.Start, end, sortedLogs, orderedAlarms, ref alarmCursor);
            }
            return;
        }
        segments.Add(CreateSegment(state, start, end, sortedLogs, orderedAlarms, ref alarmCursor));
    }

    private static ReviewStatusSegmentData CreateSegment(
        int state,
        DateTime start,
        DateTime end,
        IReadOnlyList<ProductionLog> sortedLogs,
        IReadOnlyList<AlarmEventRecord> orderedAlarms,
        ref int alarmCursor)
    {
        var output = ProductionReviewCalculations.CalculateProductionDeltaSorted(sortedLogs, start, end);
        var alarmCount = 0;
        // 段在时间轴上连续且有序，游标单调推进即可覆盖 [start, end) 内的全部 Triggered 报警
        while (alarmCursor < orderedAlarms.Count && orderedAlarms[alarmCursor].EventTime < end)
        {
            if (orderedAlarms[alarmCursor].EventTime >= start)
                alarmCount++;
            alarmCursor++;
        }
        var statusText = state switch
        {
            (int)DeviceStatus.Offline => Strings.Status_Initial,
            (int)DeviceStatus.Running => Strings.Status_Running,
            (int)DeviceStatus.Paused => Strings.Status_Paused,
            (int)DeviceStatus.Alarm => Strings.Status_Alarm,
            _ => Strings.Status_Unknown,
        };
        return new ReviewStatusSegmentData(
            start,
            end,
            statusText,
            state,
            output,
            alarmCount,
            state == (int)DeviceStatus.Running && output == 0);
    }
}
