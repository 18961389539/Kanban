using System.Threading.Channels;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// SN 数据源配置约定（文档化）：设备数据源中 Type 为 "SN" 的源参与序列号事件采集。
/// - 值项 DataType=String 的第一个成功值 = 序列号
/// - 可选值项 DataType=Int32 且名称含 "Result"/"结果" = 判定结果（0=OK，非 0=NG）
/// - 源必须配置触发地址（TriggerAddress）：仅在触发命中时记录，避免每轮重复采集
/// </summary>
public static class SnEventConventions
{
    public const string SourceType = "SN";
    public const string SourceName = "Plc";
}

/// <summary>
/// 序列号事件存储。采集线程只入队（低频：触发采集 25~45s 一次/设备），后台任务批量落库，
/// 避免 PLC 轮询线程做数据库 IO。查询读前先排空未落库记录，保证"刚采集的 SN 立即可查"。
///
/// 最小版取舍：队列溢出或落库失败仅记日志与计数，不做恢复文件回放
/// （SN 事件为低频数据，丢失概率远低于高频快照；完整恢复机制随 V2 补齐）。
/// </summary>
public interface ISnEventStore
{
    /// <summary>采集线程入队一条 SN 事件（不阻塞）。</summary>
    void Append(SnEventRecord record);

    /// <summary>按 SN 精确查询（降序），返回全部匹配事件（一个 SN 只应有一条，防御重复记录）。</summary>
    List<SnEventRecord> QueryBySn(string sn);

    /// <summary>按工单查询 SN 明细（降序，分页）。</summary>
    (int Total, List<SnEventRecord> Items) QueryByWorkOrder(int workOrderId, int page, int pageSize);

    /// <summary>按设备+时间范围查询（降序，分页；deviceId 为 null 时忽略设备过滤）。</summary>
    (int Total, List<SnEventRecord> Items) QueryByTimeRange(string? deviceId, DateTime from, DateTime to, int page, int pageSize);

    /// <summary>诊断快照（运行监控页展示写入健康度）。</summary>
    SnEventStoreDiagnosticsSnapshot GetDiagnosticsSnapshot();
}

/// <summary>SN 事件写入健康度快照。</summary>
public sealed record SnEventStoreDiagnosticsSnapshot
{
    public int PendingCount { get; init; }
    public long TotalFlushedCount { get; init; }
    public long OverflowCount { get; init; }
    public int FlushFailureCount { get; init; }
    public DateTime? LastFlushAt { get; init; }
}

/// <summary><see cref="ISnEventStore"/> 的默认实现（有界队列 + 后台批量落库）。</summary>
public sealed class SnEventStore : ISnEventStore, IDisposable
{
    private const int MaxQueueLength = 1024;
    private const int BatchSize = 100;
    private const int FlushIntervalMs = 2000;

