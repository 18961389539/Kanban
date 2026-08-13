using Kanban.Contracts.Dtos;

namespace Kanban.Web.Services;

/// <summary>
/// 状态历史分析（Web 端）：从 WPF StatusQueryViewModel + OeeCalculator.CalculateStateDurations 移植。
/// 输入为 <see cref="StatusTransitionRecordDto"/>（跨进程契约），口径与 WPF 逐条对齐：
/// 状态段切分（含窗口前最近状态的初始态）、各状态累计时长、按天汇总、甘特段、洞察。
/// </summary>
public static class StatusAnalysis
{
    /// <summary>按天汇总各状态时长（小时）。与 WPF BuildDailyDurations 一致：跨天段按午夜切分累计。</summary>
    public static List<(DateTime Date, double RunHours, double AlarmHours, double PauseHours)> BuildDailyDurations(
        List<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState)
    {
        var rawSegments = BuildSegments(transitions, from, to, initialState);

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
                    if (seg.State == (int)Kanban.Contracts.Enums.DeviceStatus.Running) acc.Run += secs;
                    else if (seg.State == (int)Kanban.Contracts.Enums.DeviceStatus.Alarm) acc.Alarm += secs;
                    else if (seg.State == (int)Kanban.Contracts.Enums.DeviceStatus.Paused) acc.Pause += secs;
                    byDay[day] = acc;
                }
                cursor = nextMidnight;
            }
        }

        return byDay
            .OrderBy(kv => kv.Key)
            .Select(kv => (Date: kv.Key, RunHours: kv.Value.Run / 3600.0, AlarmHours: kv.Value.Alarm / 3600.0, PauseHours: kv.Value.Pause / 3600.0))
            .ToList();
    }

    /// <summary>状态段（Start, End, State）升序列表；to 防御性截断到当前时刻。与 WPF BuildGanttSegments + ClampToNow 一致。</summary>
    public static List<(DateTime Start, DateTime End, int State)> BuildSegments(
        List<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState)
    {
        var now = DateTime.Now;
        if (to > now) to = now;
        if (from > now) return [];

        List<(DateTime Start, DateTime End, int State)> segments = [];
        var currentState = initialState;
        var segStart = from;

        foreach (var t in transitions)
        {
            if (t.EventTime <= from) continue;
            if (t.EventTime > to) break;
            if (t.EventTime > segStart)
                segments.Add((segStart, t.EventTime, currentState));
            currentState = (int)t.CurrentState;
            segStart = t.EventTime;
        }
        if (segStart < to)
            segments.Add((segStart, to, currentState));
        return segments;
    }

    /// <summary>各状态累计时长（秒）。与 OeeCalculator.CalculateStateDurations 逐条一致。</summary>
    public static (double RunTime, double AlarmTime, double PausedTime) CalculateStateDurations(
        List<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState)
    {
        var now = DateTime.Now;
        if (to > now) to = now;
        if (from > now) return (0, 0, 0);

        double run = 0, alarm = 0, paused = 0;
        var currentState = initialState;
        var segmentStart = from;

        foreach (var t in transitions)
        {
            if (t.EventTime < from) continue;
            var duration = (t.EventTime - segmentStart).TotalSeconds;
            if (duration > 0)
                AccumulateState(ref run, ref alarm, ref paused, currentState, duration);
            currentState = (int)t.CurrentState;
            segmentStart = t.EventTime;
        }
        if (segmentStart < to)
        {
            var duration = (to - segmentStart).TotalSeconds;
            if (duration > 0)
                AccumulateState(ref run, ref alarm, ref paused, currentState, duration);
        }
        return (run, alarm, paused);
    }

    private static void AccumulateState(ref double run, ref double alarm, ref double paused, int state, double seconds)
    {
        switch (state)
        {
            case (int)Kanban.Contracts.Enums.DeviceStatus.Running: run += seconds; break;
            case (int)Kanban.Contracts.Enums.DeviceStatus.Alarm: alarm += seconds; break;
            case (int)Kanban.Contracts.Enums.DeviceStatus.Paused: paused += seconds; break;
        }
    }

    /// <summary>
    /// 状态洞察（按需本地化）：最长连续运行/报警/待机段 + 报警/待机占比异常检测。
    /// 阈值与 WPF BuildStatusInsight 对齐（单次报警 > 30 分钟；占比 > 20%）。
    /// </summary>
    public static string? BuildInsight(
        List<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState,
        Func<string, object[], string> localize)
    {
        const double LongAlarmThresholdMin = 30;
        const double HighPauseRatioThreshold = 0.20;

        var segments = BuildSegments(transitions, from, to, initialState);
        if (segments.Count == 0) return null;

        List<string> parts = [];

        var runSegments = segments.Where(s => s.State == (int)Kanban.Contracts.Enums.DeviceStatus.Running).ToList();
        if (runSegments.Count > 0)
        {
            var longestRun = runSegments.MaxBy(s => s.End - s.Start);
            var minutes = (longestRun.End - longestRun.Start).TotalMinutes;
            if (minutes > 0)
                parts.Add(localize("Hq_InsLongestRun", [Math.Round(minutes), Fmt(longestRun.Start), Fmt(longestRun.End)]));
        }

        var alarmSegments = segments.Where(s => s.State == (int)Kanban.Contracts.Enums.DeviceStatus.Alarm).ToList();
        if (alarmSegments.Count > 0)
        {
            var longestAlarm = alarmSegments.MaxBy(s => s.End - s.Start);
            var minutes = (longestAlarm.End - longestAlarm.Start).TotalMinutes;
            if (minutes > 0)
            {
                var key = minutes > LongAlarmThresholdMin ? "Hq_InsLongestAlarmLong" : "Hq_InsLongestAlarm";
                parts.Add(localize(key, [Math.Round(minutes), Fmt(longestAlarm.Start), Fmt(longestAlarm.End)]));
            }
        }

        var pauseSegments = segments.Where(s => s.State == (int)Kanban.Contracts.Enums.DeviceStatus.Paused).ToList();
        if (pauseSegments.Count > 0)
        {
            var longestPause = pauseSegments.MaxBy(s => s.End - s.Start);
            var minutes = (longestPause.End - longestPause.Start).TotalMinutes;
            if (minutes > 0)
                parts.Add(localize("Hq_InsLongestPause", [Math.Round(minutes), Fmt(longestPause.Start), Fmt(longestPause.End)]));
        }

        var totalSpan = (to - from).TotalSeconds;
        if (totalSpan > 0)
        {
            var alarmRatio = alarmSegments.Sum(s => (s.End - s.Start).TotalSeconds) / totalSpan;
            var pauseRatio = pauseSegments.Sum(s => (s.End - s.Start).TotalSeconds) / totalSpan;
            if (pauseRatio > HighPauseRatioThreshold)
                parts.Add(localize("Hq_InsPauseRatio", [pauseRatio, HighPauseRatioThreshold]));
            else if (alarmRatio > HighPauseRatioThreshold)
                parts.Add(localize("Hq_InsAlarmRatio", [alarmRatio, HighPauseRatioThreshold]));
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    private static string Fmt(DateTime t) => t.ToString("MM-dd HH:mm");
}
