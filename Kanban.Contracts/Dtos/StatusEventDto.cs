using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Dtos;

/// <summary>
/// 设备状态转换事件（边沿事件流，用于 OEE 状态回溯/看板状态高亮）。
/// </summary>
public sealed record StatusEventDto
{
    /// <summary>服务端单调递增序号（断线补拉游标；仅在 <see cref="ServerEpoch"/> 不变时有效）</summary>
    public long Seq { get; init; }

    /// <summary>
    /// 服务端事件纪元（进程启动时刻的 TickCount）。Collector 重启后 Seq 会从 1 重新计数，
    /// 客户端必须在此值变化时重置补拉游标，否则旧游标会把新进程的低 Seq 事件全部过滤掉（漏报）。
    /// 与 <see cref="AlarmEventDto.ServerEpoch"/> 同语义（两条流各自独立计数，但共享同一纪元）。
    /// </summary>
    public long ServerEpoch { get; init; }

    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }

    /// <summary>转换前状态（PreviousState=0 表示初始状态）</summary>
    public required DeviceStatus PreviousState { get; init; }

    /// <summary>转换后状态</summary>
    public required DeviceStatus CurrentState { get; init; }

    /// <summary>离线原因；转入非离线时为 <see cref="OfflineCause.None"/>。</summary>
    public OfflineCause OfflineCause { get; init; }

    public DateTime EventTime { get; init; }

    /// <summary>班次名称快照</summary>
    public required string ShiftName { get; init; }
}
