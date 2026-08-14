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
/// 设备缺陷计数（低频元数据推送用，服务端按 Count 排序取 TOP5）。
/// Count 与 WPF 首页 DefectBarChart 同源（采集循环写入的设备实体缺陷计数）。
/// </summary>
public sealed record DeviceDefectCountDto
{
    public string DeviceId { get; init; } = "";

    public string DeviceName { get; init; } = "";

    public required string Name { get; init; }

    public int Count { get; init; }
}

/// <summary>
/// 设备上一班次产量汇总（低频元数据推送用，服务端班次切换缓存）。
/// 对齐 WPF 首页合格率卡的班次产量/不良差对比口径（HomeViewModel.ShiftOutputDiff/ShiftNgDiff）。
/// </summary>
public sealed record DeviceShiftSummaryDto
{
    public string DeviceId { get; init; } = "";

    public string ShiftName { get; init; } = "";

    public int Ok { get; init; }

    public int Ng { get; init; }
}

/// <summary>
/// 低频元数据推送包（约 5s 一次，由 Collector MetaPublisher 广播）：
/// 全部设备的当前工单 + 全局班次进度 + 缺陷 TOP5 + 上班次汇总。客户端订阅即得，无需轮询 Invoke。
/// </summary>
public sealed record MetaStateDto
{
    public IReadOnlyList<DeviceWorkOrderDto> Devices { get; init; } = [];

    public ShiftProgressDto Shift { get; init; } = new();

    /// <summary>各设备缺陷计数 TOP5（Count&gt;0 才推送；无数据时为空列表）。</summary>
    public IReadOnlyList<DeviceDefectCountDto> DefectTop { get; init; } = [];

    /// <summary>各设备上一班次产量汇总（班次切换缓存；无缓存时为空列表）。</summary>
    public IReadOnlyList<DeviceShiftSummaryDto> LastShifts { get; init; } = [];
}
