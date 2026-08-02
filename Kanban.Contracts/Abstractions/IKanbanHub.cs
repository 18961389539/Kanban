using Kanban.Contracts.Dtos;

namespace Kanban.Contracts.Abstractions;

/// <summary>
/// SignalR 强类型 Hub 的客户端回调契约（服务端 → 客户端推送方向）。
/// MainAPP 的 HubConnection 以泛型 <see cref="HubConnectionExtensions"/> 强类型订阅这些方法。
/// </summary>
public interface IKanbanHubClient
{
    /// <summary>推送设备实时快照（约 200~500ms 一次，全量快照）</summary>
    Task OnSnapshot(DeviceSnapshotDto snapshot);

    /// <summary>推送报警边沿事件</summary>
    Task OnAlarmEvent(AlarmEventDto alarmEvent);

    /// <summary>推送状态转换边沿事件</summary>
    Task OnStatusEvent(StatusEventDto statusEvent);
}

/// <summary>
/// SignalR Hub 方法契约（客户端 → 服务端调用方向）。
/// 仅作为方法名与签名约定（配合 nameof 使用），由 Collector 的 KanbanHub 实现。
/// 注意：SignalR 不支持 CancellationToken 参数（服务端通过 HubCallerContext.Abort 感知断开），
/// 因此此处方法签名一律不含 CancellationToken。
/// </summary>
public interface IKanbanHubServer
{
    /// <summary>连接后先拉取当前全部设备快照</summary>
    Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync();

    /// <summary>订阅实时快照流（订阅后服务端持续 OnSnapshot）</summary>
    Task SubscribeSnapshotsAsync();

    /// <summary>订阅报警事件流，afterSeq = 客户端已消费最大序号（0 = 从头订阅）</summary>
    Task SubscribeAlarmEventsAsync(long afterSeq);

    /// <summary>订阅状态事件流，afterSeq = 客户端已消费最大序号（0 = 从头订阅）</summary>
    Task SubscribeStatusEventsAsync(long afterSeq);

    /// <summary>历史查询（Unary）</summary>
    Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request);

    /// <summary>同步设备配置（Remote 模式：MainAPP 设备管理页保存时推给 Collector 落盘 devices.json）</summary>
    Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices);

    /// <summary>新增/更新工单（Remote 模式：Collector 落库 work_orders.db，返回带 Id 的落库结果）</summary>
    Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder);

    /// <summary>删除工单（Remote 模式：Collector 落库）</summary>
    Task DeleteWorkOrderAsync(int workOrderId);
}
