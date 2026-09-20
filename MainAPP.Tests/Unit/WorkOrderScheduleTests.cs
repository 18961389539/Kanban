using System;
using Kanban.Contracts.Enums;
using Kanban.Contracts.Metrics;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class WorkOrderScheduleTests
{
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0);
    private static readonly DateTime End = new(2026, 9, 20, 16, 0, 0);

    [Fact]
    public void ProgressDeviation_MidShiftHalfDone_IsZero()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0);
        var deviation = WorkOrderSchedule.ProgressDeviation(
            WorkOrderStatus.Running, Start, End, achievementRate: 0.5, now);
        Assert.Equal(0, deviation, 3);
    }

    [Fact]
    public void Classify_BehindWhenAchievementLagsPlanByMoreThanTenPercent()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0);
        var kind = WorkOrderSchedule.Classify(
            WorkOrderStatus.Running, Start, End, achievementRate: 0.3, now);
        Assert.Equal(WorkOrderScheduleKind.Behind, kind);
        Assert.Equal("M094", WorkOrderSchedule.StatusKey(kind));
    }

    [Fact]
    public void Classify_OverdueWhenPastPlannedEndAndNotMet()
    {
        var now = new DateTime(2026, 9, 20, 17, 0, 0);
        var kind = WorkOrderSchedule.Classify(
            WorkOrderStatus.Running, Start, End, achievementRate: 0.8, now);
        Assert.Equal(WorkOrderScheduleKind.OverdueIncomplete, kind);
        Assert.True(WorkOrderSchedule.IsOverdue(WorkOrderStatus.Running, End, now));
    }

    [Fact]
    public void Classify_CompletedMetWhenAchievementReachesTarget()
    {
        var kind = WorkOrderSchedule.Classify(
            WorkOrderStatus.Completed, Start, End, achievementRate: 1.0, End);
        Assert.Equal(WorkOrderScheduleKind.CompletedMet, kind);
        Assert.Equal("M092", WorkOrderSchedule.StatusKey(kind));
    }
}
