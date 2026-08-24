using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

public sealed class ProductionHistoryWriter : IProductionHistoryWriter, IDisposable, IAsyncDisposable
{
    private const int FlushIntervalMs = 5000;
    private const int BatchSize = 200;
    private const int MaxQueueLength = 10000;
    /// <summary>恢复文件大小上限：超过后停止回放并告警（防止异常写入把文件撑到 GB 级导致启动/回放卡死）。</summary>
    private const long MaxRecoveryFileBytes = 200 * 1024 * 1024;
    /// <summary>回放失败退避间隔：DB 持续不可写时避免每 5s 全量重读恢复文件并持锁插库拖停采集链路（审查修复 2026-08-13）。</summary>
    private static readonly TimeSpan ReplayRetryDelay = TimeSpan.FromSeconds(30);
    /// <summary>上次回放失败时刻（仅 flush 循环线程读写）；非 null 且在退避窗口内时跳过回放。</summary>
    private DateTime? _lastReplayFailureAt;
    /// <summary>测试用：覆盖恢复文件大小上限（生产保持 <see cref="MaxRecoveryFileBytes"/>；审查修复 2026-08-13 新增）。</summary>
    internal long MaxRecoveryFileBytesOverride { get; set; } = MaxRecoveryFileBytes;
    private readonly DatabaseProvider _db;
    private readonly ILogger<ProductionHistoryWriter> _logger;
    private readonly string _recoveryFilePath;
    private readonly Channel<ProductionLog> _channel = Channel.CreateBounded<ProductionLog>(
        new BoundedChannelOptions(MaxQueueLength) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly object _recoveryLock = new();
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private readonly object _diagnosticsLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private readonly Queue<long> _flushDurations = new();
    private DateTime? _lastFlushAt;
    private int _flushFailureCount;
    private int _totalFlushedCount;
    private long _queuePeak;
    private long _overflowCount;
    /// <summary>恢复文件当前行数（增量维护，避免诊断快照每轮全文件数行）。</summary>
    private long _recoveryLineCount;

    public ProductionHistoryWriter(DatabaseProvider db, AppSettings settings, ILogger<ProductionHistoryWriter> logger)
    {
        _db = db;
        _logger = logger;
        _recoveryFilePath = settings.GetFilePath("production_logs.recovery.jsonl");
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    public ProductionWriterDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        lock (_diagnosticsLock)
        {
            var recoveryBytes = 0L;
            try { if (File.Exists(_recoveryFilePath)) recoveryBytes = new FileInfo(_recoveryFilePath).Length; }
            catch { }
            return new ProductionWriterDiagnosticsSnapshot
            {
                PendingCount = _channel.Reader.Count,
                QueuePeakCount = _queuePeak,
                OverflowCount = _overflowCount,
                RecoveryFileExists = recoveryBytes > 0,
                RecoveryFileBytes = recoveryBytes,
                RecoveryFileLines = _recoveryLineCount,
                LastFlushAt = _lastFlushAt,
                FlushFailureCount = _flushFailureCount,
                TotalFlushedCount = _totalFlushedCount,
                FlushP95Milliseconds = Percentile(_flushDurations, 0.95),
                FlushP99Milliseconds = Percentile(_flushDurations, 0.99),
            };
        }
    }

    public void LogProduction(ProductionLog log)
    {
        if (!_channel.Writer.TryWrite(log))
        {
            Interlocked.Increment(ref _overflowCount);
            PersistRecoveryLogs([log]);
            _logger.LogWarning("生产快照通道已满，已转存本地恢复文件");
        }
        else
        {
            UpdateQueuePeak(_channel.Reader.Count);
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
                await ReplayRecoveryAsync(ct);
            }
            catch (TimeoutException)
            {
                if (reader.Count > 0) await FlushPendingAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "生产历史后台写入异常");
                try { await Task.Delay(FlushIntervalMs, ct); } catch (OperationCanceledException) { break; }
            }
        }

