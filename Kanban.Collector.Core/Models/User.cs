using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanban.Collector.Core.Models;

/// <summary>
/// 系统用户账号。密码以 PBKDF2-SHA256 + 随机 salt 哈希存储，源码不含明文密码。
/// 持久化到 users.json（与 settings.json 同目录，复用 AppSettings.WriteFileAtomically 原子写入）。
/// </summary>
public partial class User : ObservableObject
{
    /// <summary>用户名（唯一，登录凭据）。不可为空。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>显示名（用于界面展示，如"张工"）。可为空，回退为 Username。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色（决定可访问页面与可执行操作）。</summary>
    [ObservableProperty]
    private UserRole _role = UserRole.Operator;

    /// <summary>
    /// 密码哈希（Base64）。新版格式：pbkdf2${iterations}${saltBase64}${hashBase64}（PBKDF2-SHA256）；
    /// 兼容旧版格式 {saltBase64}:{hashBase64}（SHA256(salt + password)）。
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>是否启用（禁用账号不可登录）。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>创建时间（本地时间，与工单/审计等域口径一致）。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最后登录时间（本地时间）。持久化到 users.json。</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>连续登录失败次数（达到阈值触发锁定；登录成功/解锁/重置密码时清零）。</summary>
    public int FailedAttempts { get; set; }

    /// <summary>锁定截止时间（本地时间）。null = 未锁定。</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>首次登录必须修改密码（登录成功后强制弹改密对话框）。</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>界面显示名（DisplayName 为空时回退 Username）。</summary>
    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}
