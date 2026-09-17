using System.Threading.Channels;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 操作审计服务：有界 Channel + 后台批量落库。
/// 队列满和最终落库失败都会被准确计入诊断计数；正常释放时先完成写端并等待队列排空。
/// </summary>
public sealed class AuditService : IAuditService, IDisposable, IAsyncDisposable
{
    private const int FlushIntervalMs = 2000;
    private const int BatchSize = 200;
    private const int MaxQueueLength = 2048;
    private const int CleanupIntervalMinutes = 30;
    private const int RetentionDays = 30;
    private const int PersistenceRetryCount = 3;
    /// <summary>恢复文件大小上限：超过后停止追加并告警（有界且大声的丢失 &gt; 无限磁盘占用）。</summary>
    private const long MaxRecoveryFileBytes = 100 * 1024 * 1024;

    private readonly DatabaseProvider _db;
    private readonly ILogger<AuditService> _logger;
    private readonly Channel<AuditEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    /// <summary>每次批量落库成功释放一个令牌，供测试确定性等待（事件驱动，避免固定预算轮询在高并发下偶发超时）。</summary>
    private readonly SemaphoreSlim _flushSignal = new(0);
    private DateTime _lastCleanupAt = DateTime.Now;
    private int _queueDroppedCount;
    private int _persistenceFailedCount;
    private int _flushedCount;
    private int _disposeStarted;
    /// <summary>溢出恢复通道：队列满/重试耗尽的条目转存磁盘，后台循环回放——审计不允许静默丢失
    /// （设计审查修复 2026-09-16，照搬 ProductionHistoryWriter 恢复文件语义）。</summary>
    private readonly SpilloverRecoveryFile<AuditEntry> _spillover;

    public AuditService(DatabaseProvider db, ILogger<AuditService> logger)
        : this(db, logger, MaxQueueLength, startWorker: true)
    {
    }

