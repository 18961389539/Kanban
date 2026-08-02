using Kanban.Core.Entities;

namespace Kanban.Core.Services;

/// <summary>
/// <see cref="PlcDataAcquisitionService"/> 的测试访问助手（partial）。
/// 仅 internal，测试项目通过 InternalsVisibleTo 可见；生产代码不调用。
/// 拆分到独立文件避免污染主类的可读性，新增测试助手时优先加在此处。
/// 状态字典已迁移到 AlarmStateTracker/DeviceStatusTracker/ShiftContext 等协作组件，
/// 此处仅作转发，维持原测试 API 不变（测试代码零改动）。
/// </summary>
public partial class PlcDataAcquisitionService
{
    /// <summary>
    /// 测试用：直接读取/设置当前班次标识，便于单测在不依赖 DateTime.Now 的情况下构造班次切换场景。
    /// 生产代码仍通过 DetectShiftChange 自动维护。
    /// </summary>
    internal ShiftIdentifier? CurrentShiftIdForTest
    {
        get => _shiftContext.CurrentShiftId;
        set => _shiftContext.SetCurrentShift(value);
    }

    /// <summary>
    /// 测试用：读取 _prevAlarmStates 的快照副本，便于断言报警内存状态。
    /// </summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, bool> PrevAlarmStatesForTest
        => _alarmTracker.GetPrevAlarmStatesSnapshot();

    /// <summary>
    /// 测试用：读取 _prevStatusWords 的快照副本，便于断言状态字内存状态。
    /// </summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, int> PrevStatusWordsForTest
        => _statusTracker.GetPrevStatusWordsSnapshot();

    /// <summary>
    /// 测试用：读取 _shiftChangeFailedAlarms 的快照副本，便于断言班次切换失败集合状态。
    /// 用于验证 LogShiftChangeForActiveAlarms 在写入失败后是否正确标记报警。
    /// </summary>
    internal System.Collections.Generic.IReadOnlyCollection<string> ShiftChangeFailedAlarmsForTest
        => _alarmTracker.GetShiftChangeFailedAlarmsSnapshot();

    /// <summary>
    /// 测试用：直接设置 _prevAlarmStates 中的某项，便于构造报警已触发等初始场景。
    /// </summary>
    internal void SetPrevAlarmStateForTest(string alarmId, bool state)
        => _alarmTracker.SetPrevAlarmStateForTest(alarmId, state);

    /// <summary>
    /// 测试用：仅从 _prevAlarmStates 移除指定报警（不清 _shiftChangeFailedAlarms），
    /// 精确模拟 ResetShift 对单条报警状态的影响（ResetShift 内 _prevAlarmStates.Clear()
    /// 不会清空 _shiftChangeFailedAlarms），便于班次切换失败恢复场景的单测。
    /// </summary>
    internal void ClearPrevAlarmStateForTest(string alarmId)
        => _alarmTracker.ClearPrevAlarmStateForTest(alarmId);

    /// <summary>
    /// 测试用：直接注入指定设备的上班次产量快照，便于构造本班次 vs 上班次对比场景。
    /// </summary>
    internal void SetLastShiftSummaryForTest(string deviceId, int ok, int ng, string shiftName)
        => _shiftContext.SetLastShiftSummaryForTest(deviceId, ok, ng, shiftName);

    /// <summary>
    /// 测试用：读取 _pendingBaselineClearAt 的快照副本（key=deviceId, value=到期时间）。
    /// 长期累积泄漏测试用于断言"删除设备后窗口期标记被清理，字典不无限增长"。
    /// </summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, System.DateTime> PendingBaselineClearAtForTest
        => _baselineCoordinator.GetPendingBaselineClearAtSnapshot();

    /// <summary>
    /// 测试用：读取 _pendingPlcResetOnReconnect 的快照副本。
    /// 长期累积泄漏测试用于断言"重连待清零集合不无限增长"。
    /// </summary>
    internal System.Collections.Generic.IReadOnlyCollection<string> PendingPlcResetOnReconnectForTest
        => _baselineCoordinator.GetPendingPlcResetOnReconnectSnapshot();

    /// <summary>
    /// 测试用：直接调用 TryScan(Func<bool>) 包装器，验证异常分类与 MarkDisconnected(ScanException) 触发逻辑。
    /// 生产代码通过 PollingLoopAsync 间接调用，测试需直接调用以构造异常注入场景。
    /// </summary>
    internal bool TryScanForTest(Func<bool> scanAction, string scanName) => TryScan(scanAction, scanName);

    /// <summary>
    /// 测试用：直接调用 TryScan(Action) 包装器（void 重载），验证无返回值扫描的异常处理路径。
    /// </summary>
    internal void TryScanForTest(Action scanAction, string scanName) => TryScan(scanAction, scanName);
}
