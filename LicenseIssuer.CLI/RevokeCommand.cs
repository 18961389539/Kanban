using System.IO;
using System.Text.Json;

namespace LicenseIssuer;

/// <summary>
/// revoke 命令：撤销指定机器码的签发记录。
/// </summary>
/// <remarks>
/// 将 issued/ 目录下匹配机器码的签发记录移动到 revoked/ 目录，
/// 并在记录中添加 RevokedAt 和 RevokeReason 字段。
///
/// 注意：离线激活系统无法主动失效已激活的客户端（客户端无网络连接）。
/// 此命令仅用于：
/// - 标记签发记录为已撤销，便于管理员审计
/// - 用户重新激活时，管理员可查询 revoked/ 目录确认是否已撤销
/// </remarks>
public static class RevokeCommand
{
    private const string IssuedDir = "issued";
    private const string RevokedDir = "revoked";

    public static void Handle(string machine, string? reason)
    {
        // 1. 校验机器码格式
        if (string.IsNullOrWhiteSpace(machine) || machine.Length != 8)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"错误：机器码必须为 8 字符，当前：{machine}");
            Console.ResetColor();
            return;
        }

        if (!Directory.Exists(IssuedDir))
        {
            Console.WriteLine($"暂无签发记录（{IssuedDir}/ 目录不存在）。");
            return;
        }

        // 2. 查找该机器码的所有签发记录（文件名格式：yyyyMMdd_HHmmss_{machine}.json）
        var files = Directory.GetFiles(IssuedDir, $"*_{machine}.json")
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine($"未找到机器码 {machine} 的签发记录。");
            return;
        }

        // 3. 移动到 revoked/ 目录并添加撤销信息
        Directory.CreateDirectory(RevokedDir);
        var revokedAt = DateTime.UtcNow;
        var successCount = 0;

        Console.WriteLine($"机器码 {machine} 共 {files.Count} 条签发记录：");
        Console.WriteLine(new string('─', 60));

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<IssuedRecord>(json);
                if (record == null)
                {
                    Console.WriteLine($"  [跳过损坏的记录：{Path.GetFileName(file)}]");
                    continue;
                }

                // 更新记录：添加撤销信息
                record.RevokedAt = revokedAt;
                record.RevokeReason = reason;

                // 写入 revoked/ 目录（文件名加 revoked_ 前缀）
                var revokedFileName = $"revoked_{Path.GetFileName(file)}";
                var revokedPath = Path.Combine(RevokedDir, revokedFileName);
                File.WriteAllText(revokedPath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
                File.Delete(file);

                successCount++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ {Path.GetFileName(file)} → {revokedFileName}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ✗ 撤销失败：{Path.GetFileName(file)} - {ex.Message}");
                Console.ResetColor();
            }
        }

        Console.WriteLine(new string('─', 60));
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"已撤销 {successCount}/{files.Count} 条记录。");
        Console.ResetColor();

        if (!string.IsNullOrEmpty(reason))
        {
            Console.WriteLine($"撤销原因：{reason}");
        }
        Console.WriteLine();
        Console.WriteLine("注意：撤销仅标记签发记录，无法主动失效已激活的客户端。");
        Console.WriteLine("      若用户重新激活，管理员可通过 list --machine 查询是否已撤销。");
    }
}
