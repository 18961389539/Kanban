using System.Threading.Channels;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 缺陷历史快照存储。写入走 Channel + 后台批量落库（采集线程零阻塞，P1-9 性能修复 2026-09-01），
/// 复盘页按时间范围读取。
/// </summary>
public sealed class DefectHistoryStore : IDefectHistoryReader, IDisposable
{
    /// <summary>通道容量：正常速率（每 ~5s 一批、每批数十行）远不会触顶；极端溢出丢最旧保最新（快照型数据）。</summary>
    private const int ChannelCapacity = 8192;
    /// <summary>单批最大落库条数。</summary>
    private const int MaxBatchSize = 512;
    private readonly DatabaseProvider _databaseProvider;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly Channel<DefectSnapshotRecord> _channel = Channel.CreateBounded<DefectSnapshotRecord>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            // DropOldest：采集线程永不因通道满而阻塞；溢出丢弃最旧快照（最新快照代表当前状态）
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    /// <summary>
    /// 排空互斥闸门：所有 <see cref="FlushPendingAsync"/> 调用（后台循环 / <see cref="Flush"/> /
    /// 停机排空）必须经此串行。审查修复 2026-09-02（P0-4）：通道声明 SingleReader=true 走无锁
    /// 快路径，原实现 Flush() 由任意调用线程与后台循环并发 TryRead，会静默丢项/损坏内部索引。
    /// </summary>
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private long _overflowCount;
    private bool _disposed;

