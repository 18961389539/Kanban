using System;

namespace LicenseIssuer.Wpf;

/// <summary>
/// 签发记录（JSON 持久化）。
/// </summary>
public class IssuedRecord
{
    public string MachineCode { get; set; } = string.Empty;

    public DateTime? ExpireDate { get; set; }

    public bool IsPermanent { get; set; }

    public string ProductKey { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }
}
