using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LicenseManager.Crypto;
using LicenseManager.Models;

namespace LicenseManager.Services;

/// <summary>
/// 授权信息持久化：用 DPAPI 加密 + HMAC 签名保护，防止文件被篡改或复制到其他机器。
/// 文件存储在 %AppData%\Kanban\ 下，绑定当前 Windows 用户。
/// </summary>
public class LicenseStore
{
    private const string LicenseFileName = "license.dat";
    private const string TrialFileName = "trial.dat";
    private const string ProtectedTrialPrefix = "dpapi|";

    private readonly string _licenseFilePath;
    private readonly string _trialFilePath;

    public LicenseStore(string? storageDir = null)
    {
        // 与 AppSettings.DataRoot 保持一致：优先读取 KANBAN_DATA_DIR 环境变量，
        // 允许测试/沙箱环境将授权文件重定向到可写目录，避免硬编码 AppData 导致无法运行。
        var dir = storageDir
            ?? Environment.GetEnvironmentVariable("KANBAN_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kanban");
        Directory.CreateDirectory(dir);
        _licenseFilePath = Path.Combine(dir, LicenseFileName);
        _trialFilePath = Path.Combine(dir, TrialFileName);
    }

    // ──────────── 激活信息 ────────────

    public LicenseInfo? LoadLicense()
    {
        if (!File.Exists(_licenseFilePath)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(_licenseFilePath);
            var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<LicenseInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    public void SaveLicense(LicenseInfo info)
    {
        var json = JsonSerializer.Serialize(info);
        var bytes = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        AtomicWriteBytes(_licenseFilePath, encrypted);
    }

    public void ClearLicense()
    {
        if (File.Exists(_licenseFilePath)) File.Delete(_licenseFilePath);
    }

    // ──────────── 试用期状态 ────────────

    public TrialState? LoadTrial()
    {
        if (!File.Exists(_trialFilePath)) return null;
        try
        {
            var content = File.ReadAllText(_trialFilePath);

            // 新电脑首次试用可能尚未配置激活码签名密钥，使用当前用户 DPAPI
            // 保存试用状态；配置 HMAC 后仍兼容并优先使用原有签名格式。
            if (content.StartsWith(ProtectedTrialPrefix, StringComparison.Ordinal))
            {
                var protectedBytes = Convert.FromBase64String(content[ProtectedTrialPrefix.Length..]);
                var jsonBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<TrialState>(jsonBytes);
            }

            var parts = content.Split('|');
            if (parts.Length != 2 || !EmbeddedKey.TryGetHmacKey(out _)) return null;

            var signature = Convert.FromBase64String(parts[1]);
            var data = Encoding.UTF8.GetBytes(parts[0]);

            // 验证 HMAC 签名
            var expectedSig = HmacValidator.ComputeTag(data);
            if (!HmacValidator.ConstantTimeEquals(signature, expectedSig)) return null;

            return JsonSerializer.Deserialize<TrialState>(parts[0]);
        }
        catch
        {
            return null;
        }
    }

    public void SaveTrial(TrialState state)
    {
        var json = JsonSerializer.Serialize(state);
        var data = Encoding.UTF8.GetBytes(json);

        string content;
        if (EmbeddedKey.TryGetHmacKey(out _))
        {
            var signature = HmacValidator.ComputeTag(data);
            content = json + "|" + Convert.ToBase64String(signature);
        }
        else
        {
            var protectedBytes = ProtectedData.Protect(
                data,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            content = ProtectedTrialPrefix + Convert.ToBase64String(protectedBytes);
        }

        AtomicWriteText(_trialFilePath, content);
    }

    public void ClearTrial()
    {
        if (File.Exists(_trialFilePath)) File.Delete(_trialFilePath);
    }

    // ──────────── 原子写入（防断电损坏）────────────

    /// <summary>
    /// 原子写入字节文件：先写到 .tmp 临时文件，再 File.Move 替换（原子操作）。
    /// 避免断电或异常退出时文件被截断为半写状态导致 JSON/HMAC 解析失败。
    /// </summary>
    private static void AtomicWriteBytes(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>原子写入文本文件（同 AtomicWriteBytes）。</summary>
    private static void AtomicWriteText(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
