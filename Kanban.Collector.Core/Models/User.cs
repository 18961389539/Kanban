using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanban.Core.Models;

/// <summary>
/// 系统用户账号。密码以 SHA256 + 随机 salt 哈希存储，源码不含明文密码。
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
    /// 密码哈希（Base64）。格式：{saltBase64}:{hashBase64}。
    /// salt 为 16 字节随机数，hash = SHA256(salt + password)。
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>是否启用（禁用账号不可登录）。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最后登录时间（UTC，本地显示时转换）。</summary>
    [JsonIgnore]
    public DateTime? LastLoginAt { get; set; }

    /// <summary>界面显示名（DisplayName 为空时回退 Username）。</summary>
    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}
