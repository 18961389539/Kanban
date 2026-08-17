using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 数据源快照（datasource_snapshots.db）读写能力。
/// </summary>
public interface IDataSourceSnapshotStore
{
    /// <summary>批量追加快照（采集落盘节拍调用，短事务群写）。</summary>
    void Append(IEnumerable<DataSourceSnapshotRecord> snapshots);

    /// <summary>按设备+时间范围查询快照（按时间正序）。</summary>
    List<DataSourceSnapshotRecord> Query(string deviceId, DateTime from, DateTime to);
}