using Kanban.Analysis;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace Kanban.Web.Services;

/// <summary>
/// 生产复盘分析（Web 端）：计算委托 <see cref="ReviewWindowMetrics"/>（ADR-4 单源），
/// 健康分/结论文案在屏端本地化。
/// </summary>
public static class ReviewAnalysis
{
    public const double QualityTarget = 0.95;
    public const double OeeTarget = 0.85;

    public static int CalculateProductionDeltaSorted(List<ProductionLogDto> logs, DateTime from, DateTime to)
        => ReviewWindowMetrics.CalculateProductionDeltaSorted(logs, from, to);

    public static List<ReviewAlarmItemDto> AnalyzeAlarms(
        List<AlarmEventRecordDto> events, List<ProductionLogDto> productionLogs, DateTime windowTo,
        DateTime? now = null)
        => ReviewWindowMetrics.AnalyzeAlarms(events, productionLogs, windowTo, now);

    public static (string Name, double Hours) FindLongestAlarm(List<AlarmEventRecordDto> events, DateTime windowTo, DateTime? now = null)
        => ReviewWindowMetrics.FindLongestAlarm(events, windowTo, now);

    public static List<ReviewTimelineSegmentDto> BuildTimeline(
        List<StatusTransitionRecordDto> transitions,
        List<AlarmEventRecordDto> alarms,
        List<ProductionLogDto> productionLogs,
        int initialState,
        DateTime from,
        DateTime to)
        => ReviewWindowMetrics.BuildTimeline(transitions, alarms, productionLogs, initialState, from, to);

    public static List<ReviewDefectRowDto> BuildDefectConcentrations(
        List<DefectSnapshotRecordDto> hourlyBounds, DateTime from, DateTime to)
        => ReviewWindowMetrics.BuildDefectConcentrations(hourlyBounds, from, to);

    public static (string PeakHour, int PeakOk, string ValleyHour, int ValleyOk) FindPeakValley(
        List<ProductionLogDto> logs, DateTime from, DateTime to)
        => ReviewWindowMetrics.FindPeakValley(logs, from, to);

    public static List<ReviewShiftRowDto> BuildShiftSummaries(
        List<ProductionLogDto> logs, List<AlarmEventRecordDto> alarms)
        => ReviewWindowMetrics.BuildShiftSummaries(logs, alarms);

    public static (List<string> Issues, int Score) CalculateHealth(
        int targetCycle,
        int totalOutput,
        int currentAlarmCount,
        IReadOnlyList<ReviewTimelineSegmentDto> timeline,
        IReadOnlyList<ReviewDefectRowDto> concentrations,
        int previousAlarmCount,
        int previousDefectCount,
        DateTime from,
        DateTime to,
        Func<string, object[], string> L)
    {
        List<(string Text, int Deduct)> issues = [];
        var runHours = timeline.Where(s => s.StatusWord == (int)DeviceStatus.Running)
            .Sum(s => (s.End - s.Start).TotalMinutes) / 60.0;
        if (targetCycle > 0 && runHours > 0 && totalOutput / runHours < targetCycle * 0.8)
            issues.Add((L("Rv_HealthLowOutput", [totalOutput / runHours, targetCycle]), 30));

        if (currentAlarmCount >= 3 && (previousAlarmCount == 0 || currentAlarmCount > previousAlarmCount * 1.5))
            issues.Add((L("Rv_HealthAlarmSpike", [currentAlarmCount, previousAlarmCount]), 20));

        var defectCount = concentrations.Sum(item => item.Count);
        if (defectCount >= 3 && (previousDefectCount == 0 || defectCount > previousDefectCount * 1.5))
            issues.Add((L("Rv_HealthDefectSpike", [defectCount, previousDefectCount]), 25));

        var idleRunning = timeline.FirstOrDefault(s => s.HasNoOutput && (s.End - s.Start).TotalMinutes >= 30);
        if (idleRunning != null)
            issues.Add((L("Rv_HealthNoOutput", [idleRunning.Start.ToString("HH:mm:ss"), idleRunning.End.ToString("HH:mm:ss"), (idleRunning.End - idleRunning.Start).TotalMinutes]), 30));

        var score = 100;
        foreach (var issue in issues)
            score -= issue.Deduct;
        return (issues.Select(i => i.Text).ToList(), Math.Clamp(score, 0, 100));
    }

    public sealed record ReviewConclusion(string Text, string Kind, bool? Met);

    public static List<ReviewConclusion> BuildConclusions(
        int totalOk,
        int totalNg,
        int totalAlarmCount,
        double longestDowntimeHours,
        string longestDowntimeDevice,
        string longestDowntimeAlarm,
        IReadOnlyList<ReviewShiftRowDto> shifts,
        IReadOnlyList<ReviewDefectRowDto> defects,
        double quality,
        double oee,
        Func<string, object[], string> L)
    {
        List<ReviewConclusion> result = [];
        var total = totalOk + totalNg;
        if (total == 0 && totalAlarmCount == 0)
            return [new ReviewConclusion(L("Rv_NoConclusion", []), "info", null)];

        if (total > 0)
        {
            var met = quality >= QualityTarget;
            result.Add(new ReviewConclusion(
                L("Rv_QualityConclusion", [quality, met ? L("Rv_Met", []) : L("Rv_NotMet", []), QualityTarget]),
                "quality", met));
        }

        if (oee > 0)
        {
            var met = oee >= OeeTarget;
            result.Add(new ReviewConclusion(
                L("Rv_OeeConclusion", [oee, met ? L("Rv_Met", []) : L("Rv_NotMet", []), OeeTarget]),
                "oee", met));
        }

        if (longestDowntimeHours > 0)
        {
            result.Add(new ReviewConclusion(
                L("Rv_DowntimeConclusion", [longestDowntimeHours, longestDowntimeDevice, longestDowntimeAlarm]),
                "downtime", null));
        }

        var topShift = shifts.Where(s => s.OkCount + s.NgCount > 0).OrderByDescending(s => s.OkCount + s.NgCount).FirstOrDefault();
        if (topShift != null)
        {
            var okRatio = (topShift.OkCount + topShift.NgCount) > 0 ? (double)topShift.OkCount / (topShift.OkCount + topShift.NgCount) : 0;
            result.Add(new ReviewConclusion(
                L("Rv_BestShiftConclusion", [topShift.ShiftName, topShift.OkCount + topShift.NgCount, okRatio]),
                "bestshift", null));
        }

        var topDefect = defects.FirstOrDefault();
        if (topDefect != null)
        {
            result.Add(new ReviewConclusion(
                L("Rv_TopDefectConclusion", [topDefect.DefectName, topDefect.ShiftName, topDefect.Count, topDefect.Share * 100]),
                "topdefect", null));
        }

        if (totalAlarmCount > 0 && result.Count < 5)
        {
            result.Add(new ReviewConclusion(
                L("Rv_AlarmCountConclusion", [totalAlarmCount]),
                "alarmcount", null));
        }

        return result.Take(5).ToList();
    }
}
