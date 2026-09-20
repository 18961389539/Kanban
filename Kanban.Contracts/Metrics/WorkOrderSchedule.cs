using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Metrics;

/// <summary>
/// 工单进度相对计划时间的口径（WPF 工单管理与 Web 工单页共用）。
/// 偏差 = 实际达成率 − 计划时间进度；落后阈值为 −10%。
/// </summary>
public enum WorkOrderScheduleKind
{
    CompletedMet,
    CompletedShort,
    Aborted,
    MetPendingComplete,
    OverdueIncomplete,
    Behind,
    OnTrack,
}

public static class WorkOrderSchedule
{
    public const double BehindThreshold = -0.1;

    public static bool IsOpen(WorkOrderStatus status)
        => status is WorkOrderStatus.Pending or WorkOrderStatus.Running;

    public static bool IsOverdue(WorkOrderStatus status, DateTime plannedEnd, DateTime now)
        => IsOpen(status) && plannedEnd != default && plannedEnd < now;

    public static double AchievementRate(int okCount, int targetQuantity)
        => targetQuantity <= 0 ? 0 : (double)okCount / targetQuantity;

    public static double ProgressDeviation(
        WorkOrderStatus status,
        DateTime plannedStart,
        DateTime plannedEnd,
        double achievementRate,
        DateTime now)
    {
        if (status is WorkOrderStatus.Completed or WorkOrderStatus.Aborted)
            return achievementRate - 1.0;
        if (plannedStart == default || plannedEnd <= plannedStart)
            return 0;
        var total = (plannedEnd - plannedStart).TotalSeconds;
        var elapsed = (now - plannedStart).TotalSeconds;
        var plannedRate = Math.Clamp(elapsed / total, 0, 1);
        return achievementRate - plannedRate;
    }

    public static WorkOrderScheduleKind Classify(
        WorkOrderStatus status,
        DateTime plannedStart,
        DateTime plannedEnd,
        double achievementRate,
        DateTime now)
    {
        if (status == WorkOrderStatus.Completed)
            return achievementRate >= 1 ? WorkOrderScheduleKind.CompletedMet : WorkOrderScheduleKind.CompletedShort;
        if (status == WorkOrderStatus.Aborted)
            return WorkOrderScheduleKind.Aborted;
        if (achievementRate >= 1.0)
            return WorkOrderScheduleKind.MetPendingComplete;
        if (plannedEnd != default && plannedEnd < now)
            return WorkOrderScheduleKind.OverdueIncomplete;
        return ProgressDeviation(status, plannedStart, plannedEnd, achievementRate, now) < BehindThreshold
            ? WorkOrderScheduleKind.Behind
            : WorkOrderScheduleKind.OnTrack;
    }

    /// <summary>对应 Localization.csv 的 M031–M095 键。</summary>
    public static string StatusKey(WorkOrderScheduleKind kind) => kind switch
    {
        WorkOrderScheduleKind.CompletedMet => "M092",
        WorkOrderScheduleKind.CompletedShort => "M093",
        WorkOrderScheduleKind.Aborted => "M031",
        WorkOrderScheduleKind.MetPendingComplete => "M032",
        WorkOrderScheduleKind.OverdueIncomplete => "M033",
        WorkOrderScheduleKind.Behind => "M094",
        _ => "M095",
    };

    public static string CssClass(WorkOrderScheduleKind kind) => kind switch
    {
        WorkOrderScheduleKind.CompletedMet or WorkOrderScheduleKind.MetPendingComplete or WorkOrderScheduleKind.OnTrack
            => "wo-sched-ok",
        WorkOrderScheduleKind.Behind or WorkOrderScheduleKind.CompletedShort
            => "wo-sched-warn",
        _ => "wo-sched-bad",
    };
}