    /// <summary>测试入口：可缩小容量或暂停消费者，以稳定验证队列饱和行为。</summary>
    internal AuditService(DatabaseProvider db, ILogger<AuditService> logger, int queueLength, bool startWorker)
    {
        _db = db;
        _logger = logger;
        _spillover = new SpilloverRecoveryFile<AuditEntry>(
            db.AppSettings.GetFilePath("audit.recovery.jsonl"), MaxRecoveryFileBytes, logger, "审计");
        _channel = Channel.CreateBounded<AuditEntry>(
            new BoundedChannelOptions(Math.Max(1, queueLength))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            },
            OnEntryDropped);
        _flushTask = startWorker
            ? BackgroundTaskRunner.StartLoop(FlushLoopAsync, _cts.Token, _logger, nameof(AuditService))
            : Task.CompletedTask;
    }

    /// <summary>队列满导致的丢弃条数。</summary>
    public int QueueDroppedCount => Volatile.Read(ref _queueDroppedCount);

    /// <summary>数据库重试后仍写入失败的条数。</summary>
    public int PersistenceFailedCount => Volatile.Read(ref _persistenceFailedCount);

    /// <summary>全部未落库条数（队列丢弃 + 最终写入失败）。</summary>
    public int DroppedCount => QueueDroppedCount + PersistenceFailedCount;

    /// <summary>已落库条数。</summary>
    public int FlushedCount => Volatile.Read(ref _flushedCount);

    /// <summary>
    /// 测试辅助：等待已落库条数达到 <paramref name="minCount"/>（内部以刷盘完成信号驱动，非固定预算轮询）。
    /// 返回 true=达到；false=超时（FlushedCount/DroppedCount 提供诊断信息）。internal：仅供测试程序集经 InternalsVisibleTo 使用。
    /// </summary>
    internal async Task<bool> WaitFlushedAsync(int minCount, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (FlushedCount < minCount)
        {
            var remaining = deadline - DateTime.Now;
            if (remaining <= TimeSpan.Zero) return false;
            // 令牌可能在“计数检查与等待之间”已被释放并排队，WaitAsync 立即返回后循环重查即可
            await _flushSignal.WaitAsync(remaining);
        }
        return true;
    }

    public void Record(string action, string? targetType = null, string? targetId = null,
        bool succeeded = true, string? detail = null, string? operatorName = null,
        string? beforeJson = null, string? afterJson = null)
    {
        var entry = new AuditEntry
        {
            Operator = operatorName ?? string.Empty,
            Action = action ?? string.Empty,
            TargetType = targetType ?? string.Empty,
            TargetId = targetId,
            Succeeded = succeeded,
            Detail = detail,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
        };

        // DropWrite 模式在容量满时仍返回 true；真正的丢弃由 itemDropped 回调统计。
        // false 只表示写端已经完成（服务正在释放）。
        if (!_channel.Writer.TryWrite(entry))
            OnEntryDropped(entry);
    }

    private void OnEntryDropped(AuditEntry entry)
    {
        var dropped = Interlocked.Increment(ref _queueDroppedCount);
        // 不再静默丢弃：转存恢复文件，后台循环回放落库（合规审计不允许缺口）
        _spillover.Append([entry]);
        if (dropped == 1 || dropped % 100 == 0)
            _logger.LogWarning("审计队列已满，{Dropped} 条记录已转存恢复文件待回放", dropped);
    }

    public (List<AuditEntry> Items, int Total) QueryAll(
        DateTime from, DateTime to,
        string? operatorName, string? action, string? targetType, bool? succeeded,
        int maxResults = 10000)
    {
        using var context = _db.CreateAuditContext();
        var query = BuildFilteredQuery(context, from, to, operatorName, action, targetType, succeeded);
        var total = query.Count();
        var items = query
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.Id)
            .Take(Math.Clamp(maxResults, 1, 100000))
            .ToList();
        return (items, total);
    }

    private static IQueryable<AuditEntry> BuildFilteredQuery(
        AuditDbContext context, DateTime from, DateTime to,
        string? operatorName, string? action, string? targetType, bool? succeeded)
    {
        var query = context.AuditEntries.AsNoTracking()
            .Where(entry => entry.Timestamp >= from && entry.Timestamp <= to);

        if (!string.IsNullOrWhiteSpace(operatorName))
            query = query.Where(entry => EF.Functions.Like(entry.Operator, $"%{operatorName}%"));
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(entry => EF.Functions.Like(entry.Action, $"%{action}%"));
        if (!string.IsNullOrWhiteSpace(targetType))
            query = query.Where(entry => EF.Functions.Like(entry.TargetType, $"%{targetType}%"));
        if (succeeded.HasValue)
            query = query.Where(entry => entry.Succeeded == succeeded.Value);
        return query;
    }

    public (int Succeeded, int Failed) CountByResult(
        DateTime from, DateTime to,
        string? operatorName, string? action, string? targetType, bool? succeeded)
    {
        using var context = _db.CreateAuditContext();
        var query = BuildFilteredQuery(context, from, to, operatorName, action, targetType, succeeded);
        // 单次 GROUP BY 取回两类计数，避免两次 COUNT 重复扫描
        var groups = query
            .GroupBy(entry => entry.Succeeded)
            .Select(g => new { Succeeded = g.Key, Count = g.Count() })
            .ToList();
        var ok = groups.FirstOrDefault(g => g.Succeeded)?.Count ?? 0;
        return (ok, groups.Sum(g => g.Count) - ok);
    }

    public (List<AuditEntry> Items, int Total) QueryPaged(
        DateTime from, DateTime to,
        string? operatorName, string? action, string? targetType, bool? succeeded,
        int page, int pageSize)
    {
        using var context = _db.CreateAuditContext();
        var query = BuildFilteredQuery(context, from, to, operatorName, action, targetType, succeeded);
        var total = query.Count();
        // 复用 HistoryPagination.Normalize：页码钳制上界——此前只做下界 Math.Max，
        // page=int.MaxValue 时 (page-1)*pageSize 整数溢出为负，EF Skip(负数) 直接抛异常变 500（审查修复 2026-08-13）
        var (p, s) = HistoryPagination.Normalize(page, pageSize);
        var items = query
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.Id)
            .Skip((p - 1) * s)
            .Take(s)
            .ToList();
        return (items, total);
    }

    public int CleanupOldEntries(int retentionDays = RetentionDays)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        try
        {
            using var context = _db.CreateAuditContext();
            var old = context.AuditEntries
                .Where(entry => entry.Timestamp < cutoff)
                .Take(10000)
                .ToList();
            if (old.Count == 0) return 0;
            context.AuditEntries.RemoveRange(old);
            return context.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "审计记录清理失败");
            return 0;
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hasData = await reader.WaitToReadAsync(ct).AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(FlushIntervalMs), ct);
                if (!hasData) break;
                await FlushPendingAsync(ct);
                await _spillover.ReplayAsync(InsertRecoveryBatchAsync, BatchSize, ct);
                MaybeCleanup();
            }
            catch (TimeoutException)
            {
                if (reader.Count > 0) await FlushPendingAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "审计后台写入异常");
                try { await Task.Delay(FlushIntervalMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // 正常完成或强制取消后，尽最大努力排空已经接收的记录；
        // 再尝试回放一次恢复文件（上次运行/本次批写失败转存的条目不遗留到下次启动）。
        while (reader.TryPeek(out _))
            await FlushPendingAsync(CancellationToken.None);
        try
        {
            await _spillover.ReplayAsync(InsertRecoveryBatchAsync, BatchSize, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停机时回放审计恢复文件失败（留待下次启动回放）");
        }
    }

    /// <summary>恢复文件回放批次落库（回放线程 = 后台 flush 循环，与正常批量写串行）。</summary>
    private async Task InsertRecoveryBatchAsync(IReadOnlyList<AuditEntry> batch)
    {
        using var context = _db.CreateAuditContext();
        context.AuditEntries.AddRange(batch);
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task FlushPendingAsync(CancellationToken ct)
    {
        var batch = new List<AuditEntry>(BatchSize);
        if (!_channel.Reader.TryRead(out var first)) return;
        batch.Add(first);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(10);
        try
        {
            while (batch.Count < BatchSize)
            {
                if (_channel.Reader.TryRead(out var entry)) { batch.Add(entry); continue; }
                if (!await _channel.Reader.WaitToReadAsync(timeout.Token)) break;
            }
        }
        catch (OperationCanceledException) { }

        for (var attempt = 1; attempt <= PersistenceRetryCount; attempt++)
        {
            try
            {
                using var context = _db.CreateAuditContext();
                context.AuditEntries.AddRange(batch);
                await context.SaveChangesAsync(CancellationToken.None);
                Interlocked.Add(ref _flushedCount, batch.Count);
                _flushSignal.Release(); // 唤醒可能的 WaitFlushedAsync 等待者
                return;
            }
            catch (Exception ex) when (attempt < PersistenceRetryCount)
            {
                _logger.LogWarning(ex, "审计批量写入失败，第 {Attempt}/{MaxAttempts} 次重试", attempt, PersistenceRetryCount);
                await Task.Delay(attempt * 100, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref _persistenceFailedCount, batch.Count);
                // 重试耗尽不再丢弃：转存恢复文件，由后台循环回放（设计审查修复 2026-09-16）
                _spillover.Append(batch);
                _logger.LogError(ex, "审计批量写入重试耗尽，{Count} 条已转存恢复文件（累计转存 {Failed} 条）",
                    batch.Count, PersistenceFailedCount);
                return;
            }
        }
    }

    private void MaybeCleanup()
    {
        if ((DateTime.Now - _lastCleanupAt).TotalMinutes < CleanupIntervalMinutes) return;
        _lastCleanupAt = DateTime.Now;
        var deleted = CleanupOldEntries(RetentionDays);
        if (deleted > 0)
            _logger.LogInformation("审计记录已清理 {Count} 条（保留 {Days} 天）", deleted, RetentionDays);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _channel.Writer.TryComplete();
        try
        {
            await _flushTask.WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("等待审计队列排空超时，剩余 {Count} 条", _channel.Reader.Count);
            _cts.Cancel();
            try { await _flushTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        finally
        {
            _cts.Dispose();
            _flushSignal.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 同步释放（兼容路径：测试/<c>using</c> 语句仍以 <see cref="IDisposable"/> 使用本类）。
    /// 刻意**不**转调 <see cref="DisposeAsync"/>（DisposeAsync().AsTask().GetAwaiter().GetResult()
    /// 是 sync-over-async，UI 上下文会死锁）；改为与 <see cref="DisposeAsync"/> 同语义的直接 Wait，
    /// 与 DefectHistoryStore 的同步释放惯例一致（审查修复 2026-09-17）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _channel.Writer.TryComplete();
        try
        {
            _flushTask.Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "等待审计队列排空超时，剩余 {Count} 条", _channel.Reader.Count);
            _cts.Cancel();
            try { _flushTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        finally
        {
            _cts.Dispose();
            _flushSignal.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>测试入口：同步触发一次恢复文件回放（生产路径由 FlushLoop 自动调用）。</summary>
    internal Task ReplayRecoveryForTestAsync(CancellationToken ct = default)
        => _spillover.ReplayAsync(InsertRecoveryBatchAsync, BatchSize, ct);

    /// <summary>测试入口：恢复文件路径（断言溢出转存）。</summary>
    internal string RecoveryFilePathForTest => _spillover.Path;
}
