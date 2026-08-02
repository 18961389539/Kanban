namespace Kanban.Contracts.Enums;

/// <summary>
/// 缺陷严重级别（与 MainAPP.Models.DefectSeverity 数值一致）。
/// </summary>
public enum DefectSeverity
{
    Minor = 0,
    Major = 1,
    Critical = 2,
}

/// <summary>
/// 缺陷类别（与 MainAPP.Models.DefectCategory 数值一致）。
/// </summary>
public enum DefectCategory
{
    Appearance = 0,
    Dimension = 1,
    Function = 2,
    Packaging = 3,
    Other = 4,
}
