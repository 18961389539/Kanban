using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 数据源快照存储。采集线程写入短事务，设备详情页按设备聚合读取。
/// 单表 + DeviceId 字段：聚合是查询维度，不做每设备物理分表（设计稿 §5）。
/// </summary>
public sealed class DataSourceSnapshotStore(
    DatabaseProvider databaseProvider,
    ILogger<DataSourceSnapshotStore>? logger = null) : IDataSourceSnapshotStore
{
    private readonly DatabaseProvider _databaseProvider = databaseProvider;
    private readonly ILogger _logger = logger ?? NullLogger<DataSourceSnapshotStore>.Instance;

    public void Append(IEnumerable<DataSourceSnapshotRecord> snapshots)
    {
        var records = snapshots.ToList();
        if (records.Count == 0) return;
        try
        {
            using var context = _databaseProvider.CreateDataSourceSnapshotContext();
            context.DataSourceSnapshots.AddRange(records);
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入数据源快照失败，数量={Count}", records.Count);
        }
    }

    public List<DataSourceSnapshotRecord> Query(string deviceId, DateTime from, DateTime to)
    {
        using var context = _databaseProvider.CreateDataSourceSnapshotContext();
        return context.DataSourceSnapshots.AsNoTracking()
            .Where(record => record.DeviceId == deviceId
                && record.Timestamp >= from
                && record.Timestamp <= to)
            .OrderBy(record => record.Timestamp)
            .ToList();
    }
}