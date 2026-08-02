using System.Text.Json.Serialization;

namespace LicenseManager.Models;

/// <summary>
/// 激活成功后的授权信息（从激活码解码并验签通过后得到）。
/// </summary>
public class LicenseInfo
{
    /// <summary>激活码中绑定的机器码哈希（5 字节，Base32 编码后 8 字符）</summary>
    public string MachineCodeHash { get; set; } = string.Empty;

    /// <summary>过期日期（UTC）。null 表示永久授权。</summary>
    public DateTime? ExpireDate { get; set; }

    /// <summary>激活时间（首次输入激活码并验证通过的时间）</summary>
    public DateTime ActivatedAt { get; set; }

    /// <summary>原始激活码（用于显示和重新验证）</summary>
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>是否永久授权</summary>
    [JsonIgnore]
    public bool IsPermanent => !ExpireDate.HasValue;

    /// <summary>是否已过期（基于当前 UTC 时间）</summary>
    [JsonIgnore]
    public bool IsExpired => ExpireDate.HasValue && DateTime.UtcNow > ExpireDate.Value;
}
