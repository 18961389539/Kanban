using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

public interface IProductionReviewHealthScoreService
{
    (IReadOnlyList<string> Issues, int Score) Calculate(
        Device device,
        IReadOnlyList<ProductionLog> productionLogs,
        IReadOnlyList<AlarmEventRecord> alarms,
        IReadOnlyList<ReviewStatusSegmentData> timeline,
        IReadOnlyList<ReviewDefectConcentrationData> concentrations,
        int previousAlarmCount,
        int previousDefectCount,
        DateTime from,
        DateTime to);
}

public sealed class ProductionReviewHealthScoreService : IProductionReviewHealthScoreService
{
    public (IReadOnlyList<string> Issues, int Score) Calculate(
        Device device,
        IReadOnlyList<ProductionLog> productionLogs,
        IReadOnlyList<AlarmEventRecord> alarms,
        IReadOnlyList<ReviewStatusSegmentData> timeline,
        IReadOnlyList<ReviewDefectConcentrationData> concentrations,
        int previousAlarmCount,
        int previousDefectCount,
        DateTime from,
        DateTime to)
    {
        List<string> issues = [];
        var totalOutput = ProductionReviewCalculations.CalculateProductionDelta(productionLogs, from, to);
        var runHours = timeline.Where(segment => segment.StatusWord == (int)DeviceStatus.Running)
            .Sum(segment => (segment.End - segment.Start).TotalMinutes) / 60.0;
        if (device.TargetCycle > 0 && runHours > 0 && totalOutput / runHours < device.TargetCycle * 0.8)
            issues.Add(string.Format(Strings.F195, totalOutput / runHours, device.TargetCycle));

        var currentAlarmCount = alarms.Count(alarm => alarm.EventType == AlarmEventType.Triggered);
        if (currentAlarmCount >= 3 && (previousAlarmCount == 0 || currentAlarmCount > previousAlarmCount * 1.5))
            issues.Add(string.Format(Strings.F130, currentAlarmCount, previousAlarmCount));

        var defectCount = concentrations.Sum(item => item.Count);
        if (defectCount >= 3 && (previousDefectCount == 0 || defectCount > previousDefectCount * 1.5))
            issues.Add(string.Format(Strings.F190, defectCount, previousDefectCount));

        var idleRunning = timeline.FirstOrDefault(segment => segment.HasNoOutput
            && (segment.End - segment.Start).TotalMinutes >= 30);
        if (idleRunning != null)
            issues.Add(string.Format(Strings.F220, idleRunning.Start, idleRunning.End, (idleRunning.End - idleRunning.Start).TotalMinutes));

        var score = 100;
        foreach (var issue in issues)
        {
            score -= issue.StartsWith("运行无产量", StringComparison.Ordinal) ? 30
                : issue.StartsWith("缺陷率突增", StringComparison.Ordinal) ? 25
                : issue.StartsWith("报警突增", StringComparison.Ordinal) ? 20
                : 25;
        }
        return (issues, Math.Clamp(score, 0, 100));
    }
}
