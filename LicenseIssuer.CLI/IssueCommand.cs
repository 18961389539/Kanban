using System.IO;
using System.Text.Json;
using LicenseManager.Crypto;

namespace LicenseIssuer;

/// <summary>
/// issue 命令：生成激活码并写入签发记录。
/// </summary>
public static class IssueCommand
{
    private const string IssuedDir = "issued";

    public static void Handle(string machine, DateTime? expire)
    {
        // 1. 校验机器码格式（8 字符 Base32）
        if (string.IsNullOrWhiteSpace(machine) || machine.Length != 8)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"错误：机器码必须为 8 字符，当前：{machine}");
            Console.ResetColor();
            return;
        }

        if (!Base32.TryDecode(machine, out var machineHashBytes) || machineHashBytes.Length != 5)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"错误：机器码 '{machine}' 不是合法的 Base32 编码");
            Console.ResetColor();
            return;
        }

        // 2. 计算过期日期（UTC，到当天结束）
        DateTime? expireUtc = null;
        if (expire.HasValue)
        {
            expireUtc = expire.Value.Date.AddDays(1);  // 到指定日期的 23:59:59 UTC
        }

        // 3. 生成激活码
        var productKey = ProductKeyCodec.Encode(machineHashBytes, expireUtc);

        // 4. 写入签发记录
        var record = new IssuedRecord
        {
            MachineCode = machine,
            ExpireDate = expireUtc,
            IsPermanent = !expireUtc.HasValue,
            ProductKey = productKey,
            IssuedAt = DateTime.UtcNow,
        };

        var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{machine}.json";
        var filePath = Path.Combine(IssuedDir, fileName);
        Directory.CreateDirectory(IssuedDir);
        File.WriteAllText(filePath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));

        // 5. 输出结果
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║              激活码签发成功                       ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"  机器码：     {machine}");
        Console.WriteLine($"  授权类型：   {(expireUtc.HasValue ? $"限期至 {expire:yyyy-MM-dd}" : "永久授权")}");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  激活码：");
        Console.WriteLine();
        Console.WriteLine($"    {productKey}");
        Console.WriteLine();
        Console.ResetColor();
        Console.WriteLine($"  签发记录已保存：{filePath}");
        Console.WriteLine();
        Console.WriteLine("  请将激活码发送给用户，用户在激活对话框中粘贴即可。");
    }
}

/// <summary>签发记录（JSON 持久化）</summary>
public class IssuedRecord
{
    public string MachineCode { get; set; } = string.Empty;
    public DateTime? ExpireDate { get; set; }
    public bool IsPermanent { get; set; }
    public string ProductKey { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }

    /// <summary>撤销时间（UTC），未撤销为 null。revoke 命令写入。</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>撤销原因，未撤销为 null。</summary>
    public string? RevokeReason { get; set; }

    /// <summary>是否已撤销</summary>
    public bool IsRevoked => RevokedAt.HasValue;
}
