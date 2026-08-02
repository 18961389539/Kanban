using System.Collections.Generic;
using System.Collections.ObjectModel;
using MainAPP.Models;

namespace MainAPP.Services;

/// <summary>
/// 班次上下文：拥有 <see cref="_currentShiftId"/> 与 <see cref="_lastShiftSummaries"/> 及其专用锁。职责：
/// - <see cref="DetectChange"/>：检测当前时刻所属班次是否与上一班次不同（首次初始化或切换）
/// - <see cref="CurrentName"/>/<see cref="CurrentStart"/>：当前班次名称与起始 DateTime
/// - <see cref="CacheLastShiftSummaries"/>：班次切换前缓存上班次各设备产量汇总
/// - <see cref="GetLastShiftSummary"/>：查询指定设备的上班次产量快照（HomeViewModel 对比展示用）
/// </summary>
internal sealed class ShiftContext
{
    /// <summary>
    /// 当前班次标识（ShiftIdentifier record，按 Name+StartTime+EndTime 值比较，用于检测班次切换）。
    /// null 表示尚未初始化，首次进入循环时设定。
    /// </summary>
    private ShiftIdentifier? _currentShiftId;

    /// <summary>
    /// 上班次产量汇总快照（班次切换时在 ResetShift 前缓存）。
    /// Key = deviceId，Value = (OkProduction, NgProduction, ShiftName)。
    /// HomeViewModel 读取当前设备的上班次数据用于本班次 vs 上班次对比。
    /// 使用 lock 保护并发访问。
    /// </summary>
    private readonly Dictionary<string, (int Ok, int Ng, string ShiftName)> _lastShiftSummaries = new();

    private readonly object _lock = new();

    /// <summary>当前班次标识（只读快照）。null 表示尚未初始化。</summary>
    internal ShiftIdentifier? CurrentShiftId => _currentShiftId;

    /// <summary>当前班次名称。_currentShiftId 为 null 时返回空字符串。</summary>
    internal string CurrentName => _currentShiftId?.Name ?? string.Empty;

    /// <summary>提交新的班次标识（DetectShiftChange 确认切换后调用，使后续基线/快照归属新班次）。</summary>
    internal void SetCurrentShift(ShiftIdentifier? shiftId) => _currentShiftId = shiftId;

    /// <summary>
    /// 检测班次切换：根据 DateTime.Now.TimeOfDay 匹配当前班次，
    /// 若与上一班次不同（或首次初始化）则返回新班次标识；相同或当前时刻不属于任何班次时返回 null。
    /// 注意：班次配置为空或当前时刻不属于任何班次时返回 null（保持上一次状态，不动）。
    /// 调用方收到非 null 结果后需自行决定是否触发 ResetShift，并调用 <see cref="SetCurrentShift"/> 提交。
    /// </summary>
    internal ShiftIdentifier? DetectChange(ObservableCollection<ShiftConfig> shifts)
    {
        var now = System.DateTime.Now.TimeOfDay;
        ShiftConfig? current = null;
        // 快照迭代：避免 UI 线程增删班次时 ObservableCollection 抛 InvalidOperationException
        foreach (var s in shifts.ToList())
        {
            if (s.Contains(now))
            {
                current = s;
                break;
            }
        }

        // 当前时刻不属于任何班次（时间空隙）——保持上一次状态，不动
        if (current is null) return null;

        var shiftId = new ShiftIdentifier(current.Name, current.StartTime, current.EndTime);
        if (shiftId == _currentShiftId) return null;

        // 首次初始化：仅设置 _currentShiftId，不返回切换信号（return null 表示无切换）
        if (_currentShiftId is null)
        {
            _currentShiftId = shiftId;
            return null;
        }

        // 班次切换：返回新班次标识，调用方负责 CacheLastShiftSummaries + LogShiftChangeForActiveAlarms + ResetShift
        return shiftId;
    }

    /// <summary>
    /// 计算当前班次的起始 DateTime，用于重启后 OEE 时间重建的范围下界。
    /// 基于当前时刻所属班次配置推算；跨天班次（如夜班 20:00-次日08:00）若当前时刻在凌晨段，
    /// 起始落在昨天，使重建范围正确覆盖整个夜班。
    /// </summary>
    internal System.DateTime CurrentStart(System.DateTime now, ObservableCollection<ShiftConfig> shifts)
    {
        var current = shifts.ToList().FirstOrDefault(s => s.Contains(now.TimeOfDay));
        if (current is null) return now;
        // 委托 ShiftConfig 的 NodaTime 实现，跨天班次起始正确落在昨天/今天
        return current.GetCurrentStart(now);
    }

    /// <summary>
    /// 班次切换前缓存当前（即将成为"上班次"）各设备产量汇总。
    /// 必须在 ResetShift 清零之前调用，否则 TotalOk/TotalNg 已被清零。
    /// 使用【旧班次】名称作为标识（由调用方传入 CurrentName 结果），便于 UI 展示"上班次: 白班"。
    /// 不做 Clear()：仅更新有 Runtime 的设备，避免短暂无 Runtime 的设备丢失已有缓存。
    /// </summary>
    internal void CacheLastShiftSummaries(
        IEnumerable<Device> devices,
        System.Func<Device, DeviceRuntime?> getRuntime,
        string shiftName)
    {
        lock (_lock)
        {
            foreach (var device in devices.ToList())
            {
                var rt = getRuntime(device);
                if (rt == null) continue;
                _lastShiftSummaries[device.Id] = (rt.TotalOkProduction, rt.TotalNgProduction, shiftName);
            }
        }
    }

    /// <summary>查询指定设备的上班次产量快照（线程安全拷贝）。无记录时返回 (0, 0, "")。</summary>
    internal (int Ok, int Ng, string ShiftName) GetLastShiftSummary(string deviceId)
    {
        lock (_lock)
        {
            return _lastShiftSummaries.TryGetValue(deviceId, out var s) ? s : (0, 0, "");
        }
    }

    /// <summary>
    /// 清理已删除设备的上班次产量汇总缓存，避免设备增删导致 _lastShiftSummaries 无限增长。
    /// 应在 PlcDataAcquisitionService.RemoveDeviceState 中调用。
    /// </summary>
    internal void RemoveDevice(string deviceId)
    {
        lock (_lock)
            _lastShiftSummaries.Remove(deviceId);
    }

    /// <summary>
    /// 测试用：直接注入指定设备的上班次产量快照，便于构造对比场景（无需先 CacheLastShiftSummaries）。
    /// </summary>
    internal void SetLastShiftSummaryForTest(string deviceId, int ok, int ng, string shiftName)
    {
        lock (_lock)
            _lastShiftSummaries[deviceId] = (ok, ng, shiftName);
    }
}
