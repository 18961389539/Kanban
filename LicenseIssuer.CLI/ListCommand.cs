using System.IO;
using System.Text.Json;

namespace LicenseIssuer;

/// <summary>
/// list 命令：列出已签发和已撤销的激活码记录。
/// </summary>
/// <remarks>
/// 用法：
///   LicenseIssuer.exe list                       # 列出所有记录
///   LicenseIssuer.exe list --machine ABCD1234    # 按机器码过滤
/// </remarks>
public static class ListCommand
{
    private const string IssuedDir = "issued";
    private const string RevokedDir = "revoked";

    public static void Handle(string? machineFilter = null)
    {
        var issuedRecords = LoadRecords(IssuedDir, machineFilter);
        var revokedRecords = LoadRecords(RevokedDir, machineFilter);

        if (issuedRecords.Count == 0 && revokedRecords.Count == 0)
        {
            if (string.IsNullOrEmpty(machineFilter))
            {
                Console.WriteLine("暂无签发记录。");
            }
            else
            {
                Console.WriteLine($"未找到机器码 {machineFilter} 的签发记录。");
            }
            return;
        }

        // 输出过滤信息
        if (!string.IsNullOrEmpty(machineFilter))
        {
            Console.WriteLine($"按机器码过滤：{machineFilter}");
            Console.WriteLine(new string('─', 80));
        }

        // 已签发记录
        if (issuedRecords.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"【已签发】共 {issuedRecords.Count} 条记录：");
            Console.ResetColor();
            Console.WriteLine(new string('─', 80));
            foreach (var record in issuedRecords)
            {
                PrintRecord(record, isRevoked: false);
            }
        }

        // 已撤销记录
        if (revokedRecords.Count > 0)
        {
            if (issuedRecords.Count > 0) Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"【已撤销】共 {revokedRecords.Count} 条记录：");
            Console.ResetColor();
            Console.WriteLine(new string('─', 80));
            foreach (var record in revokedRecords)
            {
                PrintRecord(record, isRevoked: true);
            }
        }

        // 汇总
        Console.WriteLine(new string('─', 80));
        Console.WriteLine($"合计：{issuedRecords.Count + revokedRecords.Count} 条（已签发 {issuedRecords.Count}，已撤销 {revokedRecords.Count}）");
    }

    /// <summary>从指定目录加载记录，可选按机器码过滤。</summary>
    private static List<IssuedRecord> LoadRecords(string dir, string? machineFilter)
    {
        var records = new List<IssuedRecord>();
        if (!Directory.Exists(dir)) return records;

        var files = Directory.GetFiles(dir, "*.json")
            .OrderByDescending(f => f)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<IssuedRecord>(json);
                if (record == null) continue;

                // 按机器码过滤（空过滤器则全部加载）
                if (!string.IsNullOrEmpty(machineFilter) &&
                    !string.Equals(record.MachineCode, machineFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                records.Add(record);
            }
            catch
            {
                // 跳过损坏的记录文件
            }
        }
        return records;
    }

    private static void PrintRecord(IssuedRecord record, bool isRevoked)
    {
        var status = record.IsPermanent ? "永久" : $"{record.ExpireDate:yyyy-MM-dd}";
        Console.WriteLine($"  签发时间： {record.IssuedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"  机器码：   {record.MachineCode}");
        Console.WriteLine($"  授权类型： {status}");
        Console.WriteLine($"  激活码：   {record.ProductKey}");
        if (isRevoked && record.RevokedAt.HasValue)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  撤销时间： {record.RevokedAt.Value:yyyy-MM-dd HH:mm:ss} UTC");
            if (!string.IsNullOrEmpty(record.RevokeReason))
            {
                Console.WriteLine($"  撤销原因： {record.RevokeReason}");
            }
            Console.ResetColor();
        }
        Console.WriteLine(new string('─', 80));
    }
}
