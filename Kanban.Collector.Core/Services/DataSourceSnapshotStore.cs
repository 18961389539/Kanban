using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 数据源快照存储。采集线程只写入有界队列，后台任务批量落库，设备详情页按设备聚合读取。
/// 单表 + DeviceId 字段：聚合是查询维度，不做每设备物理分表（设计稿 §5）。
/// </summary>
public sealed class DataSourceSnapshotStore : IDataSourceSnapshotStore, IDisposable, IAsyncDisposable
{
    private const int FlushIntervalMs = 5000;
    private const int BatchSize = 200;
    private const int MaxQueueLength = 10000;
    private const long MaxRecoveryFileBytes = 200 * 1024 * 1024;

    private readonly DatabaseProvider _databaseProvider;
    private readonly ILogger _logger;
    private readonly string _recoveryFilePath;
    private readonly Channel<DataSourceSnapshotRecord> _channel = Channel.CreateBounded<DataSourceSnapshotRecord>(
        new BoundedChannelOptions(MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly object _recoveryLock = new();
    private readonly object _diagnosticsLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private readonly Queue<long> _flushDurations = new();
    private DateTime? _lastFlushAt;
    private int _flushFailureCount;
    private int _totalFlushedCount;
    private long _recoveryLineCount;
    private long _queuePeak;
    private long _overflowCount;

    public DataSourceSnapshotStore(
        DatabaseProvider databaseProvider,
        ILogger<DataSourceSnapshotStore>? logger = null)
    {
        _databaseProvider = databaseProvider;
        _logger = logger ?? NullLogger<DataSourceSnapshotStore>.Instance;
        _recoveryFilePath = databaseProvider.AppSettings.GetFilePath("datasource_snapshots.recovery.jsonl");
        _recoveryLineCount = CountRecoveryLines();
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 采集线程快速入队。队列满时只把未入队部分转存恢复文件，避免历史写入反向阻塞 PLC 轮询。
    /// </summary>
    public void Append(IEnumerable<DataSourceSnapshotRecord> snapshots)
    {
        var records = snapshots.ToList();
        if (records.Count == 0) return;

        var overflow = new List<DataSourceSnapshotRecord>();
        foreach (var record in records)
        {
            if (!_channel.Writer.TryWrite(record))
                overflow.Add(record);
        }

        var pending = _channel.Reader.Count;
        UpdatePeak(pending);
        if (overflow.Count > 0)
        {
            Interlocked.Add(ref _overflowCount, overflow.Count);
            PersistRecoveryRecords(overflow);
            _logger.LogWarning("数据源快照后台队列已满，{Count} 条已转存恢复文件", overflow.Count);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_channel.Reader.Count > 0)
                await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
            await ReplayRecoveryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public DataSourceSnapshotWriterDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        lock (_diagnosticsLock)
        {
            var recoveryBytes = GetFileLength(_recoveryFilePath);
            return new DataSourceSnapshotWriterDiagnosticsSnapshot
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
                await FlushWithGateAsync(cancellationToken).ConfigureAwait(false);
                await ReplayWithGateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await FlushWithGateAsync(cancellationToken).ConfigureAwait(false);
                await ReplayWithGateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "数据源快照后台写入异常");
                try { await Task.Delay(FlushIntervalMs, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        try
        {
            await FlushWithGateAsync(CancellationToken.None).ConfigureAwait(false);
            await ReplayWithGateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "数据源快照停机排空失败");
        }
    }

    private async Task FlushWithGateAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await FlushPendingAsync(cancellationToken).ConfigureAwait(false); }
        finally { _flushGate.Release(); }
    }

    private async Task ReplayWithGateAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ReplayRecoveryAsync(cancellationToken).ConfigureAwait(false); }
        finally { _flushGate.Release(); }
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        var batch = new List<DataSourceSnapshotRecord>(BatchSize);
        if (!_channel.Reader.TryRead(out var first)) return;
        batch.Add(first);

        using var fillTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        fillTimeout.CancelAfter(10);
        try
        {
            while (batch.Count < BatchSize)
            {
                if (_channel.Reader.TryRead(out var record)) { batch.Add(record); continue; }
                if (!await _channel.Reader.WaitToReadAsync(fillTimeout.Token).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) { }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var context = _databaseProvider.CreateDataSourceSnapshotContext();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            context.DataSourceSnapshots.AddRange(batch);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            RecordFlushSuccess(batch.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            lock (_diagnosticsLock) _flushFailureCount++;
            PersistRecoveryRecords(batch);
            _logger.LogError(ex, "批量写入数据源快照失败，已转存 {Count} 条", batch.Count);
        }
    }

    private async Task ReplayRecoveryAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] lines;
            lock (_recoveryLock)
            {
                if (!File.Exists(_recoveryFilePath)) return;
                if (GetFileLength(_recoveryFilePath) > MaxRecoveryFileBytes)
                {
                    _logger.LogError("数据源快照恢复文件超过上限 {Max:N0} 字节，暂停回放：{Path}", MaxRecoveryFileBytes, _recoveryFilePath);
                    return;
                }
                lines = File.ReadAllLines(_recoveryFilePath);
            }

            if (lines.Length == 0)
            {
                lock (_recoveryLock)
                {
                    if (File.Exists(_recoveryFilePath)) File.Delete(_recoveryFilePath);
                    _recoveryLineCount = 0;
                }
                return;
            }

            var scannedCount = Math.Min(BatchSize, lines.Length);
            var records = new List<DataSourceSnapshotRecord>(scannedCount);
            var badLines = new List<string>();
            for (var index = 0; index < scannedCount; index++)
            {
                var line = lines[index];
                try
                {
                    var record = JsonSerializer.Deserialize<DataSourceSnapshotRecord>(line);
                    if (record is null) { badLines.Add(line); continue; }
                    records.Add(record);
                }
                catch (JsonException ex)
                {
                    badLines.Add(line);
                    _logger.LogError(ex, "数据源快照恢复文件存在损坏记录，已转存 .bad 文件");
                }
            }

            // 一个批次一个事务；只有事务成功后才移除恢复文件前缀，
            // 因而 DB 在中途恢复/再次失败时不会重复插入已完成批次。
            if (records.Count > 0)
                await InsertBatchAsync(records, cancellationToken).ConfigureAwait(false);

            lock (_recoveryLock)
            {
                if (!File.Exists(_recoveryFilePath)) return;
                var currentLines = File.ReadAllLines(_recoveryFilePath);
                if (currentLines.Length < scannedCount
                    || !currentLines.Take(scannedCount).SequenceEqual(lines.Take(scannedCount)))
                {
                    _logger.LogWarning("数据源快照恢复文件在回放期间被替换，保留现有文件避免丢失");
                    return;
                }

                if (badLines.Count > 0)
                {
                    File.AppendAllLines(_recoveryFilePath + ".bad", badLines, Encoding.UTF8);
                    _logger.LogError("数据源快照恢复文件有 {Count} 条损坏记录，已转存 .bad 文件", badLines.Count);
                }

                var remaining = currentLines.Skip(scannedCount).ToList();
                if (remaining.Count == 0)
                {
                    File.Delete(_recoveryFilePath);
                    _recoveryLineCount = 0;
                }
                else
                {
                    WriteRecoveryFileAtomic(remaining);
                    _recoveryLineCount = remaining.Count;
                }
            }

            if (scannedCount < BatchSize) return;
        }
    }

    private async Task InsertBatchAsync(IReadOnlyCollection<DataSourceSnapshotRecord> batch, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var context = _databaseProvider.CreateDataSourceSnapshotContext();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            context.DataSourceSnapshots.AddRange(batch);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            RecordFlushSuccess(batch.Count, stopwatch.ElapsedMilliseconds);
        }
        catch
        {
            lock (_diagnosticsLock) _flushFailureCount++;
            throw;
        }
    }

    private void PersistRecoveryRecords(IEnumerable<DataSourceSnapshotRecord> records)
    {
        var lines = records.Select(record => JsonSerializer.Serialize(record)).ToArray();
        if (lines.Length == 0) return;
        try
        {
            lock (_recoveryLock)
            {
                var existingBytes = GetFileLength(_recoveryFilePath);
                var addedBytes = lines.Sum(line => Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
                if (existingBytes + addedBytes > MaxRecoveryFileBytes)
                {
                    _logger.LogError("数据源快照恢复文件超过上限 {Max:N0} 字节，停止追加：{Path}", MaxRecoveryFileBytes, _recoveryFilePath);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_recoveryFilePath)!);
                File.AppendAllLines(_recoveryFilePath, lines, Encoding.UTF8);
                lock (_diagnosticsLock) _recoveryLineCount += lines.Length;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据源快照恢复文件写入失败");
        }
    }

    private static void WriteRecoveryFileAtomic(List<string> lines, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllLines(temporaryPath, lines, Encoding.UTF8);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void WriteRecoveryFileAtomic(List<string> lines)
        => WriteRecoveryFileAtomic(lines, _recoveryFilePath);

    private void RecordFlushSuccess(int count, long elapsedMilliseconds)
    {
        lock (_diagnosticsLock)
        {
            _lastFlushAt = DateTime.Now;
            _totalFlushedCount += count;
            _flushDurations.Enqueue(elapsedMilliseconds);
            while (_flushDurations.Count > 1024) _flushDurations.Dequeue();
        }
    }

    private void UpdatePeak(int pending)
    {
        var observed = Interlocked.Read(ref _queuePeak);
        while (pending > observed)
        {
            var previous = Interlocked.CompareExchange(ref _queuePeak, pending, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    private long CountRecoveryLines()
    {
        try { return File.Exists(_recoveryFilePath) ? File.ReadLines(_recoveryFilePath).LongCount() : 0; }
        catch { return 0; }
    }

    private static long GetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 0;
        var index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    public List<DataSourceSnapshotRecord> Query(string deviceId, DateTime from, DateTime to)
    {
        FlushBeforeQuery();
        using var context = _databaseProvider.CreateDataSourceSnapshotContext();
        return context.DataSourceSnapshots.AsNoTracking()
            .Where(record => record.DeviceId == deviceId
                && record.Timestamp >= from
                && record.Timestamp <= to)
            .OrderBy(record => record.Timestamp)
            .ToList();
    }

    public List<DataSourceSnapshotRecord> Query(
        string deviceId,
        string sourceId,
        string valueId,
        DateTime from,
        DateTime to)
    {
        FlushBeforeQuery();
        using var context = _databaseProvider.CreateDataSourceSnapshotContext();
        return context.DataSourceSnapshots.AsNoTracking()
            .Where(record => record.DeviceId == deviceId
                && record.Timestamp >= from
                && record.Timestamp <= to
                && ((record.SourceId == sourceId && record.ValueId == valueId)
                    // 兼容修复前 SourceId 错写为 ValueId 的历史记录。
                    || (record.ValueId == string.Empty && record.SourceId == valueId)))
            .OrderBy(record => record.Timestamp)
            .ThenBy(record => record.Id)
            .ToList();
    }

    private void FlushBeforeQuery()
    {
        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 查询仍返回已提交数据；未提交记录保留在恢复文件/诊断中，避免读路径因暂时不可写而失去可用性。
            _logger.LogWarning(ex, "查询前排空数据源快照写入队列失败，将返回已提交数据");
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { _flushTask.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { _logger.LogWarning(ex, "释放数据源快照写入器超时"); }
        _cts.Dispose();
        _flushGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _flushTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { _logger.LogWarning("异步释放数据源快照写入器超时"); }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
            _flushGate.Dispose();
        }
        GC.SuppressFinalize(this);
    }

}

public sealed record DataSourceSnapshotWriterDiagnosticsSnapshot
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