using System.Threading.Channels;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

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

    private readonly DatabaseProvider _db;
    private readonly ILogger<AuditService> _logger;
    private readonly Channel<AuditEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private DateTime _lastCleanupUtc = DateTime.UtcNow;
    private int _queueDroppedCount;
    private int _persistenceFailedCount;
    private int _flushedCount;
    private int _disposeStarted;

    public AuditService(DatabaseProvider db, ILogger<AuditService> logger)
        : this(db, logger, MaxQueueLength, startWorker: true)
    {
    }

    /// <summary>测试入口：可缩小容量或暂停消费者，以稳定验证队列饱和行为。</summary>
    internal AuditService(DatabaseProvider db, ILogger<AuditService> logger, int queueLength, bool startWorker)
    {
        _db = db;
        _logger = logger;
        _channel = Channel.CreateBounded<AuditEntry>(
            new BoundedChannelOptions(Math.Max(1, queueLength))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            },
            OnEntryDropped);
        _flushTask = startWorker ? Task.Run(() => FlushLoopAsync(_cts.Token)) : Task.CompletedTask;
    }

    /// <summary>队列满导致的丢弃条数。</summary>
    public int QueueDroppedCount => Volatile.Read(ref _queueDroppedCount);

    /// <summary>数据库重试后仍写入失败的条数。</summary>
    public int PersistenceFailedCount => Volatile.Read(ref _persistenceFailedCount);

    /// <summary>全部未落库条数（队列丢弃 + 最终写入失败）。</summary>
    public int DroppedCount => QueueDroppedCount + PersistenceFailedCount;

    /// <summary>已落库条数。</summary>
    public int FlushedCount => Volatile.Read(ref _flushedCount);

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

    private void OnEntryDropped(AuditEntry _)
    {
        var dropped = Interlocked.Increment(ref _queueDroppedCount);
        if (dropped == 1 || dropped % 100 == 0)
            _logger.LogWarning("审计队列已丢弃 {Dropped} 条记录", dropped);
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

        // 正常完成或强制取消后，尽最大努力排空已经接收的记录。
        while (reader.TryPeek(out _))
            await FlushPendingAsync(CancellationToken.None);
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
                _logger.LogError(ex, "审计批量写入重试耗尽，丢弃 {Count} 条（累计失败 {Failed} 条）",
                    batch.Count, PersistenceFailedCount);
                return;
            }
        }
    }

    private void MaybeCleanup()
    {
        if ((DateTime.UtcNow - _lastCleanupUtc).TotalMinutes < CleanupIntervalMinutes) return;
        _lastCleanupUtc = DateTime.UtcNow;
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
            GC.SuppressFinalize(this);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
