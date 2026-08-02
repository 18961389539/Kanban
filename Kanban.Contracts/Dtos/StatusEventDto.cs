using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Dtos;

/// <summary>
/// 设备状态转换事件（边沿事件流，用于 OEE 状态回溯/看板状态高亮）。
/// </summary>
public sealed record StatusEventDto
{
    /// <summary>服务端单调递增序号</summary>
    public long Seq { get; init; }

    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }

    /// <summary>转换前状态（PreviousState=0 表示初始状态）</summary>
    public required DeviceStatus PreviousState { get; init; }

    /// <summary>转换后状态</summary>
    public required DeviceStatus CurrentState { get; init; }

    public DateTime EventTime { get; init; }

    /// <summary>班次名称快照</summary>
    public required string ShiftName { get; init; }
}
