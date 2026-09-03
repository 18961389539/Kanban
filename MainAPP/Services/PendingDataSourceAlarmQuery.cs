using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;

namespace MainAPP.Services;

/// <summary>
/// 数据源报警（AlarmId 以 src: 前缀）活跃查询：直查报警活跃状态表（ActiveAlarmStates），
/// 取代已废弃的"回溯历史事件推断活跃状态"逻辑（旧 7/30 天回溯窗口 + ExtractPending）。
/// 状态行由采集端 DataSourceAlarmTracker / PlcScanPipeline 边沿 Upsert 维护，IsActive 即当前事实。
/// </summary>
public static class PendingDataSourceAlarmQuery
{
    /// <summary>直查状态表并按 src: 前缀过滤数据源报警；查询失败返回 null，调用方回退缓存。</summary>
    public static List<ActiveAlarmStateRecord>? TryQueryActiveSourceStates(
        IAlarmHistoryService historyService,
        string? deviceId)
    {
        try
        {
            var states = historyService.QueryActiveAlarmStates(deviceId);
            return states
                .Where(s => s.AlarmId.StartsWith("src:", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 存在性校验：值项须仍存在于当前设备配置。设备/数据源/值项被删除后采集端应同步清除状态行，
    /// 此处作为前端兜底遮蔽极端情况下的残留孤儿行，避免被翻出来当活跃报警。
    /// 不做实时值覆盖——状态表 IsActive 由采集端以 PLC 采样事实为准维护，是权威真源。
    /// </summary>
    public static List<ActiveAlarmStateRecord> FilterByCurrentState(
        IReadOnlyList<ActiveAlarmStateRecord> states,
        IReadOnlyList<Device> devices)
    {
        if (states.Count == 0) return new List<ActiveAlarmStateRecord>();

        // 索引 deviceId(忽略大小写) → valueId → DataSourceValue，O(1) 反查避免每记录全量遍历
        var valueLookup = new Dictionary<string, Dictionary<string, DataSourceValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var values = new Dictionary<string, DataSourceValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in device.Sources)
            {
                foreach (var value in source.Values)
                {
                    if (!values.ContainsKey(value.Id))
                        values[value.Id] = value;
                }
            }
            valueLookup[device.Id] = values;
        }

        var result = new List<ActiveAlarmStateRecord>(states.Count);
        foreach (var record in states)
        {
            if (!record.AlarmId.StartsWith("src:", StringComparison.OrdinalIgnoreCase))
                continue;
            var valueId = record.AlarmId["src:".Length..];
            if (!valueLookup.TryGetValue(record.DeviceId, out var values)
                || !values.TryGetValue(valueId, out _))
                continue; // 值项已不在当前 PLC 配置中 → 视为不存在

            result.Add(record);
        }
        return result;
    }
}