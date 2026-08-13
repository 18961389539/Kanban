using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Dtos;

/// <summary>
/// 配方参数项 DTO（一条配方多行：参数名 + PLC 地址 + 类型 + 值 + 校验范围）。
/// </summary>
public sealed record RecipeItemDto
{
    public required string ParamName { get; init; }

    public required string PlcAddress { get; init; }

    /// <summary>PLC 数据类型，决定下发读写方法与值校验规则。</summary>
    public PlcDataType DataType { get; init; } = PlcDataType.Int32;

    /// <summary>参数值，统一字符串存储；下发/校验时按 <see cref="DataType"/> 解析。</summary>
    public required string Value { get; init; }

    /// <summary>数值类参数最小值（可选，仅 Int32/Float/UInt16 有意义）。</summary>
    public double? Min { get; init; }

    /// <summary>数值类参数最大值（可选，仅 Int32/Float/UInt16 有意义）。</summary>
    public double? Max { get; init; }

    public string Unit { get; init; } = string.Empty;
}

/// <summary>
/// 配方 DTO（按机型归属的配方库条目，跨进程传输）。
/// Remote 模式下 MainAPP 配方管理页经 SignalR 同步到 Collector 落盘 recipes.json。
/// </summary>
public sealed record RecipeDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>归属机型（设备类型）。空字符串 = 通用配方（所有机型可用）。</summary>
    public required string MachineType { get; init; }

    public string Remark { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    /// <summary>参数项（List 承载：MessagePack 使用确定性 ListFormatter，避免接口集合的只读包装序列化兼容问题）。</summary>
    public List<RecipeItemDto> Items { get; init; } = [];
}

/// <summary>配方下发单参数项结果。</summary>
public sealed record RecipeItemResultDto(
    string ParamName,
    bool Success,
    string Message);

/// <summary>
/// 配方下发进度（ApplyRecipeAsync 执行期间经 OnRecipeApplyProgress 逐项推送）。
/// Index/Total 供 UI 显示"第 i/N 项"；Message 为本地化状态文案（写入中/写入结果/校验结果）。
/// </summary>
public sealed record RecipeApplyProgressDto(
    string ParamName,
    int Index,
    int Total,
    bool Success,
    string Message);

/// <summary>
/// 配方下发结果。Success=false 时 Message 说明失败原因（校验失败/PLC 写入失败/读回不一致已回滚）。
/// </summary>
public sealed record RecipeApplyResultDto(
    bool Success,
    string Message,
    IReadOnlyList<RecipeItemResultDto> Items);
