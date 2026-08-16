using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 缺陷历史快照存储。采集线程写入短事务，复盘页按时间范围读取。
/// </summary>
public sealed class DefectHistoryStore(
    DatabaseProvider databaseProvider,
    ILogger<DefectHistoryStore>? logger = null) : IDefectHistoryReader
{
    private readonly DatabaseProvider _databaseProvider = databaseProvider;
    private readonly Microsoft.Extensions.Logging.ILogger _logger =
        logger ?? NullLogger<DefectHistoryStore>.Instance;

    public void Append(IEnumerable<DefectSnapshotRecord> snapshots)
    {
        var records = snapshots.ToList();
        if (records.Count == 0) return;
        try
        {
            using var context = _databaseProvider.CreateDefectHistoryContext();
            context.DefectSnapshots.AddRange(records);
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "写入缺陷历史快照失败，数量={Count}", records.Count);
        }
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
