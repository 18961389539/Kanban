using System;
using System.Collections.Generic;
using System.Linq;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 报警活跃状态快照的 SQLite/EF Core 实现（alarm_events.db 的 ActiveAlarmStates 表）。
/// 边沿 Upsert 幂等；删除用 ExecuteDelete，行已不存在视为成功（恢复边沿与断线清理可并发）。
/// 写入失败无需重试集合——下轮边沿/重建时自动覆盖。
/// </summary>
public sealed class ActiveAlarmStateStore(DatabaseProvider db, ILogger<ActiveAlarmStateStore> logger)
    : IActiveAlarmStateService
{
    public bool UpsertActive(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, bool isActive, DateTime triggeredAt,
        string? shiftName = null)
    {
        try
        {
            using var ctx = db.CreateAlarmEventContext();
            var row = ctx.ActiveAlarmStates.FirstOrDefault(e => e.DeviceId == deviceId && e.AlarmId == alarmId);
            if (row is null)
            {
                ctx.ActiveAlarmStates.Add(new ActiveAlarmStateRecord
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
                });
            }
            else
            {
                row.DeviceName = deviceName;
                row.AlarmName = alarmName;
                row.PlcAddress = plcAddress;
                row.IsActive = isActive;
                row.TriggeredAt = triggeredAt;
                row.ShiftName = shiftName ?? row.ShiftName;
                row.UpdatedAt = DateTime.Now;
            }
            ctx.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "报警活跃状态 Upsert 失败（Device={Device} Alarm={Alarm}）", deviceId, alarmId);
            return false;
        }
    }

    public List<ActiveAlarmStateRecord> QueryActive(string? deviceId = null)
    {
        try
        {
            using var ctx = db.CreateAlarmEventContext();
            var query = ctx.ActiveAlarmStates.AsNoTracking().Where(e => e.IsActive);
            if (!string.IsNullOrWhiteSpace(deviceId))
                query = query.Where(e => e.DeviceId == deviceId);
            return query.OrderBy(e => e.TriggeredAt).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询活跃报警失败");
            return [];
        }
    }

    public bool RemoveByDeviceId(string deviceId)
    {
        try
        {
            using var ctx = db.CreateAlarmEventContext();
            // ExecuteDelete：0 行即已不存在，避免先加载再 RemoveRange 在并发恢复/断线清理时
            // 抛 DbUpdateConcurrencyException（expected 1, affected 0）。
            ctx.ActiveAlarmStates.Where(e => e.DeviceId == deviceId).ExecuteDelete();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "删除设备报警状态失败（Device={Device}）", deviceId);
            return false;
        }
    }

    public bool RemoveByAlarm(string deviceId, string alarmId)
    {
        try
        {
            using var ctx = db.CreateAlarmEventContext();
            ctx.ActiveAlarmStates
                .Where(e => e.DeviceId == deviceId && e.AlarmId == alarmId)
                .ExecuteDelete();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "删除报警状态失败（Device={Device} Alarm={Alarm}）", deviceId, alarmId);
            return false;
        }
    }

    public bool ResetActiveSince(DateTime now, string? shiftName = null)
    {
        try
        {
            using var ctx = db.CreateAlarmEventContext();
            var rows = ctx.ActiveAlarmStates.Where(e => e.IsActive).ToList();
            if (rows.Count == 0) return true;
            foreach (var row in rows)
            {
                row.TriggeredAt = now;
                row.UpdatedAt = now;
                if (!string.IsNullOrWhiteSpace(shiftName))
                    row.ShiftName = shiftName;
            }
            ctx.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "班次切换重置活跃报警时间戳失败");
            return false;
        }
    }
}