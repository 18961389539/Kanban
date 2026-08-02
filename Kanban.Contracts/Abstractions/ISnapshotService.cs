using Kanban.Contracts.Dtos;

namespace Kanban.Contracts.Abstractions;

/// <summary>
/// 实时快照服务：向展示端推送设备实时快照流。
/// 契约层定义（C#），Collector 以 gRPC Server-streaming 实现，MainAPP 以客户端代理消费。
/// </summary>
public interface ISnapshotService
{
    /// <summary>
    /// 订阅实时快照流。服务端按采集节奏（约 200~500ms）推送全量快照。
    /// </summary>
    IAsyncEnumerable<DeviceSnapshotDto> WatchSnapshots(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前最新快照（客户端连接后先拉一次，再订阅增量）。
    /// </summary>
    Task<DeviceSnapshotDto> GetCurrentSnapshotAsync(CancellationToken cancellationToken = default);
}
