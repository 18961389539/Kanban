using System.Collections.Generic;
using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 报警活跃状态（当前是否触发）快照能力。采集端触发/恢复边沿写盘（Upsert 幂等），
/// 进程重启/重连后据此恢复状态；前端活跃报警墙直接查询，不再回溯历史事件推断。
/// </summary>
public interface IActiveAlarmStateService
{
    /// <summary>幂等写入/更新某报警的活跃状态行（(DeviceId, AlarmId) 唯一键）。</summary>
    bool UpsertActive(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, bool isActive, DateTime triggeredAt,
        string? shiftName = null);

    /// <summary>查询当前活跃报警（非活跃恢复行不返回；可选按设备过滤）。</summary>
    List<ActiveAlarmStateRecord> QueryActive(string? deviceId = null);

    /// <summary>按设备删除全部状态行（设备删除时调用）。</summary>
    bool RemoveByDeviceId(string deviceId);

    /// <summary>按 (设备, 报警) 删除状态行（报警/值项删除时调用；alarmId 为 Alarm.Id 或 src:{valueId}）。</summary>
    bool RemoveByAlarm(string deviceId, string alarmId);

    /// <summary>班次切换：仅对 IsActive=true 的行把 TriggeredAt 重置为 now（跨班次重新计持续时长）。</summary>
    bool ResetActiveSince(DateTime now, string? shiftName = null);
}