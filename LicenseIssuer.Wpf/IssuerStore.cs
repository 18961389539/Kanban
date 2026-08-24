using System;
using System.IO;
using System.Text.Json;

namespace LicenseIssuer.Wpf;

/// <summary>
/// 签发记录持久化：使用程序目录下的 issued/ 目录与 JSON 格式。
/// </summary>
public static class IssuerStore
{
    private const string IssuedDir = "issued";

    /// <summary>签发记录目录（位于程序所在目录下的 issued/）。</summary>
    public static string RecordsDirectory => Path.Combine(AppContext.BaseDirectory, IssuedDir);

    /// <summary>写入一条签发记录（文件名：yyyyMMdd_HHmmss_{machine}.json）。</summary>
    public static string Save(IssuedRecord record)
    {
        Directory.CreateDirectory(RecordsDirectory);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{record.MachineCode}.json";
        var filePath = Path.Combine(RecordsDirectory, fileName);
        File.WriteAllText(filePath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        return filePath;
    }
}
