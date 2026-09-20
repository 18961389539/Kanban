using Kanban.Analysis;
using Kanban.Contracts.Dtos;

namespace Kanban.Web.Services;

/// <summary>
/// 状态历史分析（Web 端）：时长/按天/甘特委托 <see cref="StatusWindowMetrics"/>，洞察在屏端本地化。
/// </summary>
public static class StatusAnalysis
{
    public static List<(DateTime Date, double RunHours, double AlarmHours, double PauseHours)> BuildDailyDurations(
        List<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState,
        DateTime? now = null)
        => StatusWindowMetrics.BuildDailyDurations(transitions, from, to, initialState, now)
            .Select(d => (d.Date, d.RunHours, d.AlarmHours, d.PauseHours))
            .ToList();

    public static List<(DateTime Start, DateTime End, int State)> BuildSegments(
        List<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState,
        DateTime? now = null)
        => StatusWindowMetrics.BuildSegments(transitions, from, to, initialState, now)
            .Select(s => (s.Start, s.End, s.State))
            .ToList();

    public static (double RunTime, double AlarmTime, double PausedTime, double OfflineTime) CalculateStateDurations(
        List<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState,
        DateTime? now = null)
        => StatusWindowMetrics.CalculateStateDurations(transitions, from, to, initialState, now);

    public static string? BuildInsight(
        List<StatusTransitionRecordDto> transitions, DateTime from, DateTime to, int initialState,
        Func<string, object[], string> localize, DateTime? now = null)
        => BuildInsight(StatusWindowMetrics.BuildSegments(transitions, from, to, initialState, now), from, to, localize);

    public static string? BuildInsight(
        IReadOnlyList<StatusSegmentDto> segments, DateTime from, DateTime to,
        Func<string, object[], string> localize)
    {
        const double LongAlarmThresholdMin = 30;
        const double HighPauseRatioThreshold = 0.20;
        if (segments.Count == 0) return null;

        List<string> parts = [];

        var runSegments = segments.Where(s => s.State == (int)Kanban.Contracts.Enums.DeviceStatus.Running).ToList();
        if (runSegments.Count > 0)
        {
            var longestRun = runSegments.MaxBy(s => s.End - s.Start)!;
            var minutes = (longestRun.End - longestRun.Start).TotalMinutes;
            if (minutes > 0)
                parts.Add(localize("Hq_InsLongestRun", [Math.Round(minutes), Fmt(longestRun.Start), Fmt(longestRun.End)]));
        }

        var alarmSegments = segments.Where(s => s.State == (int)Kanban.Contracts.Enums.DeviceStatus.Alarm).ToList();
        if (alarmSegments.Count > 0)
        {
            var longestAlarm = alarmSegments.MaxBy(s => s.End - s.Start)!;
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
            var longestPause = pauseSegments.MaxBy(s => s.End - s.Start)!;
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
