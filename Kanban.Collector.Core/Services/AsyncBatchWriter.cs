using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 边沿事件异步批量写入器（报警事件/状态转换共用骨架，设计审查修复 2026-09-16）。
/// 此前报警/状态边沿在采集热路径上同步 SaveChanges：SQLite 写锁竞争/磁盘抖动直接拖停采集循环，
/// 且与生产快照（ProductionHistoryWriter 异步批量）策略不一致。
///
/// 语义：
/// - 采集线程只做非阻塞入队；后台单读者按批落库（批量事务由插入回调内的 EF SaveChanges 承担）；
/// - 队列满（DropWrite 回调）与批写失败一律转存恢复文件（<see cref="SpilloverRecoveryFile{T}"/>），
///   后台循环自动回放——不静默丢失；
/// - <see cref="TryAccept"/> 返回 true = 已接收（入队或转存，保证最终落库）；false = 写入器已释放；
/// - 需要"写完立即可读"的调用方（如空窗补离线前的 GetLatestStatusBefore）应先 <see cref="Flush()"/>；
/// - 停机时排空通道并回放一次恢复文件。
/// </summary>
internal sealed class AsyncBatchWriter<T> : IDisposable
{
    private readonly string _name;
    private readonly ILogger _logger;
    private readonly int _batchSize;
    private readonly int _flushIntervalMs;
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _insertBatchAsync;
    private readonly SpilloverRecoveryFile<T> _spillover;
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    /// <summary>排空互斥闸门：<see cref="Flush"/>（任意线程）与后台循环串行——通道声明 SingleReader
    /// 走无锁快路径，并发 TryRead 会静默丢项/损坏内部索引（P0-4 同类教训，2026-09-02）。</summary>
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    /// <summary>每批落库成功释放一个令牌，供测试确定性等待（事件驱动，避免固定预算轮询偶发超时）。</summary>
    private readonly SemaphoreSlim _flushSignal = new(0);
    private int _flushedCount;
    private long _overflowCount;
    private int _disposed;

    public AsyncBatchWriter(
        string name,
        string recoveryFilePath,
        long maxRecoveryFileBytes,
        Func<IReadOnlyList<T>, CancellationToken, Task> insertBatchAsync,
        ILogger logger,
        int capacity = 2048,
        int batchSize = 200,
        int flushIntervalMs = 1000)
    {
        _name = name;
        _logger = logger;
        _batchSize = batchSize;
        _flushIntervalMs = flushIntervalMs;
        _insertBatchAsync = insertBatchAsync;
        _spillover = new SpilloverRecoveryFile<T>(recoveryFilePath, maxRecoveryFileBytes, logger, name);
        _channel = Channel.CreateBounded<T>(
            new BoundedChannelOptions(Math.Max(1, capacity))
            {
                // DropWrite：采集线程永不因队列满阻塞；被丢条目经回调转存恢复文件（不丢数据）
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            },
            OnItemDropped);
        _flushTask = BackgroundTaskRunner.StartLoop(FlushLoopAsync, _cts.Token, _logger, $"AsyncBatchWriter<{name}>");
    }

    /// <summary>队列溢出转存恢复文件的累计条数（诊断观测用）。</summary>
    public long OverflowCount => Interlocked.Read(ref _overflowCount);

    /// <summary>已落库条数。</summary>
    public int FlushedCount => Volatile.Read(ref _flushedCount);

    /// <summary>恢复文件路径（测试断言用）。</summary>
    public string RecoveryFilePath => _spillover.Path;

    /// <summary>接收一条记录：true=已接收（入队或转存恢复文件，保证最终落库）；false=写入器已释放。</summary>
    public bool TryAccept(T item)
    {
        if (Volatile.Read(ref _disposed) == 1) return false;
        if (!_channel.Writer.TryWrite(item))
        {
            // 写端已关闭（与 Dispose 并发的极窄窗口）：转存恢复文件，不丢
            _spillover.Append([item]);
        }
        return true;
    }

    private void OnItemDropped(T dropped)
    {
        Interlocked.Increment(ref _overflowCount);
        _spillover.Append([dropped]);
    }

    /// <summary>
    /// 同步排空通道并落库（供"写完立即可读"路径与测试使用；生产热路径不调用）。
    /// 与后台循环经 <see cref="_flushGate"/> 互斥：返回时保证"已 TryRead 的数据均已落库"。
    /// </summary>
    public void Flush()
    {
        _flushGate.Wait();
        try
        {
            while (_channel.Reader.Count > 0)
                FlushPendingAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { _flushGate.Release(); }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hasData = await reader.WaitToReadAsync(ct).AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(_flushIntervalMs), ct);
                if (!hasData) break;
                await FlushBatchAsync(ct);
                await ReplayRecoveryAsync(ct);
            }
            catch (TimeoutException)
            {
                if (reader.Count > 0) await FlushBatchAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name}后台写入异常", _name);
                try { await Task.Delay(_flushIntervalMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // 停机排空：通道剩余全部落库后再回放恢复文件（失败转存的条目不遗留到下次启动）
        while (reader.Count > 0)
        {
            var before = reader.Count;
            await FlushBatchAsync(CancellationToken.None);
            if (reader.Count >= before) break;
        }
        try
        {
            await ReplayRecoveryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停机时回放{Name}恢复文件失败（留待下次启动回放）", _name);
        }
    }

    private Task ReplayRecoveryAsync(CancellationToken ct)
        => _spillover.ReplayAsync(batch => _insertBatchAsync(batch, CancellationToken.None), _batchSize, ct);

    private async Task FlushBatchAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct);
        try { await FlushPendingAsync(ct); }
        finally { _flushGate.Release(); }
    }

    private async Task FlushPendingAsync(CancellationToken ct)
    {
        var batch = new List<T>(_batchSize);
        if (!_channel.Reader.TryRead(out var first)) return;
        batch.Add(first);
        while (batch.Count < _batchSize && _channel.Reader.TryRead(out var next))
            batch.Add(next);
        try
        {
            await _insertBatchAsync(batch, ct);
            Interlocked.Add(ref _flushedCount, batch.Count);
            _flushSignal.Release(); // 唤醒可能的 WaitFlushedAsync 等待者
        }
        catch (Exception ex)
        {
            // 失败批次不丢：转存恢复文件由后台循环回放
            _spillover.Append(batch);
            _logger.LogWarning(ex, "{Name}批量写入失败，{Count} 条已转存恢复文件待回放", _name, batch.Count);
        }
    }

    /// <summary>测试辅助：等待已落库条数达到 <paramref name="minCount"/>（刷盘信号驱动）。</summary>
    internal async Task<bool> WaitFlushedAsync(int minCount, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (FlushedCount < minCount)
        {
            var remaining = deadline - DateTime.Now;
            if (remaining <= TimeSpan.Zero) return false;
            await _flushSignal.WaitAsync(remaining);
        }
        return true;
    }

    /// <summary>测试入口：同步触发一次恢复文件回放（生产路径由 FlushLoop 自动调用）。</summary>
    internal Task ReplayRecoveryForTestAsync(CancellationToken ct = default)
        => _spillover.ReplayAsync(batch => _insertBatchAsync(batch, CancellationToken.None), _batchSize, ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { _flushTask.Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { _logger.LogWarning(ex, "释放{Name}写入器超时", _name); }
        _cts.Dispose();
        _flushGate.Dispose();
        _flushSignal.Dispose();
        GC.SuppressFinalize(this);
    }
}
