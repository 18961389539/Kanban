using Kanban.Contracts.Dtos;

namespace Kanban.Client;

/// <summary>
/// 客户端监控域接口（展示/查询/订阅）：由 <see cref="KanbanDataClient"/> 实现。
/// 与契约 <c>IKanbanHubServer</c>（监控域）一一对应，按能力域拆分管理接口
/// <see cref="IKanbanAdminClient"/>——调用方按需依赖窄接口，新功能按域落位。
/// </summary>
public interface IKanbanMonitoringClient
{
    Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync(CancellationToken ct = default);
    Task SubscribeSnapshotsAsync(CancellationToken ct = default);
    Task SubscribeAlarmEventsAsync(long afterSeq, CancellationToken ct = default);
    Task SubscribeStatusEventsAsync(long afterSeq, CancellationToken ct = default);
    Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync(CancellationToken ct = default);
    Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId, CancellationToken ct = default);
    Task<ShiftProgressDto> GetShiftProgressAsync(CancellationToken ct = default);
    Task SubscribeMetaAsync(CancellationToken ct = default);
    Task<string> GetServerVersionAsync(CancellationToken ct = default);
}

/// <summary>
/// 客户端管理域接口（Collector 单写者的管理写入口）：由 <see cref="KanbanDataClient"/> 实现。
/// 与契约 <c>IKanbanAdminServer</c> 对应。
/// </summary>
public interface IKanbanAdminClient
{
    Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices, CancellationToken ct = default);
    Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder, CancellationToken ct = default);
    Task DeleteWorkOrderAsync(int workOrderId, CancellationToken ct = default);
    Task SaveCollectorSettingsAsync(CollectorSettingsDto settings, CancellationToken ct = default);
}
