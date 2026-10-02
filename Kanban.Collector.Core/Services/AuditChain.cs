using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

/// <summary>审计记录链。每条记录的哈希覆盖上一条哈希和本条内容，删改中间记录会断开。</summary>
public static class AuditChain
{
    public static string Hash(string? previous, AuditEntry entry)
    {
        var payload = string.Join('\u001f',
            previous ?? "",
            entry.Timestamp.Ticks.ToString(CultureInfo.InvariantCulture),
            entry.Operator ?? "",
            entry.Action ?? "",
            entry.TargetType ?? "",
            entry.TargetId ?? "",
            entry.Succeeded ? "1" : "0",
            entry.Detail ?? "",
            entry.BeforeJson ?? "",
            entry.AfterJson ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

/// <summary>整条记录链的核对结果。Unchecked 表示这次没有完成核对。</summary>
public sealed record AuditChainReport(bool Intact, int Checked, bool Unchecked = false);
