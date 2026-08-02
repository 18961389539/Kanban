using System;

namespace LicenseIssuer.Wpf;

/// <summary>
/// 签发记录（JSON 持久化）。
/// 字段顺序与 LicenseIssuer.CLI 中的 IssuedRecord 保持一致，
/// 因此本工具写入的 issued/*.json 可被 CLI 的 list / revoke 命令读取。
/// </summary>
public class IssuedRecord
{
    public string MachineCode { get; set; } = string.Empty;

    public DateTime? ExpireDate { get; set; }

    public bool IsPermanent { get; set; }

    public string ProductKey { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    /// <summary>撤销时间（UTC），未撤销为 null。</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>撤销原因，未撤销为 null。</summary>
    public string? RevokeReason { get; set; }

    /// <summary>是否已撤销</summary>
    public bool IsRevoked => RevokedAt.HasValue;
}