    private readonly DatabaseProvider _databaseProvider;
    private readonly ILogger _logger;
    private readonly Channel<SnEventRecord> _channel = Channel.CreateBounded<SnEventRecord>(
        new BoundedChannelOptions(MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private readonly object _diagnosticsLock = new();
    /// <summary>落库互斥门：查询前排空（FlushBeforeQuery）与后台 FlushLoop 可能并发进入 FlushPendingAsync，
    /// 不加门会导致双线程并发写 SQLite（busy_timeout 内未拿到写锁则抛异常，该批记录丢失）。
    /// 参照 DataSourceSnapshotStore 的 _flushGate 模式（审查修复 2026-08-30）。</summary>
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private long _totalFlushedCount;
    private long _overflowCount;
    private int _flushFailureCount;
    private DateTime? _lastFlushAt;

    public SnEventStore(DatabaseProvider databaseProvider, ILogger<SnEventStore>? logger = null)
    {
        _databaseProvider = databaseProvider;
        _logger = logger ?? NullLogger<SnEventStore>.Instance;
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    public void Append(SnEventRecord record)
    {
        if (!_channel.Writer.TryWrite(record))
        {
            Interlocked.Increment(ref _overflowCount);
            _logger.LogWarning("SN 事件后台队列已满，丢弃 1 条（SN={Sn}）", record.Sn);
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var hasData = await reader.WaitToReadAsync(cancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(FlushIntervalMs), cancellationToken)
                    .ConfigureAwait(false);
                if (!hasData) break;
                await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SN 事件后台写入异常");
                try { await Task.Delay(FlushIntervalMs, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        try { await FlushPendingAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "SN 事件停机排空失败"); }
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        // gate 等待不可取消：持有时间仅毫秒级（读队列+写库），若可取消，后台 FlushLoop 的
        // WaitAsync 超时会抛 OperationCanceledException 被外层误判为"退出信号"而终止落库循环。
        await _flushGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await FlushPendingCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>实际落库段（调用方须持有 _flushGate，保证与后台 FlushLoop/查询排空互斥）。</summary>
    private async Task FlushPendingCoreAsync(CancellationToken cancellationToken)
    {
        var batch = new List<SnEventRecord>(BatchSize);
        if (!_channel.Reader.TryRead(out var first)) return;
        batch.Add(first);
        while (batch.Count < BatchSize && _channel.Reader.TryRead(out var record))
            batch.Add(record);

        try
        {
            await using var context = _databaseProvider.CreateSnEventContext();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            context.SnEvents.AddRange(batch);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lock (_diagnosticsLock)
            {
                _totalFlushedCount += batch.Count;
                _lastFlushAt = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            lock (_diagnosticsLock) _flushFailureCount++;
            _logger.LogError(ex, "批量写入 SN 事件失败，{Count} 条已丢失", batch.Count);
        }
    }

    private void FlushBeforeQuery()
    {
        try { FlushPendingAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            // 查询仍返回已提交数据；未提交记录保留在队列中，避免读路径因暂时不可写而失去可用性。
            _logger.LogWarning(ex, "查询前排空 SN 事件写入队列失败，将返回已提交数据");
        }
    }

    public List<SnEventRecord> QueryBySn(string sn)
    {
        FlushBeforeQuery();
        using var context = _databaseProvider.CreateSnEventContext();
        return context.SnEvents.AsNoTracking()
            .Where(record => record.Sn == sn)
            .OrderByDescending(record => record.Timestamp)
            .ThenByDescending(record => record.Id)
            .ToList();
    }

    public (int Total, List<SnEventRecord> Items) QueryByWorkOrder(int workOrderId, int page, int pageSize)
    {
        FlushBeforeQuery();
        using var context = _databaseProvider.CreateSnEventContext();
        var query = context.SnEvents.AsNoTracking()
            .Where(record => record.WorkOrderId == workOrderId);
        var total = query.Count();
        var items = query
            .OrderByDescending(record => record.Timestamp)
            .ThenByDescending(record => record.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return (total, items);
    }

    public (int Total, List<SnEventRecord> Items) QueryByTimeRange(
        string? deviceId, DateTime from, DateTime to, int page, int pageSize)
    {
        FlushBeforeQuery();
        using var context = _databaseProvider.CreateSnEventContext();
        var query = context.SnEvents.AsNoTracking()
            .Where(record => record.Timestamp >= from && record.Timestamp <= to);
        if (!string.IsNullOrEmpty(deviceId))
            query = query.Where(record => record.DeviceId == deviceId);
        var total = query.Count();
        var items = query
            .OrderByDescending(record => record.Timestamp)
            .ThenByDescending(record => record.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return (total, items);
    }

    public SnEventStoreDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        lock (_diagnosticsLock)
        {
            return new SnEventStoreDiagnosticsSnapshot
            {
                PendingCount = _channel.Reader.Count,
                TotalFlushedCount = _totalFlushedCount,
                OverflowCount = Interlocked.Read(ref _overflowCount),
                FlushFailureCount = _flushFailureCount,
                LastFlushAt = _lastFlushAt,
            };
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { _flushTask.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { _logger.LogWarning(ex, "释放 SN 事件写入器超时"); }
        _cts.Dispose();
        _flushGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
