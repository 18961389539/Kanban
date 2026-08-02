using Kanban.Core.Data;
using Kanban.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

public sealed class StatusTransitionHistoryStore(DatabaseProvider db, ILogger<StatusTransitionHistoryStore> logger) : IStatusTransitionHistoryService
{
    public bool LogStatusTransition(string deviceId, string deviceName,
        int previousState, int currentState, DateTime eventTime,
        string? shiftName = null)
    {
        try
        {
            using var ctx = db.CreateStatusTransitionContext();
            ctx.StatusTransitions.Add(new StatusTransitionRecord
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                PreviousState = previousState,
                CurrentState = currentState,
                EventTime = eventTime,
                ShiftName = shiftName ?? string.Empty
            });
            ctx.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "同步写入状态转换事件失败");
            return false;
        }
    }

    public List<StatusTransitionRecord> QueryStatusTransitions(
        string deviceId, DateTime from, DateTime to, string? shiftName = null)
    {
        try
        {
            return QueryStatusTransitionsStrict(deviceId, from, to, shiftName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询状态转换记录失败");
            return [];
        }
    }

    public List<StatusTransitionRecord> QueryStatusTransitionsStrict(
        string deviceId, DateTime from, DateTime to, string? shiftName = null)
    {
        using var ctx = db.CreateStatusTransitionContext();
        var query = HistoryQueryFilter.ApplyRange(
            ctx.StatusTransitions, from, to, deviceId, shiftName,
            nameof(StatusTransitionRecord.EventTime), nameof(StatusTransitionRecord.DeviceId), nameof(StatusTransitionRecord.ShiftName));
        return query.OrderBy(s => s.EventTime).AsNoTracking().ToList();
    }

    public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null)
    {
        try
        {
            return GetLatestStatusBeforeStrict(deviceId, before, shiftName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询窗口前状态失败");
            return null;
        }
    }

    public StatusTransitionRecord? GetLatestStatusBeforeStrict(string deviceId, DateTime before, string? shiftName = null)
    {
        using var ctx = db.CreateStatusTransitionContext();
        var query = ctx.StatusTransitions.AsNoTracking()
            .Where(s => s.DeviceId == deviceId && s.EventTime < before);
        if (shiftName != null)
            query = query.Where(s => s.ShiftName == shiftName);
        return query.OrderByDescending(s => s.EventTime).FirstOrDefault();
    }

    public Dictionary<string, List<StatusTransitionRecord>> QueryStatusTransitionsBatch(
        DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
    {
        try
        {
            using var ctx = db.CreateStatusTransitionContext();
            var idSet = deviceIds.ToHashSet();
            return ctx.StatusTransitions.AsNoTracking()
                .Where(s => idSet.Contains(s.DeviceId) && s.EventTime >= from && s.EventTime <= to)
                .OrderBy(s => s.EventTime)
                .ToList()
                .GroupBy(s => s.DeviceId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "批量查询状态转换失败");
            return [];
        }
    }

    public int CleanupOldStatusTransitions(int retentionDays = 365) =>
        HistoryRetentionCleanup.DeleteBefore(
            db.CreateStatusTransitionContext,
            context => ((StatusTransitionDbContext)context).StatusTransitions,
            record => record.EventTime,
            "状态转换记录",
            retentionDays,
            logger);
}
