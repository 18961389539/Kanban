using Kanban.Core.Data;
using Kanban.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Kanban.Core.Services;

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

    public int CleanupOldSnapshots(int retentionDays = 365) =>
        HistoryRetentionCleanup.DeleteBefore(
            _databaseProvider.CreateDefectHistoryContext,
            context => ((DefectHistoryDbContext)context).DefectSnapshots,
            record => record.Timestamp,
            "缺陷历史快照",
            retentionDays,
            _logger);
}
