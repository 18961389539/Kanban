using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
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
    /// <summary>健康问题类型：扣分按结构化枚举判定（P2-15 修复——原按中文字符串前缀 StartsWith，文案一改评分即变）。</summary>
    private enum HealthIssueKind
    {
        /// <summary>运行无产量 / 节拍异常（低产）：扣 30 分。</summary>
        NoOutput,
        /// <summary>缺陷率突增：扣 25 分。</summary>
        DefectSpike,
        /// <summary>报警突增：扣 20 分。</summary>
        AlarmSpike,
    }

    private readonly record struct HealthIssue(HealthIssueKind Kind, string Text);

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
        List<HealthIssue> issues = [];
        var totalOutput = ProductionReviewCalculations.CalculateProductionDelta(productionLogs, from, to);
        var runHours = timeline.Where(segment => segment.StatusWord == (int)DeviceStatus.Running)
            .Sum(segment => (segment.End - segment.Start).TotalMinutes) / 60.0;
        if (device.TargetCycle > 0 && runHours > 0 && totalOutput / runHours < device.TargetCycle * 0.8)
            issues.Add(new HealthIssue(HealthIssueKind.NoOutput,
                string.Format(Strings.F195, totalOutput / runHours, device.TargetCycle)));

        var currentAlarmCount = alarms.Count(alarm => alarm.EventType == AlarmEventType.Triggered);
        if (currentAlarmCount >= 3 && (previousAlarmCount == 0 || currentAlarmCount > previousAlarmCount * 1.5))
            issues.Add(new HealthIssue(HealthIssueKind.AlarmSpike,
                string.Format(Strings.F130, currentAlarmCount, previousAlarmCount)));

        var defectCount = concentrations.Sum(item => item.Count);
        if (defectCount >= 3 && (previousDefectCount == 0 || defectCount > previousDefectCount * 1.5))
            issues.Add(new HealthIssue(HealthIssueKind.DefectSpike,
                string.Format(Strings.F190, defectCount, previousDefectCount)));

        var idleRunning = timeline.FirstOrDefault(segment => segment.HasNoOutput
            && (segment.End - segment.Start).TotalMinutes >= 30);
        if (idleRunning != null)
            issues.Add(new HealthIssue(HealthIssueKind.NoOutput,
                string.Format(Strings.F220, idleRunning.Start, idleRunning.End, (idleRunning.End - idleRunning.Start).TotalMinutes)));

        var score = 100;
        foreach (var issue in issues)
        {
            score -= issue.Kind switch
            {
                HealthIssueKind.NoOutput => 30,
                HealthIssueKind.DefectSpike => 25,
                HealthIssueKind.AlarmSpike => 20,
                _ => 25,
            };
        }
        return (issues.Select(issue => issue.Text).ToList(), Math.Clamp(score, 0, 100));
    }
}
