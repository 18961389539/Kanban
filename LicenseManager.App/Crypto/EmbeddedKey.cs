using System.Reflection;

namespace LicenseManager.Crypto;

/// <summary>
/// HMAC 密钥来源：优先环境变量，回退到内嵌常量。
/// </summary>
/// <remarks>
/// 安全说明：
/// - 这是 HMAC 对称密钥，签发工具（LicenseIssuer.CLI）和客户端（LicenseManager.App）共享同一密钥。
/// - 客户端反编译可获取此密钥，对应"标准强度"防护等级（防普通用户复制，不防专业破解）。
/// - 配合 Obfuscar 混淆可提升逆向难度。
/// - 实际部署时应替换为自行生成的随机密钥，并在签发工具中同步更新。
///
/// 密钥来源（P3-1）：
/// 1. 优先读取环境变量 KANBAN_HMAC_KEY（Base64 编码 32 字节）
///    - 用于生产环境：密钥不写入程序集，反编译无法获取
///    - 通过系统环境变量或启动脚本注入（如 setx KANBAN_HMAC_KEY "..."）
/// 2. 环境变量未设置或非法时回退到内嵌常量 HmacKeyBase64
///    - 用于开发测试：方便首次运行，不要求配置环境变量
///    - 正式发布前应替换为随机密钥
///
/// 密钥生成方式（PowerShell）：
///   $bytes = New-Object byte[] 32
///   [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
///   [Convert]::ToBase64String($bytes)
/// </remarks>
[Obfuscation(Exclude = false, ApplyToMembers = true)]
public static class EmbeddedKey
{
    /// <summary>环境变量名：用于外部加载 HMAC 密钥（Base64 编码 32 字节）</summary>
    public const string EnvKeyName = "KANBAN_HMAC_KEY";

    /// <summary>内嵌 HMAC-SHA256 密钥（32 字节，Base64 编码，44 字符含 padding）。
    /// 默认值仅供开发测试，正式发布前必须替换。
    /// 生产环境应通过 KANBAN_HMAC_KEY 环境变量注入，避免密钥写入程序集。</summary>
    public const string HmacKeyBase64 = "zUMnUR03aZR3jMzKkEUHf8iANX1nifFf725jv6LgONE=";

    /// <summary>
    /// 解码后的 HMAC 密钥字节。
    /// 优先从 KANBAN_HMAC_KEY 环境变量加载，未设置或非法时回退到内嵌常量。
    /// 进程内缓存（Lazy 保证只解析一次）。
    /// </summary>
    public static byte[] HmacKey => _hmacKeyLazy.Value;

    private static readonly Lazy<byte[]> _hmacKeyLazy = new(LoadKey);

    private static byte[] LoadKey()
    {
        // 1. 优先尝试环境变量
        try
        {
            var envValue = Environment.GetEnvironmentVariable(EnvKeyName);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                var bytes = Convert.FromBase64String(envValue);
                if (bytes.Length == 32)
                {
                    return bytes;
                }
                // 长度非法 → 回退到内嵌密钥（不抛异常，保证启动不中断）
            }
        }
        catch
        {
            // 环境变量解析失败 → 回退到内嵌密钥
        }

        // 2. 回退到内嵌常量
        return Convert.FromBase64String(HmacKeyBase64);
    }

    /// <summary>当前密钥来源描述（用于日志/调试）</summary>
    public static string KeySource
    {
        get
        {
            var envValue = Environment.GetEnvironmentVariable(EnvKeyName);
            return !string.IsNullOrWhiteSpace(envValue) ? $"环境变量 {EnvKeyName}" : "内嵌常量";
        }
    }

    /// <summary>激活码负载长度（字节）：5 机器码哈希 + 2 过期日期 = 7 字节</summary>
    public const int PayloadSize = 7;

    /// <summary>HMAC 标签长度（字节）：截断到 8 字节（64 位），提供 2^64 抗碰撞</summary>
    public const int TagSize = 8;

    /// <summary>完整激活码字节数：负载 + HMAC 标签 = 15 字节</summary>
    public const int TotalSize = PayloadSize + TagSize;

    /// <summary>Base32 编码后字符数：15 字节 → 24 字符</summary>
    public const int EncodedLength = 24;

    /// <summary>分组后的激活码字符数：5 组 × 5 字符 = 25 字符（含 1 个校验位）</summary>
    public const int FormattedLength = 25;
}
