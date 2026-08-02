namespace Kanban.Contracts.Dtos;

/// <summary>
/// 设备当前工单快照（低频元数据推送用）。
/// 服务端单源计算：Running 优先，无则回退最新 Pending；WorkOrder=null 表示该设备无工单。
/// </summary>
public sealed record DeviceWorkOrderDto
{
    public string DeviceId { get; init; } = "";

    public WorkOrderDto? WorkOrder { get; init; }
}

/// <summary>
/// 低频元数据推送包（约 5s 一次，由 Collector MetaPublisher 广播）：
/// 全部设备的当前工单 + 全局班次进度。客户端订阅即得，无需轮询 Invoke。
/// </summary>
public sealed record MetaStateDto
{
    public IReadOnlyList<DeviceWorkOrderDto> Devices { get; init; } = [];

    public ShiftProgressDto Shift { get; init; } = new();
}
