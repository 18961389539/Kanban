using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

public sealed class StatusTransitionHistoryStore : IStatusTransitionHistoryService, IDisposable
{
    /// <summary>恢复文件大小上限：超过后停止追加并告警（有界且大声的丢失 &gt; 无限磁盘占用）。</summary>
    private const long MaxRecoveryFileBytes = 100 * 1024 * 1024;

    private readonly DatabaseProvider _db;
    private readonly ILogger<StatusTransitionHistoryStore> _logger;
    /// <summary>状态转换异步批量写入（设计审查修复 2026-09-16）：此前采集热路径同步 SaveChanges，
    /// SQLite 写锁竞争直接拖停采集循环；现入队即返回，后台批量落库，溢出/失败转存恢复文件回放。
    /// 通道 FIFO + 单读者保证同设备转换按序落库（状态机日志的顺序语义）。</summary>
    private readonly AsyncBatchWriter<StatusTransitionRecord> _writer;

    public StatusTransitionHistoryStore(DatabaseProvider db, ILogger<StatusTransitionHistoryStore> logger)
    {
        _db = db;
        _logger = logger;
        _writer = new AsyncBatchWriter<StatusTransitionRecord>(
            "状态转换",
            db.AppSettings.GetFilePath("status_transitions.recovery.jsonl"),
            MaxRecoveryFileBytes,
            InsertBatchAsync,
            logger);
    }

    private async Task InsertBatchAsync(IReadOnlyList<StatusTransitionRecord> batch, CancellationToken ct)
    {
        using var ctx = _db.CreateStatusTransitionContext();
        ctx.StatusTransitions.AddRange(batch);
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 记录状态转换（非阻塞）。返回 true = 已接收（入队或转存恢复文件，保证最终落库）；
    /// false = 存储已释放。调用方的"写失败不改内存状态"路径仅在释放后命中——正常 DB 故障由恢复文件兜底。
    /// </summary>
    public bool LogStatusTransition(string deviceId, string deviceName,
        int previousState, int currentState, DateTime eventTime,
        string? shiftName = null, int offlineCause = 0)
        => _writer.TryAccept(new StatusTransitionRecord
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            PreviousState = previousState,
            CurrentState = currentState,
            EventTime = eventTime,
            ShiftName = shiftName ?? string.Empty,
            OfflineCause = currentState == 0 ? offlineCause : 0
        });

    /// <summary>排空在途写入（"写完立即可读"路径与测试用；生产热路径不调用）。</summary>
    public void FlushPendingWrites() => _writer.Flush();

    /// <summary>测试入口：异步写入器（等待落库/断言恢复文件）。</summary>
    internal AsyncBatchWriter<StatusTransitionRecord> WriterForTest => _writer;

    public void Dispose() => _writer.Dispose();

    public List<StatusTransitionRecord> QueryStatusTransitions(
        string deviceId, DateTime from, DateTime to, string? shiftName = null)
    {
        try
        {
            return QueryStatusTransitionsStrict(deviceId, from, to, shiftName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询状态转换记录失败");
            return [];
        }
    }

    public List<StatusTransitionRecord> QueryStatusTransitionsStrict(
        string deviceId, DateTime from, DateTime to, string? shiftName = null)
    {
        using var ctx = _db.CreateStatusTransitionContext();
        var query = HistoryQueryFilter.ApplyRange(
            ctx.StatusTransitions, from, to, deviceId, shiftName,
            nameof(StatusTransitionRecord.EventTime), nameof(StatusTransitionRecord.DeviceId), nameof(StatusTransitionRecord.ShiftName));
        return query.OrderBy(s => s.EventTime).AsNoTracking().ToList();
    }

    /// <summary>
    /// 分页查询状态转换记录（SQL 层 Count + OrderByDescending + Skip/Take；异常向调用方抛出）。
    /// 供历史查询页使用——此前全量 ToList 后客户端内存分页。
    /// </summary>
    public (List<StatusTransitionRecord> Items, int Total) QueryStatusTransitionsPaged(
        string deviceId, DateTime from, DateTime to, string? shiftName, int page, int pageSize)
    {
        using var ctx = _db.CreateStatusTransitionContext();
        var query = HistoryQueryFilter.ApplyRange(
            ctx.StatusTransitions, from, to, deviceId, shiftName,
            nameof(StatusTransitionRecord.EventTime), nameof(StatusTransitionRecord.DeviceId), nameof(StatusTransitionRecord.ShiftName));
        var total = query.Count();
        var offset = HistoryPagination.Offset(page, pageSize);
        var (_, size) = HistoryPagination.Normalize(page, pageSize);
        var items = query
            .OrderByDescending(s => s.EventTime)
            // 稳定次级键：同轮采集多设备状态转换可能共享同一 EventTime，仅按时间排序翻页会重复/漏行（审查修复 2026-08-13）
            .ThenByDescending(s => s.Id)
            .Skip(offset)
            .Take(size)
            .AsNoTracking()
            .ToList();
        return (items, total);
    }

    public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null)
    {
        try
        {
            return GetLatestStatusBeforeStrict(deviceId, before, shiftName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询窗口前状态失败");
            return null;
        }
    }

    public StatusTransitionRecord? GetLatestStatusBeforeStrict(string deviceId, DateTime before, string? shiftName = null)
    {
        // 写完立即可读：异步批量化后先排空在途批次——空窗补离线（SealAcquisitionGaps）依赖
        // "最近一次转换"判定，漏读在途记录会导致重复补离线（设计审查修复 2026-09-16）。
        _writer.Flush();
        using var ctx = _db.CreateStatusTransitionContext();
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
            using var ctx = _db.CreateStatusTransitionContext();
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
            _logger.LogWarning(ex, "批量查询状态转换失败");
            return [];
        }
    }

    public int CleanupOldStatusTransitions(int retentionDays = 365) =>
        HistoryRetentionCleanup.DeleteBefore(
            _db.CreateStatusTransitionContext,
            context => ((StatusTransitionDbContext)context).StatusTransitions,
            record => record.EventTime,
            "状态转换记录",
            retentionDays,
            _logger);
}
