using Kanban.Core.Models;

namespace Kanban.Core.Services;

/// <summary>
/// 班次标识：用于检测班次切换与产量基线归属判定。
/// 替代早期 "Name|StartTime|EndTime" 字符串拼接方案，避免班次名包含 "|" 字符导致解析错误。
/// <para>
/// ToString() 仍返回原拼接格式，以保持 baselines.json 磁盘格式向后兼容
/// （<see cref="ProductionBaselineStore.BaselineShiftId"/> 仍是 string?）。
/// </para>
/// </summary>
public sealed record ShiftIdentifier(string Name, TimeSpan StartTime, TimeSpan EndTime)
{
    /// <summary>
    /// 序列化为 "Name|StartTime|EndTime"，供基线文件磁盘存储格式使用。
    /// </summary>
    public override string ToString() => $"{Name}|{StartTime}|{EndTime}";

    /// <summary>
    /// 从 ShiftConfig 构造 ShiftIdentifier；config 为 null 时返回 null。
    /// </summary>
    public static ShiftIdentifier? From(ShiftConfig? config)
        => config is null ? null : new(config.Name, config.StartTime, config.EndTime);
}
