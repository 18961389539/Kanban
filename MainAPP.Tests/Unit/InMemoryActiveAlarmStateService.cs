using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 内存版报警活跃状态服务（测试替身）：直接操作字典，
/// 供"状态表重建/边沿 Upsert"语义的单测注入（与生产 ActiveAlarmStateStore 行为对齐）。
/// </summary>
internal sealed class InMemoryActiveAlarmStateService : IActiveAlarmStateService
{
    private readonly Dictionary<string, ActiveAlarmStateRecord> _rows = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string deviceId, string alarmId) => $"{deviceId}|{alarmId}";

    public bool UpsertActive(string deviceId, string deviceName, string alarmId, string alarmName,
        string plcAddress, bool isActive, DateTime triggeredAt, string? shiftName = null)
    {
        var key = Key(deviceId, alarmId);
        if (_rows.TryGetValue(key, out var row))
        {
            row.DeviceName = deviceName;
            row.AlarmName = alarmName;
            row.PlcAddress = plcAddress;
            row.IsActive = isActive;
            row.TriggeredAt = triggeredAt;
            row.ShiftName = shiftName ?? row.ShiftName;
            row.UpdatedAt = DateTime.Now;
        }
        else
        {
            _rows[key] = new ActiveAlarmStateRecord
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                AlarmId = alarmId,
                AlarmName = alarmName,
                PlcAddress = plcAddress,
                IsActive = isActive,
                TriggeredAt = triggeredAt,
                ShiftName = shiftName ?? string.Empty,
                UpdatedAt = DateTime.Now,
            };
        }
        return true;
    }

    public List<ActiveAlarmStateRecord> QueryActive(string? deviceId = null)
        => _rows.Values
            .Where(r => r.IsActive)
            .Where(r => deviceId == null || string.Equals(r.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public bool RemoveByDeviceId(string deviceId)
    {
        foreach (var key in _rows.Keys.Where(k => k.StartsWith(deviceId + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            _rows.Remove(key);
        return true;
    }

    public bool RemoveByAlarm(string deviceId, string alarmId)
        => _rows.Remove(Key(deviceId, alarmId));

    public bool ResetActiveSince(DateTime now, string? shiftName = null)
    {
        foreach (var row in _rows.Values.Where(r => r.IsActive))
        {
            row.TriggeredAt = now;
            if (!string.IsNullOrWhiteSpace(shiftName))
                row.ShiftName = shiftName;
        }
        return true;
    }

    /// <summary>测试辅助：读取单行（无则 null）。</summary>
    public ActiveAlarmStateRecord? GetRow(string deviceId, string alarmId)
        => _rows.TryGetValue(Key(deviceId, alarmId), out var row) ? row : null;
}