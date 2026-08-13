namespace Kanban.Core.Models;

/// <summary>
/// 用户角色（权限分级）。
/// Operator（操作员，默认）：查看看板、确认/复位报警、工单开完工、录入缺陷。
/// Engineer（工程师）：+ 设备配置增删改、导入导出、阈值调整、生成样本数据。
/// Admin（管理员）：+ 用户管理、数据备份恢复、系统设置、许可证管理。
/// </summary>
public enum UserRole
{
    /// <summary>操作员：仅查看与日常操作。</summary>
    Operator = 0,

    /// <summary>工程师：操作员 + 设备配置/导入导出/样本数据。</summary>
    Engineer = 1,

    /// <summary>管理员：工程师 + 用户管理/系统设置。</summary>
    Admin = 2,
}

/// <summary>角色扩展：权限比较辅助。</summary>
public static class UserRoleExtensions
{
    /// <summary>当前角色是否满足所需最低角色（含等于）。</summary>
    public static bool AtLeast(this UserRole current, UserRole required)
        => (int)current >= (int)required;

    /// <summary>是否工程师或以上。</summary>
    public static bool IsEngineerOrAbove(this UserRole role) => role.AtLeast(UserRole.Engineer);

    /// <summary>是否管理员。</summary>
    public static bool IsAdmin(this UserRole role) => role == UserRole.Admin;
}
