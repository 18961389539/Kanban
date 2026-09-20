using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace Kanban.Analysis;

/// <summary>
/// 状态窗口时长 / 按天 / 甘特段（Collector 与 Web 回退路径单源）。
/// </summary>
public static class StatusWindowMetrics
{
    public static (double RunTime, double AlarmTime, double PausedTime, double OfflineTime) CalculateStateDurations(
        IReadOnlyList<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState,
        DateTime? now = null)
    {
        var nowValue = now ?? DateTime.Now;
        if (to > nowValue) to = nowValue;
        if (from > nowValue) return (0, 0, 0, 0);

        double run = 0, alarm = 0, paused = 0, offline = 0;
        var currentState = initialState;
        var segmentStart = from;

        foreach (var t in transitions)
        {
            if (t.EventTime < from) continue;
            var duration = (t.EventTime - segmentStart).TotalSeconds;
            if (duration > 0)
                AccumulateState(ref run, ref alarm, ref paused, ref offline, currentState, duration);
            currentState = (int)t.CurrentState;
            segmentStart = t.EventTime;
        }
        if (segmentStart < to)
        {
            var duration = (to - segmentStart).TotalSeconds;
            if (duration > 0)
                AccumulateState(ref run, ref alarm, ref paused, ref offline, currentState, duration);
        }
        return (run, alarm, paused, offline);
    }

    public static List<StatusSegmentDto> BuildSegments(
        IReadOnlyList<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState,
        DateTime? now = null)
    {
        var nowValue = now ?? DateTime.Now;
        if (to > nowValue) to = nowValue;
        if (from > nowValue) return [];

        List<StatusSegmentDto> segments = [];
        var currentState = initialState;
        var segStart = from;

        foreach (var t in transitions)
        {
            if (t.EventTime <= from) continue;
            if (t.EventTime > to) break;
            if (t.EventTime > segStart)
                segments.Add(new StatusSegmentDto { Start = segStart, End = t.EventTime, State = currentState });
            currentState = (int)t.CurrentState;
            segStart = t.EventTime;
        }
        if (segStart < to)
            segments.Add(new StatusSegmentDto { Start = segStart, End = to, State = currentState });
        return segments;
    }

    public static List<StatusDailyDurationDto> BuildDailyDurations(
        IReadOnlyList<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState,
        DateTime? now = null)
    {
        var rawSegments = BuildSegments(transitions, from, to, initialState, now);
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
            .Select(kv => new StatusDailyDurationDto
            {
                Date = kv.Key,
                RunHours = kv.Value.Run / 3600.0,
                AlarmHours = kv.Value.Alarm / 3600.0,
                PauseHours = kv.Value.Pause / 3600.0,
            })
            .ToList();
    }

    /// <summary>甘特段还原为转换事件（供分班次 OEE 切窗，不含设备名）。</summary>
    public static List<StatusTransitionRecordDto> ToTransitions(
        IReadOnlyList<StatusSegmentDto> segments, int initialState)
    {
        List<StatusTransitionRecordDto> list = [];
        var prev = initialState;
        foreach (var s in segments)
        {
            if (list.Count == 0 && s.State == initialState)
            {
                prev = s.State;
                continue;
            }
            if (s.State == prev) continue;
            list.Add(new StatusTransitionRecordDto
            {
                DeviceId = "",
                DeviceName = "",
                ShiftName = "",
                PreviousState = (DeviceStatus)prev,
                CurrentState = (DeviceStatus)s.State,
                EventTime = s.Start,
            });
            prev = s.State;
        }
        return list;
    }

    private static void AccumulateState(ref double run, ref double alarm, ref double paused, ref double offline, int state, double seconds)
    {
        switch (state)
        {
            case (int)DeviceStatus.Running: run += seconds; break;
            case (int)DeviceStatus.Alarm: alarm += seconds; break;
            case (int)DeviceStatus.Paused: paused += seconds; break;
            case (int)DeviceStatus.Offline: offline += seconds; break;
        }
    }
}