        // 停机排空：通道内剩余批次全部落库后再回放恢复文件（此前仅在循环内回放，
        // 若停机时通道已空、恢复文件仍有数据（上次批写失败转存），会遗留到下次启动才回放）
        while (reader.Count > 0)
        {
            var before = reader.Count;
            await FlushPendingAsync(CancellationToken.None);
            if (reader.Count >= before) break;
        }
        await ReplayRecoveryAsync(CancellationToken.None);
    }

    private async Task FlushPendingAsync(CancellationToken ct)
    {
        var batch = new List<ProductionLog>(BatchSize);
        if (!_channel.Reader.TryRead(out var first)) return;
        batch.Add(first);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(10);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (batch.Count < BatchSize)
            {
                if (_channel.Reader.TryRead(out var log)) { batch.Add(log); continue; }
                if (!await _channel.Reader.WaitToReadAsync(timeout.Token)) break;
            }
        }
        catch (OperationCanceledException) { }

        try
        {
            using var context = _db.CreateProductionLogContext();
            using var transaction = context.Database.BeginTransaction();
            context.ProductionLogs.AddRange(batch);
            context.SaveChanges();
            transaction.Commit();
            lock (_diagnosticsLock)
            {
                _lastFlushAt = DateTime.Now;
                _totalFlushedCount += batch.Count;
                _flushDurations.Enqueue(stopwatch.ElapsedMilliseconds);
                while (_flushDurations.Count > 1024) _flushDurations.Dequeue();
            }
        }
        catch (Exception ex)
        {
            lock (_diagnosticsLock) _flushFailureCount++;
            PersistRecoveryLogs(batch);
            _logger.LogError(ex, "批量写入生产快照失败，已转存 {Count} 条", batch.Count);
        }
    }

    private void PersistRecoveryLogs(IEnumerable<ProductionLog> logs)
    {
        try
        {
            var lines = logs.Select(log => JsonSerializer.Serialize(log)).ToList();
            if (lines.Count == 0) return;
            lock (_recoveryLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_recoveryFilePath)!);
                // 追加前大小上限检查（审查修复 2026-08-13）：此前上限只在回放入口检查，
                // DB 持续不可写时文件可无限增长到 GB 级——超限停止追加并告警，
                // CollectorReadinessCheck 会把 RecoveryFileBytes 超限转为 Degraded 健康态（人工介入）。
                if (File.Exists(_recoveryFilePath))
                {
                    var newBytes = lines.Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + 1);
                    if (new FileInfo(_recoveryFilePath).Length + newBytes > MaxRecoveryFileBytesOverride)
                    {
                        _logger.LogError("恢复文件超过上限（{Max:N0} 字节），停止追加转存；请人工处理 {Path}",
                            MaxRecoveryFileBytesOverride, _recoveryFilePath);
                        return;
                    }
                }
                var existingLines = File.Exists(_recoveryFilePath)
                    ? File.ReadAllLines(_recoveryFilePath).ToList()
                    : [];
                existingLines.AddRange(lines);
                WriteRecoveryFileAtomic(existingLines);
                lock (_diagnosticsLock) _recoveryLineCount = existingLines.Count;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "生产快照恢复文件写入失败"); }
    }

    /// <summary>
    /// 恢复文件回放（幂等 + 分块落库 + 原子收尾）：
    /// - 按批（<see cref="BatchSize"/>）处理，不再一次性 AddRange 整个文件；
    /// - 按 EventId 唯一索引去重，回放失败重试/回放后崩溃不会重复落库；
    /// - 处理完成后删除恢复文件（回放期间有新追加则保留，留待下一轮回放）；
    /// - 超过 <see cref="MaxRecoveryFileBytes"/> 时停止回放并告警（防异常写入撑爆内存/IO）；
    /// - 失败退避 <see cref="ReplayRetryDelay"/>：DB 不可用时不再每 5s 全量重读+持锁插库（审查修复 2026-08-13）。
    /// </summary>
    private async Task ReplayRecoveryAsync(CancellationToken ct)
    {
        if (!File.Exists(_recoveryFilePath)) return;
        await _replayGate.WaitAsync(ct);
        try
        {
            await ReplayRecoveryCoreAsync(ct);
        }
        finally
        {
            _replayGate.Release();
        }
    }

    private Task ReplayRecoveryCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_recoveryFilePath)) return Task.CompletedTask;

        // 失败退避：上次回放失败后 30s 内不再重试（成功路径会清零标记）
        if (_lastReplayFailureAt is { } lastFailure && DateTime.UtcNow - lastFailure < ReplayRetryDelay)
            return Task.CompletedTask;

        try
        {
            // 阶段 1（锁内）：读取文件快照——锁只保护"读取与收尾删除"的原子性，
            // DB 插入（慢 IO，busy_timeout 最长 5s）移出锁外，通道满时 PersistRecoveryLogs 不再被插库阻塞（审查修复 2026-08-13）
            string[] allLines;
            RecoveryFileStamp snapshotStamp;
            lock (_recoveryLock)
            {
                if (new FileInfo(_recoveryFilePath).Length > MaxRecoveryFileBytesOverride)
                {
                    _logger.LogError("恢复文件超过上限（{Max:N0} 字节），停止回放；请人工处理 {Path}",
                        MaxRecoveryFileBytesOverride, _recoveryFilePath);
                    return Task.CompletedTask;
                }

                snapshotStamp = CaptureRecoveryFileStamp();
                allLines = File.ReadAllLines(_recoveryFilePath);
                if (allLines.Length == 0)
                {
                    File.Delete(_recoveryFilePath);
                    return Task.CompletedTask;
                }
            }

            // 阶段 2（锁外）：解析 + 幂等插库
            var badLines = new List<string>();
            var totalReplayed = 0;
            var processed = 0;

            while (processed < allLines.Length)
            {
                ct.ThrowIfCancellationRequested();

                var batch = new List<ProductionLog>(BatchSize);
                var scanned = 0;
                while (processed < allLines.Length && scanned < BatchSize)
                {
                    var line = allLines[processed++];
                    scanned++;
                    try
                    {
                        var log = JsonSerializer.Deserialize<ProductionLog>(line);
                        if (log is null) { badLines.Add(line); continue; }
                        batch.Add(log);
                    }
                    catch (JsonException ex)
                    {
                        badLines.Add(line);
                        _logger.LogError(ex, "恢复文件存在损坏记录，已转存 .bad 文件");
                    }
                }

                if (batch.Count > 0)
                {
                    InsertBatchIdempotent(batch);
                    totalReplayed += batch.Count;
                }
            }

            // 阶段 3（重取锁收尾）：回放期间可能又有新追加（通道满转存）——
            // 写时间戳变化则跳过删除，文件留待下一轮回放处理新尾部，避免丢数据
            lock (_recoveryLock)
            {
                if (!File.Exists(_recoveryFilePath))
                    return Task.CompletedTask;
                var currentStamp = CaptureRecoveryFileStamp();
                if (currentStamp.Length > snapshotStamp.Length)
                {
                    _logger.LogInformation("回放期间恢复文件有新追加，保留文件留待下一轮回放");
                    _lastReplayFailureAt = null;
                    return Task.CompletedTask;
                }
                if (currentStamp.Length < snapshotStamp.Length || currentStamp.Hash != snapshotStamp.Hash || currentStamp.LastWriteUtc != snapshotStamp.LastWriteUtc)
                {
                    _logger.LogWarning("恢复文件在回放期间被替换或截断，保留现有文件避免丢失");
                    return Task.CompletedTask;
                }

                // 原子收尾：
                // - 全部处理成功且无坏行 → 删除恢复文件（回放完成）；
                // - 存在坏行 → 坏行追加转存 .bad，恢复文件只保留"未处理的有效尾部"（断点续传语义，
                //   坏行不回写恢复文件，否则每次回放都会反复失败）。
                if (badLines.Count == 0)
                {
                    File.Delete(_recoveryFilePath);
                    lock (_diagnosticsLock) _recoveryLineCount = 0;
                    if (totalReplayed > 0)
                        _logger.LogInformation("恢复文件回放完成：共 {Total} 条", totalReplayed);
                }
                else
                {
                    var badPath = _recoveryFilePath + ".bad";
                    try
                    {
                        var existingBad = File.Exists(badPath) ? File.ReadAllLines(badPath).ToList() : [];
                        existingBad.AddRange(badLines);
                        WriteRecoveryFileAtomicTo(badPath, existingBad);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "恢复文件损坏记录转存 .bad 失败，保留原恢复文件");
                        return Task.CompletedTask;
                    }
                    _logger.LogWarning("恢复文件 {Count} 条损坏记录已转存 .bad 文件", badLines.Count);
                    File.Delete(_recoveryFilePath);
                    lock (_diagnosticsLock) _recoveryLineCount = 0;
                }
            }

            // 回放成功：清除退避标记
            _lastReplayFailureAt = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生产快照恢复文件回放失败，{Delay}s 后重试", (int)ReplayRetryDelay.TotalSeconds);
            _lastReplayFailureAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    private RecoveryFileStamp CaptureRecoveryFileStamp()
    {
        var info = new FileInfo(_recoveryFilePath);
        using var stream = File.OpenRead(_recoveryFilePath);
        var buffer = new byte[Math.Min(4096, (int)Math.Max(1, info.Length))];
        var read = stream.Read(buffer, 0, buffer.Length);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buffer.AsSpan(0, read)));
        return new RecoveryFileStamp(info.Length, info.LastWriteTimeUtc, hash);
    }

    private readonly record struct RecoveryFileStamp(long Length, DateTime LastWriteUtc, string Hash);

    /// <summary>原子重写恢复文件（临时文件 + 替换），避免断电/崩溃留下半写文件。</summary>
    private void WriteRecoveryFileAtomic(List<string> lines) => WriteRecoveryFileAtomicTo(_recoveryFilePath, lines);

    private static void WriteRecoveryFileAtomicTo(string path, List<string> lines)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 幂等批量插入：按 EventId 去重（已存在则跳过），同 EventId 不会重复落库。
    /// 旧数据 EventId 为 null 的不参与去重（数量有限，容忍重复；不阻塞回放）。
    /// </summary>
    private void InsertBatchIdempotent(List<ProductionLog> batch)
    {
        var eventIds = batch
            .Where(log => log.EventId.HasValue)
            .Select(log => log.EventId!.Value)
            .ToList();
        var existing = QueryExistingEventIds(eventIds);

        var toInsert = batch
            .GroupBy(log => log.EventId)
            .SelectMany(group => group.Key.HasValue && existing?.Contains(group.Key.Value) == true
                ? []
                : group.Take(1))
            .ToList();
        if (toInsert.Count == 0) return;

        using var insertContext = _db.CreateProductionLogContext();
        using var transaction = insertContext.Database.BeginTransaction();
        insertContext.ProductionLogs.AddRange(toInsert);
        insertContext.SaveChanges();
        transaction.Commit();
    }

    /// <summary>查询批次中已在库的 EventId（空批次返回 null）。</summary>
    private HashSet<Guid>? QueryExistingEventIds(List<Guid> eventIds)
    {
        if (eventIds.Count == 0) return null;
        using var context = _db.CreateProductionLogContext();
        return context.ProductionLogs
            .Where(log => eventIds.Contains(log.EventId!.Value))
            .Select(log => log.EventId!.Value)
            .ToHashSet();
    }

    private void UpdateQueuePeak(int pending)
    {
        var observed = Interlocked.Read(ref _queuePeak);
        while (pending > observed)
        {
            var previous = Interlocked.CompareExchange(ref _queuePeak, pending, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 0;
        var index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    internal int PendingChannelCountForTest => _channel.Reader.Count;

    /// <summary>测试入口：同步触发一次恢复文件回放（生产路径由 FlushLoop 自动调用）。</summary>
    internal Task ReplayRecoveryForTestAsync(CancellationToken ct = default)
        => ReplayRecoveryAsync(ct);

    /// <summary>测试入口：直接触发恢复文件追加（验证大小上限；生产路径由通道满/批写失败触发）。</summary>
    internal void PersistRecoveryLogsForTest(params ProductionLog[] logs) => PersistRecoveryLogs(logs);

    /// <summary>测试入口：上次回放失败时刻（验证失败退避生效）。</summary>
    internal DateTime? LastReplayFailureAtForTest => _lastReplayFailureAt;

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _cts.Cancel(); _flushTask.Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) { _logger.LogWarning(ex, "释放生产写入器超时"); }
        _cts.Dispose();
        _replayGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { _cts.Cancel(); await _flushTask.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { _logger.LogWarning("异步释放生产写入器超时"); }
        catch (OperationCanceledException) { }
        finally { _cts.Dispose(); _replayGate.Dispose(); }
        GC.SuppressFinalize(this);
    }
}

public sealed record ProductionWriterDiagnosticsSnapshot
{
    public int PendingCount { get; init; }
    public long QueuePeakCount { get; init; }
    public long OverflowCount { get; init; }
    public bool RecoveryFileExists { get; init; }
    public long RecoveryFileBytes { get; init; }
    public long RecoveryFileLines { get; init; }
    public DateTime? LastFlushAt { get; init; }
    public int FlushFailureCount { get; init; }
    public int TotalFlushedCount { get; init; }
    public long FlushP95Milliseconds { get; init; }
    public long FlushP99Milliseconds { get; init; }
}
