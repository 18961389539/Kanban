using Kanban.Contracts.Enums;
using Kanban.Contracts.Metrics;

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
/// 设备缺陷帕累托行（低频元数据推送）。
/// 计数与 WPF 首页同源（采集循环写入的设备实体缺陷计数）；占比/累计由
/// <see cref="DefectParetoMetrics"/> 单源计算。命名 TOP5 之外的正计数合并为 IsOthers 行。
/// </summary>
public sealed record DeviceDefectCountDto
{
    public string DeviceId { get; init; } = "";

    public string DeviceName { get; init; } = "";

    public required string Name { get; init; }

    public int Count { get; init; }

    /// <summary>1-based 名次；「其他」为 0。</summary>
    public int Rank { get; init; }

    /// <summary>占总缺陷件数的比例（0–1）。旧端未下发时为 0，客户端回退相对最大值。</summary>
    public double ShareOfTotal { get; init; }

    /// <summary>从第一名累计到本行的占比（0–1）。</summary>
    public double CumulativeShare { get; init; }

    public bool IsVitalFew { get; init; }

    public bool IsOthers { get; init; }

    public int OtherKindCount { get; init; }

    public DefectSeverity Severity { get; init; }

    public DefectCategory Category { get; init; }

    public string PlcAddress { get; init; } = "";
}

/// <summary>各设备缺陷帕累托摘要（空状态与合计；无正计数时仍下发以便区分未配置与全 0）。</summary>
public sealed record DeviceDefectSummaryDto
{
    public string DeviceId { get; init; } = "";

    public int ConfiguredCount { get; init; }

    public int TotalCount { get; init; }

    public DefectParetoEmptyKind EmptyKind { get; init; }
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

    /// <summary>各设备缺陷帕累托行（正计数 TOP5 + 可选「其他」；无正计数时该设备无行）。</summary>
    public IReadOnlyList<DeviceDefectCountDto> DefectTop { get; init; } = [];

    /// <summary>各设备缺陷摘要（含未配置/全 0）；与 DefectTop 一起下发。</summary>
    public IReadOnlyList<DeviceDefectSummaryDto> DefectSummaries { get; init; } = [];

    /// <summary>各设备上一班次产量汇总（班次切换缓存；无缓存时为空列表）。</summary>
    public IReadOnlyList<DeviceShiftSummaryDto> LastShifts { get; init; } = [];
}
