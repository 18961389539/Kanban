using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

public sealed class AlarmHistoryStore : IAlarmHistoryService, IDisposable
{
    /// <summary>恢复文件大小上限：超过后停止追加并告警（有界且大声的丢失 &gt; 无限磁盘占用）。</summary>
    private const long MaxRecoveryFileBytes = 100 * 1024 * 1024;

    private readonly DatabaseProvider _db;
    private readonly ILogger<AlarmHistoryStore> _logger;
    private readonly ActiveAlarmStateStore _activeStateStore;
    /// <summary>边沿事件异步批量写入（设计审查修复 2026-09-16）：此前采集热路径同步 SaveChanges，
    /// SQLite 写锁竞争直接拖停采集循环；现入队即返回，后台批量落库，溢出/失败转存恢复文件回放。</summary>
    private readonly AsyncBatchWriter<AlarmEventRecord> _writer;

    public AlarmHistoryStore(DatabaseProvider db, ILogger<AlarmHistoryStore> logger, ActiveAlarmStateStore? activeStateStore = null)
    {
        _db = db;
        _logger = logger;
        _activeStateStore = activeStateStore
            ?? new ActiveAlarmStateStore(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ActiveAlarmStateStore>.Instance);
        _writer = new AsyncBatchWriter<AlarmEventRecord>(
            "报警事件",
            db.AppSettings.GetFilePath("alarm_events.recovery.jsonl"),
            MaxRecoveryFileBytes,
            InsertBatchAsync,
            logger);
    }

    private async Task InsertBatchAsync(IReadOnlyList<AlarmEventRecord> batch, CancellationToken ct)
    {
        using var ctx = _db.CreateAlarmEventContext();
        ctx.AlarmEvents.AddRange(batch);
        await ctx.SaveChangesAsync(ct);
    }

    public List<ActiveAlarmStateRecord> QueryActiveAlarmStates(string? deviceId = null)
        => _activeStateStore.QueryActive(deviceId);

    /// <summary>
    /// 记录报警边沿事件（非阻塞）。返回 true = 已接收（入队或转存恢复文件，保证最终落库）；
    /// false = 存储已释放。调用方的"写失败下轮重试"路径仅在释放后命中——正常 DB 故障由恢复文件兜底。
    /// </summary>
    public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, AlarmEventType eventType, DateTime eventTime,
        string? shiftName = null)
        => _writer.TryAccept(new AlarmEventRecord
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

    /// <summary>排空在途写入（"写完立即可读"路径与测试用；生产热路径不调用）。</summary>
    public void FlushPendingWrites() => _writer.Flush();

    /// <summary>测试入口：异步写入器（等待落库/断言恢复文件）。</summary>
    internal AsyncBatchWriter<AlarmEventRecord> WriterForTest => _writer;

    public void Dispose() => _writer.Dispose();

    public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        try
        {
            return QueryAlarmEventsStrict(from, to, deviceId, shiftName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询报警事件失败");
            return [];
        }
    }

    public List<AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
    {
        using var ctx = _db.CreateAlarmEventContext();
        var query = HistoryQueryFilter.ApplyRange(
            ctx.AlarmEvents, from, to, deviceId, shiftName,
            nameof(AlarmEventRecord.EventTime), nameof(AlarmEventRecord.DeviceId), nameof(AlarmEventRecord.ShiftName));
        return query.OrderBy(e => e.EventTime).AsNoTracking().ToList();
    }

    /// <summary>
    /// 分页查询报警事件（SQL 层 Count + OrderByDescending + Skip/Take；异常向调用方抛出）。
    /// 供历史查询页使用——此前全量 ToList 后客户端内存分页，长时间范围可一次拉取数十万条。
    /// </summary>
    public (List<AlarmEventRecord> Items, int Total) QueryAlarmEventsPaged(
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize)
    {
        using var ctx = _db.CreateAlarmEventContext();
        var query = HistoryQueryFilter.ApplyRange(
            ctx.AlarmEvents, from, to, deviceId, shiftName,
            nameof(AlarmEventRecord.EventTime), nameof(AlarmEventRecord.DeviceId), nameof(AlarmEventRecord.ShiftName));
        var total = query.Count();
        var offset = HistoryPagination.Offset(page, pageSize);
        var (_, size) = HistoryPagination.Normalize(page, pageSize);
        var items = query
            .OrderByDescending(e => e.EventTime)
            // 稳定次级键：同轮采集多个报警事件可能共享同一 EventTime，仅按时间排序翻页会重复/漏行（审查修复 2026-08-13）
            .ThenByDescending(e => e.Id)
            .Skip(offset)
            .Take(size)
            .AsNoTracking()
            .ToList();
        return (items, total);
    }

    public AlarmEventRecord? GetLatestAlarmEvent(string alarmId)
    {
        try
        {
            return GetLatestAlarmEventStrict(alarmId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询最新报警事件失败");
            return null;
        }
    }

    public AlarmEventRecord? GetLatestAlarmEventStrict(string alarmId)
    {
        _writer.Flush(); // 写完立即可读：异步批量化后先排空在途批次（边沿量小，排空代价可忽略）
        using var ctx = _db.CreateAlarmEventContext();
        return ctx.AlarmEvents
            .Where(e => e.AlarmId == alarmId)
            .OrderByDescending(e => e.EventTime)
            .ThenByDescending(e => e.Id)
            .AsNoTracking()
            .FirstOrDefault();
    }

    public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
    {
        try
        {
            using var ctx = _db.CreateAlarmEventContext();
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
            _logger.LogWarning(ex, "批量查询报警事件失败");
            return [];
        }
    }

    public int CleanupOldAlarmEvents(int retentionDays = 365) =>
        HistoryRetentionCleanup.DeleteBefore(
            _db.CreateAlarmEventContext,
            context => ((AlarmEventDbContext)context).AlarmEvents,
            record => record.EventTime,
            "报警事件",
            retentionDays,
            _logger);
}
