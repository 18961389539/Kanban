using MainAPP.Entities;

namespace MainAPP.Services;

/// <summary>
/// 缺陷历史快照读取能力。采集侧写入走 <see cref="DefectHistoryStore.Append"/>，
/// 展示/复盘侧只依赖此接口查询，便于 Remote 模式切换为 SignalR 实现。
/// </summary>
public interface IDefectHistoryReader
{
    /// <summary>按时间范围查询单设备缺陷快照（按 Timestamp 升序）。</summary>
    List<DefectSnapshotRecord> Query(DateTime from, DateTime to, string deviceId);
}
