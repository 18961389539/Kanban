using System.IO;
using System.Text.Json;
using LicenseManager.Crypto;

namespace LicenseIssuer;

/// <summary>
/// verify 命令：验证激活码的合法性、机器码绑定、过期状态及撤销状态。
/// </summary>
/// <remarks>
/// 用法：
///   LicenseIssuer.exe verify --key <activation-code>                         # 仅验证签名/格式
///   LicenseIssuer.exe verify --key <activation-code> --machine ABCD1234       # 同时校验机器绑定
///
/// 验证内容：
/// 1. 激活码格式和分组校验位（兼容旧 HMAC 与新 ECDSA 长度）
/// 2. 签名（新码使用 ECDSA；旧码使用 HMAC）
/// 3. 机器码绑定（如指定 --machine）
/// 4. 过期状态（基于当前 UTC 时间）
/// 5. 撤销状态（查询 issued/ 和 revoked/ 目录，匹配 ProductKey）
///
/// 应用场景：用户报障时，管理员用此命令快速判断激活码是否合法、是否已撤销。
/// </remarks>
public static class VerifyCommand
{
    private const string IssuedDir = "issued";
    private const string RevokedDir = "revoked";

    public static int Handle(string productKey, string? machine)
    {
        // 1. 校验激活码格式与签名
        if (!ProductKeyCodec.TryParseRaw(productKey, out var raw))
        {
            PrintFail("激活码格式非法或长度不正确。");
            return 2;
        }

        var formattedKey = ProductKeyCodec.Format(raw);

        // 先从签名载荷提取绑定机器码，再让 ProductKeyCodec 统一验证旧 HMAC 或新 ECDSA 格式。
        if (!Base32.TryDecode(raw[..^1], out var bytes) || bytes.Length < EmbeddedKey.PayloadSize)
        {
            PrintFail("激活码解码失败：Base32 数据长度不正确。");
            return 2;
        }

        var machineHashInKey = Base32.Encode(bytes.AsSpan(0, 5));
        var verifiedLicense = ProductKeyCodec.TryDecode(formattedKey, machineHashInKey);
        if (verifiedLicense == null)
        {
            PrintFail("激活码签名验证失败：激活码已被篡改、格式不支持或签发密钥不匹配。");
            return 2;
        }

        // 2. 解析激活码中的机器码哈希和过期日期
        var expireDate = verifiedLicense.ExpireDate;

        // 3. 机器码绑定校验（如指定 --machine）
        var machineMatch = true;
        if (!string.IsNullOrEmpty(machine))
        {
            if (machine.Length != 8 || !Base32.TryDecode(machine, out var machineHashBytes) || machineHashBytes.Length != 5)
            {
                PrintFail($"机器码格式非法：必须为 8 字符 Base32，当前：{machine}");
                return 2;
            }
            machineMatch = string.Equals(machineHashInKey, machine, StringComparison.OrdinalIgnoreCase);
        }

        // 4. 过期状态
        var now = DateTime.UtcNow;
        var isExpired = expireDate.HasValue && now > expireDate.Value;

        // 5. 撤销状态：在 issued/ 和 revoked/ 中按 ProductKey 匹配
        var (issuedRecord, revokedRecord) = FindRecordByProductKey(formattedKey);
        var isRevoked = revokedRecord != null;

        // ──────────── 输出验证报告 ────────────
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║              激活码验证报告                       ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"  激活码：       {formattedKey}");
        Console.WriteLine($"  签名验证：     √ 通过");
        Console.WriteLine($"  绑定机器码：   {machineHashInKey}");

        if (!string.IsNullOrEmpty(machine))
        {
            Console.Write("  机器码匹配：   ");
            if (machineMatch)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"√ 匹配（{machine}）");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ 不匹配（输入：{machine}，激活码绑定：{machineHashInKey}）");
            }
            Console.ResetColor();
        }

        Console.Write("  授权类型：     ");
        if (expireDate == null)
        {
            Console.WriteLine("永久授权");
        }
        else
        {
            Console.Write($"限期授权（到期 {expireDate:yyyy-MM-dd} UTC）→ ");
            if (isExpired)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("已过期");
            }
            else
            {
                var remaining = expireDate.Value - now;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"有效（剩余 {Math.Max(0, (int)remaining.TotalDays)} 天）");
            }
            Console.ResetColor();
        }

        // 签发记录
        Console.WriteLine(new string('─', 50));
        if (issuedRecord != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  签发记录：     ✓ 已签发（{issuedRecord.IssuedAt:yyyy-MM-dd HH:mm:ss} UTC）");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  签发记录：     ⚠ 未在 issued/ 目录找到对应记录");
            Console.ResetColor();
        }

        // 撤销记录
        Console.Write("  撤销状态：     ");
        if (isRevoked && revokedRecord != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ 已撤销（{revokedRecord.RevokedAt:yyyy-MM-dd HH:mm:ss} UTC）");
            Console.ResetColor();
            if (!string.IsNullOrEmpty(revokedRecord.RevokeReason))
            {
                Console.WriteLine($"  撤销原因：     {revokedRecord.RevokeReason}");
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ 未撤销");
            Console.ResetColor();
        }

        // 最终结论
        Console.WriteLine(new string('─', 50));
        var valid = !isExpired && !isRevoked && machineMatch;
        if (valid)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  结论：激活码合法且可用。");
            Console.ResetColor();
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("  结论：激活码不可用（");
            var reasons = new List<string>();
            if (!machineMatch) reasons.Add("机器码不匹配");
            if (isExpired) reasons.Add("已过期");
            if (isRevoked) reasons.Add("已撤销");
            Console.WriteLine(string.Join("、", reasons) + "）。");
            Console.ResetColor();
            return 1;
        }
    }

    /// <summary>在 issued/ 和 revoked/ 目录中按 ProductKey 查找记录。</summary>
    private static (IssuedRecord? issued, IssuedRecord? revoked) FindRecordByProductKey(string formattedKey)
    {
        IssuedRecord? issued = null;
        IssuedRecord? revoked = null;

        foreach (var (dir, isRevoked) in new[] { (IssuedDir, false), (RevokedDir, true) })
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var record = JsonSerializer.Deserialize<IssuedRecord>(json);
                    if (record == null) continue;

                    if (string.Equals(record.ProductKey, formattedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        if (isRevoked) revoked = record;
                        else issued = record;
                        break;  // 同一目录只取第一个匹配
                    }
                }
                catch
                {
                    // 跳过损坏的记录文件
                }
            }
        }

        return (issued, revoked);
    }

    private static void PrintFail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ {message}");
        Console.ResetColor();
    }

}
