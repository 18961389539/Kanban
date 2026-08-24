using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 数据源快照（datasource_snapshots.db）读写能力。
/// </summary>
public interface IDataSourceSnapshotStore
{
    /// <summary>快速追加快照：写入有界后台队列，不等待 SQLite。</summary>
    void Append(IEnumerable<DataSourceSnapshotRecord> snapshots);

    /// <summary>排空内存队列并尝试回放恢复文件。</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>获取后台写入队列、恢复文件和批量提交诊断。</summary>
    DataSourceSnapshotWriterDiagnosticsSnapshot GetDiagnosticsSnapshot();

    /// <summary>按设备+时间范围查询快照（按时间正序）。</summary>
    List<DataSourceSnapshotRecord> Query(string deviceId, DateTime from, DateTime to);

    /// <summary>按设备+数据源+值项+时间范围查询快照（按采样时刻正序）。</summary>
    List<DataSourceSnapshotRecord> Query(
        string deviceId,
        string sourceId,
        string valueId,
        DateTime from,
        DateTime to);
}