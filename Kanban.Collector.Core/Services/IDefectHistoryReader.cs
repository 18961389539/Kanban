using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 缺陷历史快照读取能力。采集侧写入走 <see cref="DefectHistoryStore.Append"/>，
/// 展示/复盘侧只依赖此接口查询，便于 Remote 模式切换为 SignalR 实现。
/// </summary>
public interface IDefectHistoryReader
{
    /// <summary>按时间范围查询单设备缺陷快照（按 Timestamp 升序）。</summary>
    List<DefectSnapshotRecord> Query(DateTime from, DateTime to, string deviceId);

    /// <summary>
    /// 窗口边界聚合：按（缺陷 + 班次）分组，每组只返回「窗口前最后一条」与「窗口内首末两条」。
    /// 缺陷快照是累计值且高频（每日数十万条），帕累托增量差分只需边界记录——
    /// SQL 分组下推把物化量从全量（2 天 27 万条）降到组数（几十行），是复盘页耗时根因的读侧修复。
    /// </summary>
    List<DefectSnapshotRecord> QueryWindowBounds(DateTime from, DateTime to, string deviceId);

    /// <summary>
    /// 缺陷快照按小时分组下推：按（缺陷 + 班次 + 小时桶）分组，每组取小时末值 + 窗口前基线。
    /// 供复盘页缺陷集中度使用——按小时差分还原逐小时分布，避免全量拉取高频累计快照。
    /// </summary>
    List<DefectSnapshotRecord> QueryHourlyBounds(DateTime from, DateTime to, string deviceId);
}
