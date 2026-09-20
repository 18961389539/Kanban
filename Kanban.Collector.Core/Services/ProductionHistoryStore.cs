using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

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

    /// <summary>
    /// SQL 层 15 分钟桶末抽样：GROUP BY DeviceId + ShiftName + 墙钟 15 分钟桶，保留 MAX(Id)。
    /// 失败时回退全量 Strict + 内存抽样（口径对齐 <see cref="Kanban.Analysis.ProductionWindowMetrics.Sample15Min"/>）。
    /// </summary>
    public List<ProductionLog> QueryProductionLogsSampled15Min(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        try
        {
            using var context = db.CreateProductionLogContext();
            var device = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            var shift = string.IsNullOrEmpty(shiftName) ? null : shiftName;
            return context.ProductionLogs
                .FromSqlInterpolated($@"
SELECT p.Id, p.DeviceId, p.DeviceName, p.ShiftName, p.WorkOrderId, p.OkProduction, p.NgProduction, p.StatusWord, p.Timestamp, p.EventId
FROM ProductionLogs p
INNER JOIN (
  SELECT MAX(Id) AS Id
  FROM ProductionLogs
  WHERE Timestamp >= {from} AND Timestamp <= {to}
    AND ({device} IS NULL OR DeviceId = {device})
    AND ({shift} IS NULL OR ShiftName = {shift})
  GROUP BY DeviceId, ShiftName,
    strftime('%Y-%m-%d %H:', Timestamp) || printf('%02d', (CAST(strftime('%M', Timestamp) AS INTEGER) / 15) * 15)
) b ON p.Id = b.Id
ORDER BY p.Timestamp")
                .AsNoTracking()
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "15 分钟抽样 SQL 失败，回退内存抽样");
            return SampleInMemory(QueryProductionLogsStrict(from, to, deviceId, shiftName));
        }
    }

    private static List<ProductionLog> SampleInMemory(List<ProductionLog> logs)
    {
        if (logs.Count == 0) return [];
        var dtos = logs.Select(p => new Kanban.Contracts.Dtos.ProductionLogDto
        {
            Id = p.Id,
            DeviceId = p.DeviceId,
            DeviceName = p.DeviceName,
            ShiftName = p.ShiftName,
            WorkOrderId = p.WorkOrderId,
            OkProduction = p.OkProduction,
            NgProduction = p.NgProduction,
            StatusWord = p.StatusWord,
            Timestamp = p.Timestamp,
        }).ToList();
        var sampled = Kanban.Analysis.ProductionWindowMetrics.Sample15Min(dtos);
        var byId = logs.ToDictionary(l => l.Id);
        return sampled.Select(d => byId[d.Id]).ToList();
    }

    /// <summary>
    /// 分页查询生产日志（服务端分页下推 SQL：Skip/Take + 独立 Count）。
    /// 供历史查询页使用——历史查询此前全量 ToList 再客户端内存分页，7 天 × 500ms 采样
    /// 可达百万级记录全量经 SignalR 传输；分页下推后只传输单页。
    /// </summary>
    public (List<ProductionLog> Items, int Total) QueryProductionLogsPaged(
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize)
    {
        using var context = db.CreateProductionLogContext();
        var query = HistoryQueryFilter.ApplyRange(
            context.ProductionLogs, from, to, deviceId, shiftName,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));
        var total = query.Count();
        var offset = HistoryPagination.Offset(page, pageSize);
        var (_, size) = HistoryPagination.Normalize(page, pageSize);
        var items = query
            .OrderBy(log => log.Timestamp)
            // 稳定次级键：同轮采集各设备共享同一 Timestamp，仅按时间排序翻页会重复/漏行（审查修复 2026-08-13）
            .ThenBy(log => log.Id)
            .Skip(offset)
            .Take(size)
            .AsNoTracking()
            .ToList();
        return (items, total);
    }

    /// <summary>最新一条生产日志（SQL 层 OrderByDescending().Take(1)，替代"全量拉取再内存 Take(1)"）。</summary>
    public ProductionLog? QueryLatestProductionLog(DateTime from, DateTime to, string? deviceId, string? shiftName)
    {
        using var context = db.CreateProductionLogContext();
        var query = HistoryQueryFilter.ApplyRange(
            context.ProductionLogs, from, to, deviceId, shiftName,
            nameof(ProductionLog.Timestamp), nameof(ProductionLog.DeviceId), nameof(ProductionLog.ShiftName));
        return query.OrderByDescending(log => log.Timestamp).AsNoTracking().FirstOrDefault();
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

    /// <inheritdoc cref="IWorkOrderProductionBatchQuery.QueryProductionLogsByDeviceWindowsBatch" />
    public Dictionary<int, List<ProductionLog>> QueryProductionLogsByDeviceWindowsBatch(
        IReadOnlyList<(int WorkOrderId, string DeviceId, DateTime From, DateTime To)> windows)
    {
        var result = new Dictionary<int, List<ProductionLog>>();
        foreach (var (workOrderId, deviceId, from, to) in windows)
        {
            // Local 模式：SQLite 本地查询廉价，逐窗口执行（与原逐条回退路径同构，无网络往返问题）
            var logs = QueryProductionLogs(from, to, deviceId);
            if (logs.Count > 0) result[workOrderId] = logs;
        }
        return result;
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