    public DefectHistoryStore(
        DatabaseProvider databaseProvider,
        ILogger<DefectHistoryStore>? logger = null)
    {
        _databaseProvider = databaseProvider;
        _logger = logger ?? NullLogger<DefectHistoryStore>.Instance;
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 缺陷快照入队（非阻塞）。由后台 flush 循环批量落库；通道溢出（极端场景）丢弃最旧记录并告警。
    /// 原实现在此同步 new DbContext + SaveChanges，与后台 flush 抢 SQLite 写锁，且随 defect_history.db
    /// 膨胀（650MB/309 万行）把采集线程拖得越来越慢。
    /// 审查修复 2026-09-02（P1-1）：Dispose 后拒绝入队并告警——原实现 Dispose 只 Cancel 后台循环，
    /// TryWrite 仍返回 true，数据进通道无人消费、静默丢失且调用方无感知。
    /// </summary>
    public void Append(IEnumerable<DefectSnapshotRecord> snapshots)
    {
        if (Volatile.Read(ref _disposed))
        {
            var count = snapshots.TryGetNonEnumeratedCount(out var known) ? known : snapshots.Count();
            _logger.LogWarning("缺陷快照存储已释放，丢弃 {Count} 条快照", count);
            return;
        }

        foreach (var snapshot in snapshots)
        {
            if (!_channel.Writer.TryWrite(snapshot))
            {
                var n = Interlocked.Increment(ref _overflowCount);
                // P1-2 修复 2026-09-02：溢出时逐条打日志会形成日志风暴（磁盘满/DB 锁死时每秒数百条，
                // 反而拖垮磁盘与 I/O）。采样：仅记录首次与每 1000 条的累计水位。
                if (n == 1 || n % 1000 == 0)
                    _logger.LogWarning("缺陷快照通道已满，丢弃最旧记录（累计溢出 {Count} 条）", n);
            }
        }
    }

    /// <summary>
    /// 同步排空通道并落库（供测试断言与停机路径使用；生产热路径不调用）。
    /// 与后台循环经 <see cref="_flushGate"/> 互斥：返回时保证"已 TryRead 的数据均已 SaveChanges 落库"，
    /// 而不仅是"通道已空"——修复测试断言早于后台批次落库的间歇性失败（P0-4）。
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
                    .WaitAsync(TimeSpan.FromSeconds(1), ct);
                if (!hasData) break;
                await FlushBatchAsync(ct);
            }
            catch (TimeoutException)
            {
                if (reader.Count > 0) await FlushBatchAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "缺陷历史后台写入异常");
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // 停机排空：通道剩余全部落库（与原有"调用方退出前数据不丢"语义一致）。
        // 此刻后台读者已退出，但仍可能与外部 Flush() 并发，故同样经闸门互斥。
        while (reader.Count > 0)
        {
            var before = reader.Count;
            await FlushBatchAsync(CancellationToken.None);
            if (reader.Count >= before) break;
        }
    }

    /// <summary>经 <see cref="_flushGate"/> 互斥地执行一次批量落库（P0-4：串行化所有读者）。</summary>
    private async Task FlushBatchAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct);
        try { await FlushPendingAsync(ct); }
        finally { _flushGate.Release(); }
    }

    private async Task FlushPendingAsync(CancellationToken ct)
    {
        var batch = new List<DefectSnapshotRecord>(MaxBatchSize);
        if (!_channel.Reader.TryRead(out var first)) return;
        batch.Add(first);
        while (batch.Count < MaxBatchSize && _channel.Reader.TryRead(out var next))
            batch.Add(next);
        try
        {
            using var context = _databaseProvider.CreateDefectHistoryContext();
            context.DefectSnapshots.AddRange(batch);
            await context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // 与原有语义一致：失败丢弃本批并记日志（快照型数据可容忍，不做回放避免拖累后台线程）
            _logger.LogWarning(ex, "写入缺陷历史快照失败，数量={Count}", batch.Count);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        // P1-1 修复 2026-09-02：显式完成通道，让 WaitToReadAsync 在 Cancel 之外也能自然结束
        // （返回 false → 退出主循环 → 停机排空剩余批次），避免后台任务续延链滞留。
        _channel.Writer.TryComplete();
        try { _flushTask.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { _logger.LogWarning(ex, "等待缺陷历史后台写入停止超时或失败"); }
        _cts.Dispose();
    }

    public List<DefectSnapshotRecord> Query(DateTime from, DateTime to, string deviceId)
    {
        using var context = _databaseProvider.CreateDefectHistoryContext();
        return context.DefectSnapshots.AsNoTracking()
            .Where(record => record.DeviceId == deviceId
                && record.Timestamp >= from
                && record.Timestamp <= to)
            .OrderBy(record => record.Timestamp)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 用原生窗口函数 SQL（ROW_NUMBER 分组取首/末）替代 EF GroupBy+First 翻译：
    /// EF 的翻译在 SQLite 上生成相关子查询（O(n²) 级，2 天 27 万行实测 6.4s），
    /// 原生窗口函数 + 索引（DeviceId, Timestamp）实测 1.1s。基线 = 窗口前每组最后一条。
    /// </remarks>
    public List<DefectSnapshotRecord> QueryWindowBounds(DateTime from, DateTime to, string deviceId)
    {
        using var context = _databaseProvider.CreateDefectHistoryContext();

        // 列名与 EF 实体映射一致（DefectHistoryDbContext 默认列名）；
        // 非插值原始字符串：{0}/{1}/{2} 是 FromSqlRaw 的参数占位符（string.Format 语义）
        const string baselinesSql = """
            SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp" FROM (
              SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp",
                     ROW_NUMBER() OVER (PARTITION BY "DefectId","ShiftName" ORDER BY "Timestamp" DESC) AS rn
              FROM "DefectSnapshots"
              WHERE "DeviceId" = {0} AND "Timestamp" >= {1} AND "Timestamp" < {2}
            ) WHERE rn = 1
            """;
        const string windowFirstsSql = """
            SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp" FROM (
              SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp",
                     ROW_NUMBER() OVER (PARTITION BY "DefectId","ShiftName" ORDER BY "Timestamp" ASC) AS rn
              FROM "DefectSnapshots"
              WHERE "DeviceId" = {0} AND "Timestamp" >= {1} AND "Timestamp" <= {2}
            ) WHERE rn = 1
            """;
        const string windowLastsSql = """
            SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp" FROM (
              SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp",
                     ROW_NUMBER() OVER (PARTITION BY "DefectId","ShiftName" ORDER BY "Timestamp" DESC) AS rn
              FROM "DefectSnapshots"
              WHERE "DeviceId" = {0} AND "Timestamp" >= {1} AND "Timestamp" <= {2}
            ) WHERE rn = 1
            """;

        var baselines = context.DefectSnapshots
            .FromSqlRaw(baselinesSql, deviceId, from.AddDays(-1), from)
            .AsNoTracking()
            .ToList();
        var windowFirsts = context.DefectSnapshots
            .FromSqlRaw(windowFirstsSql, deviceId, from, to)
            .AsNoTracking()
            .ToList();
        var windowLasts = context.DefectSnapshots
            .FromSqlRaw(windowLastsSql, deviceId, from, to)
            .AsNoTracking()
            .ToList();
        // 顺序无关紧要：调用方按（DefectId, ShiftName）分组后自行差分
        return baselines.Concat(windowFirsts).Concat(windowLasts).ToList();
    }

    /// <summary>
    /// 缺陷快照按小时分组下推：按（缺陷 + 班次 + 小时桶）分组，每组取小时末值；另附窗口前基线。
    /// 缺陷集中度需要逐小时分布（Top 10 缺陷 × 小时），全量拉取（2 天 27 万条）是复盘页耗时根因之一；
    /// 按小时分组后一次 SQL 只返回「缺陷数 × 小时数」行（通常数百行），客户端按小时差分即可。
    /// </summary>
    public List<DefectSnapshotRecord> QueryHourlyBounds(DateTime from, DateTime to, string deviceId)
    {
        using var context = _databaseProvider.CreateDefectHistoryContext();

        // 窗口前基线：每组（缺陷+班次）窗口前最后一条（不含小时桶，用于首小时差分）
        const string baselinesSql = """
            SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp" FROM (
              SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp",
                     ROW_NUMBER() OVER (PARTITION BY "DefectId","ShiftName" ORDER BY "Timestamp" DESC) AS rn
              FROM "DefectSnapshots"
              WHERE "DeviceId" = {0} AND "Timestamp" >= {1} AND "Timestamp" < {2}
            ) WHERE rn = 1
            """;
        // 窗口内每小时末值：按（缺陷+班次+小时）分组，取每组时间戳最大的一条
        const string hourlySql = """
            SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp" FROM (
              SELECT "Id","DeviceId","DeviceName","DefectId","DefectName","Severity","Category","ShiftName","Count","Timestamp",
                     ROW_NUMBER() OVER (
                       PARTITION BY "DefectId","ShiftName",strftime('%Y-%m-%d %H:00:00',"Timestamp")
                       ORDER BY "Timestamp" DESC) AS rn
              FROM "DefectSnapshots"
              WHERE "DeviceId" = {0} AND "Timestamp" >= {1} AND "Timestamp" <= {2}
            ) WHERE rn = 1
            """;

        var baselines = context.DefectSnapshots
            .FromSqlRaw(baselinesSql, deviceId, from.AddDays(-1), from)
            .AsNoTracking()
            .ToList();
        var hourly = context.DefectSnapshots
            .FromSqlRaw(hourlySql, deviceId, from, to)
            .AsNoTracking()
            .ToList();
        // 顺序无关紧要：调用方按（DefectId, ShiftName, 小时）分组后自行差分
        return baselines.Concat(hourly).ToList();
    }

    /// <summary>
    /// 分页查询缺陷快照（SQL 层 Count + OrderByDescending + Skip/Take；异常向调用方抛出）。
    /// 供历史查询页使用——此前全量 ToList 后客户端内存分页。
    /// </summary>
    public (List<DefectSnapshotRecord> Items, int Total) QueryDefectSnapshotsPaged(
        DateTime from, DateTime to, string deviceId, int page, int pageSize)
    {
        using var context = _databaseProvider.CreateDefectHistoryContext();
        var query = context.DefectSnapshots.AsNoTracking()
            .Where(record => record.DeviceId == deviceId
                && record.Timestamp >= from
                && record.Timestamp <= to);
        var total = query.Count();
        var offset = HistoryPagination.Offset(page, pageSize);
        var (_, size) = HistoryPagination.Normalize(page, pageSize);
        var items = query
            .OrderByDescending(record => record.Timestamp)
            // 稳定次级键：同轮采集同设备全部缺陷共享同一 Timestamp，仅按时间排序翻页会重复/漏行（审查修复 2026-08-13）
            .ThenByDescending(record => record.Id)
            .Skip(offset)
            .Take(size)
            .ToList();
        return (items, total);
    }

    public int CleanupOldSnapshots(int retentionDays = 365) =>
        HistoryRetentionCleanup.DeleteBefore(
            _databaseProvider.CreateDefectHistoryContext,
            context => ((DefectHistoryDbContext)context).DefectSnapshots,
            record => record.Timestamp,
            "缺陷历史快照",
            retentionDays,
            _logger);
}
