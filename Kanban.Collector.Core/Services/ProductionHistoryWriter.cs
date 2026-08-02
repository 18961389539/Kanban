using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Kanban.Core.Services;

public sealed class ProductionHistoryWriter : IProductionHistoryWriter, IDisposable, IAsyncDisposable
{
    private const int FlushIntervalMs = 5000;
    private const int BatchSize = 200;
    private const int MaxQueueLength = 10000;
    private readonly DatabaseProvider _db;
    private readonly ILogger<ProductionHistoryWriter> _logger;
    private readonly string _recoveryFilePath;
    private readonly Channel<ProductionLog> _channel = Channel.CreateBounded<ProductionLog>(
        new BoundedChannelOptions(MaxQueueLength) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly object _recoveryLock = new();
    private readonly object _diagnosticsLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private DateTime? _lastFlushAt;
    private int _flushFailureCount;
    private int _totalFlushedCount;

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
                RecoveryFileExists = recoveryBytes > 0,
                RecoveryFileBytes = recoveryBytes,
                LastFlushAt = _lastFlushAt,
                FlushFailureCount = _flushFailureCount,
                TotalFlushedCount = _totalFlushedCount,
            };
        }
    }

    public void LogProduction(ProductionLog log)
    {
        if (!_channel.Writer.TryWrite(log))
        {
            PersistRecoveryLogs([log]);
            _logger.LogWarning("生产快照通道已满，已转存本地恢复文件");
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

        while (reader.Count > 0)
        {
            var before = reader.Count;
            await FlushPendingAsync(CancellationToken.None);
            if (reader.Count >= before) break;
        }
    }

    private async Task FlushPendingAsync(CancellationToken ct)
    {
        var batch = new List<ProductionLog>(BatchSize);
        if (!_channel.Reader.TryRead(out var first)) return;
        batch.Add(first);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(10);
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
                File.AppendAllLines(_recoveryFilePath, lines);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "生产快照恢复文件写入失败"); }
    }

    private Task ReplayRecoveryAsync(CancellationToken ct)
    {
        if (!File.Exists(_recoveryFilePath)) return Task.CompletedTask;
        try
        {
            lock (_recoveryLock)
            {
                var badLines = new List<string>();
                var logs = File.ReadAllLines(_recoveryFilePath).Select(line =>
                {
                    try { return JsonSerializer.Deserialize<ProductionLog>(line); }
                    catch (JsonException ex)
                    {
                        badLines.Add(line);
                        _logger.LogError(ex, "恢复文件存在损坏记录，已转存 .bad 文件");
                        return null;
                    }
                }).Where(log => log is not null).Cast<ProductionLog>().ToList();
                ct.ThrowIfCancellationRequested();
                if (logs.Count > 0)
                {
                    using var context = _db.CreateProductionLogContext();
                    context.ProductionLogs.AddRange(logs);
                    context.SaveChanges();
                }

                if (badLines.Count > 0)
                {
                    File.AppendAllLines(_recoveryFilePath + ".bad", badLines);
                    File.Delete(_recoveryFilePath);
                }
                else
                {
                    File.Delete(_recoveryFilePath);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "生产快照恢复文件回放失败"); }
        return Task.CompletedTask;
    }

    internal int PendingChannelCountForTest => _channel.Reader.Count;

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _cts.Cancel(); _flushTask.Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) { _logger.LogWarning(ex, "释放生产写入器超时"); }
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { _cts.Cancel(); await _flushTask.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { _logger.LogWarning("异步释放生产写入器超时"); }
        catch (OperationCanceledException) { }
        finally { _cts.Dispose(); }
        GC.SuppressFinalize(this);
    }
}

public sealed record ProductionWriterDiagnosticsSnapshot
{
    public int PendingCount { get; init; }
    public bool RecoveryFileExists { get; init; }
    public long RecoveryFileBytes { get; init; }
    public DateTime? LastFlushAt { get; init; }
    public int FlushFailureCount { get; init; }
    public int TotalFlushedCount { get; init; }
}
