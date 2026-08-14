using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Dtos;

/// <summary>
/// 设备实时快照（服务端按采集节奏推送，客户端只做渲染）。
/// 字段对齐 MainAPP.Models.DeviceRuntime 的展示口径。
/// </summary>
public sealed record DeviceSnapshotDto
{
    /// <summary>设备 Id</summary>
    public required string DeviceId { get; init; }

    /// <summary>设备名称</summary>
    public required string DeviceName { get; init; }

    /// <summary>设备状态</summary>
    public required DeviceStatus Status { get; init; }

    /// <summary>状态字原始值</summary>
    public int StatusWord { get; init; }

    // ──────────── PLC 原始值（本轮） ────────────
    public int OkProduction { get; init; }
    public int NgProduction { get; init; }

    // ──────────── 会话（班次）累计值 ────────────
    public int TotalOkProduction { get; init; }
    public int TotalNgProduction { get; init; }
    public double RunTime { get; init; }
    public double AlarmTime { get; init; }
    public double PausedTime { get; init; }

    // ──────────── OEE（服务端已算好，客户端不重复计算） ────────────
    public double QualityRate { get; init; }
    public double PerformanceRate { get; init; }
    public double AvailabilityRate { get; init; }
    public double Oee { get; init; }

    /// <summary>目标周期（个/小时）</summary>
    public int TargetCycle { get; init; }

    /// <summary>当前配方名（空字符串 = 未配置）。与 WPF 设备状态卡同源（设备实体 RecipeName）。</summary>
    public string RecipeName { get; init; } = "";

    /// <summary>当前配方值（PLC 写入值）。</summary>
    public int RecipeValue { get; init; }

    /// <summary>当前激活的报警列表（触发中的报警）</summary>
    public IReadOnlyList<ActiveAlarmDto> ActiveAlarms { get; init; } = [];

    /// <summary>快照时间</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>单调递增序号（断线补拉、乱序纠正用）</summary>
    public long Seq { get; init; }

    /// <summary>
    /// 删除标记（tombstone）：Collector 在设备配置删除后广播此快照，
    /// 客户端收到后应从内存移除该设备（快照流只有 upsert 语义，删除须显式表达）。
    /// </summary>
    public bool Removed { get; init; }
}

/// <summary>
/// 当前激活的报警（快照内嵌）。
/// </summary>
public sealed record ActiveAlarmDto
{
    public required string AlarmId { get; init; }
    public required string Name { get; init; }
    public required string PlcAddress { get; init; }
    public required string Description { get; init; }
    public required AlarmLevel Level { get; init; }
    public DateTime StartTime { get; init; }
}
