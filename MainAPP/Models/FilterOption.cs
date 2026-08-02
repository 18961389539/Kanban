namespace MainAPP.Models;

/// <summary>
/// 通用筛选下拉项（班次/报警名称等），null 表示"全部"。
/// </summary>
public record FilterOption(string? Value, string DisplayText);
