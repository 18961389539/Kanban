using Kanban.Core.Data;
using Kanban.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

public sealed class AlarmHistoryStore(DatabaseProvider db, ILogger<AlarmHistoryStore> logger) : IAlarmHistoryService
{
    public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, AlarmEventType eventType, DateTime eventTime,
        string? shiftName = null)
    {
        try
        {
            using var ctx = db.CreateAlarmEventContext();
            ctx.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                AlarmId = alarmId,
                AlarmName = alarmName,
                PlcAddress = plcAddress,
                EventType = eventType,
                EventTime = eventTime,
                ShiftName = shiftName ?? string.Empty
            });
            ctx.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "同步写入报警事件失败");
            return false;
        }
    }

    public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        try
        {
            return QueryAlarmEventsStrict(from, to, deviceId, shiftName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询报警事件失败");
            return [];
        }
    }

    public List<AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        using var ctx = db.CreateAlarmEventContext();
        var query = HistoryQueryFilter.ApplyRange(
            ctx.AlarmEvents, from, to, deviceId, shiftName,
            nameof(AlarmEventRecord.EventTime), nameof(AlarmEventRecord.DeviceId), nameof(AlarmEventRecord.ShiftName));
        return query.OrderBy(e => e.EventTime).AsNoTracking().ToList();
    }

    public AlarmEventRecord? GetLatestAlarmEvent(string alarmId)
    {
        try
        {
            using var ctx = db.CreateAlarmEventContext();
            return ctx.AlarmEvents
                .Where(e => e.AlarmId == alarmId)
                .OrderByDescending(e => e.EventTime)
                .AsNoTracking()
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询最新报警事件失败");
            return null;
        }
    }

    public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
    {
        try
        {
            using var ctx = db.CreateAlarmEventContext();
            var idSet = deviceIds.ToHashSet();
            return ctx.AlarmEvents.AsNoTracking()
                .Where(e => idSet.Contains(e.DeviceId) && e.EventTime >= from && e.EventTime <= to)
                .OrderBy(e => e.EventTime)
                .ToList()
                .GroupBy(e => e.DeviceId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "批量查询报警事件失败");
            return [];
        }
    }

    public int CleanupOldAlarmEvents(int retentionDays = 365) =>
        HistoryRetentionCleanup.DeleteBefore(
            db.CreateAlarmEventContext,
            context => ((AlarmEventDbContext)context).AlarmEvents,
            record => record.EventTime,
            "报警事件",
            retentionDays,
            logger);
}
