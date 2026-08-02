using MainAPP.Data;
using MainAPP.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MainAPP.Services;

public sealed class ProductionHistoryStore(DatabaseProvider db, ILogger<ProductionHistoryStore> logger)
    : IProductionHistoryReader, IWorkOrderProductionBatchQuery
{
    public List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        try
        {
            return QueryProductionLogsStrict(from, to, deviceId, shiftName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询生产快照失败");
            return [];
        }
    }

    public List<ProductionLog> QueryProductionLogsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        using var context = db.CreateProductionLogContext();
        var query = HistoryQueryFilter.ApplyRange(
            context.ProductionLogs, from, to, deviceId, shiftName,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));
        return query.OrderBy(log => log.Timestamp).AsNoTracking().ToList();
    }

    public List<ProductionLog> QueryProductionLogsByWorkOrder(int workOrderId)
    {
        try
        {
            using var context = db.CreateProductionLogContext();
            return context.ProductionLogs.AsNoTracking()
                .Where(log => log.WorkOrderId == workOrderId)
                .OrderBy(log => log.Timestamp)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "按工单查询生产快照失败（workOrderId={WorkOrderId}）", workOrderId);
            return [];
        }
    }

    public Dictionary<int, List<ProductionLog>> QueryProductionLogsByWorkOrderBatch(IReadOnlyList<int> workOrderIds)
    {
        if (workOrderIds.Count == 0) return [];
        try
        {
            var ids = workOrderIds.ToHashSet();
            using var context = db.CreateProductionLogContext();
            return context.ProductionLogs.AsNoTracking()
                .Where(log => log.WorkOrderId.HasValue && ids.Contains(log.WorkOrderId.Value))
                .OrderBy(log => log.Timestamp)
                .ToList()
                .GroupBy(log => log.WorkOrderId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "批量查询工单生产快照失败");
            return [];
        }
    }

    public ProductionLog? GetLatestProductionBefore(string deviceId, DateTime before, string shiftName)
    {
        try
        {
            return GetLatestProductionBeforeStrict(deviceId, before, shiftName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询窗口前生产基线失败（deviceId={DeviceId}, before={Before})", deviceId, before);
            return null;
        }
    }

    public ProductionLog? GetLatestProductionBeforeStrict(string deviceId, DateTime before, string shiftName)
    {
        using var context = db.CreateProductionLogContext();
        return context.ProductionLogs.AsNoTracking()
            .Where(log => log.DeviceId == deviceId
                && (log.ShiftName ?? string.Empty) == shiftName
                && log.Timestamp < before)
            .OrderByDescending(log => log.Timestamp)
            .FirstOrDefault();
    }

    public Dictionary<string, List<ProductionLog>> QueryProductionLogsBatch(
        DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
    {
        try
        {
            var idSet = deviceIds.ToHashSet();
            using var context = db.CreateProductionLogContext();
            return context.ProductionLogs.AsNoTracking()
                .Where(log => idSet.Contains(log.DeviceId) && log.Timestamp >= from && log.Timestamp <= to)
                .OrderBy(log => log.Timestamp)
                .ToList()
                .GroupBy(log => log.DeviceId)
                .ToDictionary(group => group.Key, group => group.ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "批量查询生产快照失败");
            return [];
        }
    }

    public int CleanupOldProductionLogs(int retentionDays = 365) =>
        HistoryRetentionCleanup.DeleteBefore(
            db.CreateProductionLogContext,
            context => ((ProductionLogDbContext)context).ProductionLogs,
            log => log.Timestamp,
            "生产快照",
            retentionDays,
            logger);
}
