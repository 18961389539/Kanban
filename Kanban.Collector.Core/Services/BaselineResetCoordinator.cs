using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

/// <summary>
/// 产量基线清零协调器：拥有 <see cref="_pendingBaselineClearAt"/> 与 <see cref="_pendingPlcResetOnReconnect"/>
/// 两个并发字典及其专用锁。职责：
/// - <see cref="IsInClearWindow"/>：查询设备是否处于 PLC 清零后的基线清空窗口期
/// - <see cref="ScheduleClear"/>：PLC 清零信号发出后安排 1 秒延迟清空窗口
/// - <see cref="ExpireClears"/>：清理到期窗口，使下一轮读取以当前 PLC 值重建基线
/// - <see cref="AddPendingReconnect"/>/<see cref="DrainPendingReconnect"/>：PLC 未连接时推迟清零，重连后批量触发
/// - <see cref="RemoveDevice"/>/<see cref="ResetAll"/>：设备删除/班次切换时清理对应状态
/// </summary>
internal sealed class BaselineResetCoordinator
{
    /// <summary>
    /// 各设备待清空产量基线的时刻。
    /// Key = deviceId，Value = 清零窗口到期时间。
    /// 存在表示已触发该设备的 PLC 清零，正在等待 PLC 程序执行清零动作（约1秒），
    /// 到期后该设备下一轮读取会重建基线，避免 PLC 还没清零时旧累计值被当作新基线。
    /// </summary>
    private readonly Dictionary<string, System.DateTime> _pendingBaselineClearAt = new();

    /// <summary>
    /// 标记 PLC 重连后需要触发产量清零的设备集合。
    /// 班次切换时若 PLC 未连接，TriggerPlcProductionReset 无法执行，
    /// 推迟到 PLC 重连后再触发清零 + 延迟1秒清空基线。
    /// </summary>
    private readonly HashSet<string> _pendingPlcResetOnReconnect = new();

    private readonly object _lock = new();

    /// <summary>设备是否处于 PLC 清零后的基线清空窗口期（窗口期内产量 delta 保持 0）。</summary>
    internal bool IsInClearWindow(string deviceId)
    {
        lock (_lock)
            return _pendingBaselineClearAt.ContainsKey(deviceId);
    }

    /// <summary>
    /// 安排设备进入基线清空窗口：PLC 清零信号发出后延迟 <paramref name="delaySeconds"/> 秒到期。
    /// 到期前产量 delta 保持 0（ResetShift 已清零），到期后下一轮读取以当前 PLC 值重建基线。
    /// </summary>
    internal void ScheduleClear(string deviceId, double delaySeconds = 1.0)
    {
        lock (_lock)
            _pendingBaselineClearAt[deviceId] = System.DateTime.Now.AddSeconds(delaySeconds);
    }

    /// <summary>
    /// 清理到期的基线清空窗口。返回到期被清理的设备数量，便于日志记录。
    /// 软件基线已在 ResetShift 时经 ProductionBaselineStore.ClearAll 清空，
    /// 此处仅清理到期的设备窗口标记，使下一轮读取以当前 PLC 值重建基线。
    /// </summary>
    internal int ExpireClears(System.DateTime now, ILogger logger)
    {
        lock (_lock)
        {
            if (_pendingBaselineClearAt.Count == 0) return 0;
            var expired = _pendingBaselineClearAt
                .Where(kv => now >= kv.Value)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var deviceId in expired)
                _pendingBaselineClearAt.Remove(deviceId);
            if (expired.Count > 0)
                logger.LogInformation("已结束 {Count} 台设备的产量基线清空窗口（PLC 清零信号发出后延迟1秒）", expired.Count);
            return expired.Count;
        }
    }

    /// <summary>PLC 未连接时标记设备推迟清零到重连后。</summary>
    internal void AddPendingReconnect(string deviceId)
    {
        lock (_lock)
            _pendingPlcResetOnReconnect.Add(deviceId);
    }

    /// <summary>是否存在待重连后触发的清零设备（用于日志告警）。</summary>
    internal bool HasPendingReconnect
    {
        get
        {
            lock (_lock)
                return _pendingPlcResetOnReconnect.Count > 0;
        }
    }

    /// <summary>待重连后触发的清零设备数量（用于日志告警）。</summary>
    internal int PendingReconnectCount
    {
        get
        {
            lock (_lock)
                return _pendingPlcResetOnReconnect.Count;
        }
    }

    /// <summary>
    /// 取出并清空待重连后触发的清零设备列表（原子操作）。
    /// 仅在"上一轮未连接 + 当前已连接"的边沿调用，避免每轮反复尝试（浪费 IO）。
    /// </summary>
    internal List<string> DrainPendingReconnect()
    {
        lock (_lock)
        {
            if (_pendingPlcResetOnReconnect.Count == 0) return [];
            var list = _pendingPlcResetOnReconnect.ToList();
            _pendingPlcResetOnReconnect.Clear();
            return list;
        }
    }

    /// <summary>清理已删除设备的清零窗口与重连待清零标记。</summary>
    internal void RemoveDevice(string deviceId)
    {
        lock (_lock)
        {
            _pendingBaselineClearAt.Remove(deviceId);
            _pendingPlcResetOnReconnect.Remove(deviceId);
        }
    }

    /// <summary>班次切换时清空全部清零窗口与重连待清零标记。</summary>
    internal void ResetAll()
    {
        lock (_lock)
        {
            _pendingBaselineClearAt.Clear();
            _pendingPlcResetOnReconnect.Clear();
        }
    }

    // ──────────── 测试访问助手（仅 internal，由 PlcDataAcquisitionService.TestAccess 转发） ────────────

    /// <summary>
    /// 测试用：读取 _pendingBaselineClearAt 的快照副本（key=deviceId, value=到期时间），
    /// 用于长期运行累积泄漏测试断言"删除设备后窗口期标记被清理"。
    /// </summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, System.DateTime> GetPendingBaselineClearAtSnapshot()
    {
        lock (_lock)
            return _pendingBaselineClearAt.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// 测试用：读取 _pendingPlcResetOnReconnect 的快照副本，
    /// 用于长期运行累积泄漏测试断言"重连待清零集合不无限增长"。
    /// </summary>
    internal System.Collections.Generic.IReadOnlyCollection<string> GetPendingPlcResetOnReconnectSnapshot()
    {
        lock (_lock)
            return _pendingPlcResetOnReconnect.ToList();
    }
}
