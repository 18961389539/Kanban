using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Dtos;

/// <summary>
/// 报警事件（边沿事件流，客户端订阅后弹窗/高亮）。
/// Seq 为服务端分配的单调递增序号，客户端断线重连后带 LastSeq 补拉。
/// </summary>
public sealed record AlarmEventDto
{
    /// <summary>服务端单调递增序号（断线补拉游标；仅在 <see cref="ServerEpoch"/> 不变时有效）</summary>
    public long Seq { get; init; }

    /// <summary>
    /// 服务端事件纪元（进程启动时刻的 TickCount）。Collector 重启后 Seq 会从 1 重新计数，
    /// 客户端必须在此值变化时重置补拉游标，否则旧游标会把新进程的低 Seq 事件全部过滤掉（漏报）。
    /// </summary>
    public long ServerEpoch { get; init; }

    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string AlarmId { get; init; }
    public required string AlarmName { get; init; }
    public required string PlcAddress { get; init; }

    /// <summary>事件类型：触发 / 恢复 / 班次切换</summary>
    public required AlarmEventType EventType { get; init; }

    public required AlarmLevel Level { get; init; }

    /// <summary>事件发生时刻</summary>
    public DateTime EventTime { get; init; }

    /// <summary>班次名称快照</summary>
    public required string ShiftName { get; init; }
}
