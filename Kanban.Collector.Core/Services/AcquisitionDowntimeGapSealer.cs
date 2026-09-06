using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 采集空窗补离线：进程被杀、断电、Stop 超时未写离线边沿时，
/// 重启把「最后一次心跳 → 本次启动」记成 Offline，避免 OEE 把空窗续成运行。
/// </summary>
internal static class AcquisitionDowntimeGapSealer
{
    public static readonly TimeSpan DefaultMinGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 若最后心跳仍是运行/报警/待机，且距 <paramref name="now"/> 超过最小空窗，则应在心跳时刻写入离线边沿。
    /// </summary>
    public static bool TryResolve(
        StatusTransitionRecord? lastStatus,
        ProductionLog? lastProduction,
        DateTime now,
        TimeSpan minGap,
        out int previousState,
        out DateTime sealAt,
        out string shiftName)
    {
        previousState = 0;
        sealAt = default;
        shiftName = string.Empty;

        DateTime? lastSeen = lastStatus?.EventTime;
        var lastState = lastStatus?.CurrentState;
        var shift = lastStatus?.ShiftName;

        if (lastProduction != null && (lastSeen == null || lastProduction.Timestamp > lastSeen.Value))
        {
            lastSeen = lastProduction.Timestamp;
            lastState = lastProduction.StatusWord;
            if (string.IsNullOrEmpty(shift))
                shift = lastProduction.ShiftName;
        }

        if (lastSeen == null || lastState == null)
            return false;

        var normalized = NormalizeLiveState(lastState.Value);
        if (normalized == (int)DeviceStatus.Offline)
            return false;

        if (lastSeen.Value >= now)
            return false;

        if (now - lastSeen.Value < minGap)
            return false;

        previousState = normalized;
        sealAt = lastSeen.Value.AddTicks(1);
        if (sealAt > now)
            sealAt = now;
        shiftName = shift ?? string.Empty;
        return true;
    }

    /// <summary>仅 1/2/3 视为采集仍在跟的活状态；其余（含离线与非法值）视为已离线，无需补边沿。</summary>
    private static int NormalizeLiveState(int status) => status switch
    {
        (int)DeviceStatus.Running => status,
        (int)DeviceStatus.Alarm => status,
        (int)DeviceStatus.Paused => status,
        _ => (int)DeviceStatus.Offline,
    };
}
