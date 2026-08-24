namespace LicenseManager.Crypto;

/// <summary>
/// 旧版 HMAC 密钥来源：从环境变量 KANBAN_HMAC_KEY 加载，不写入程序集。
/// </summary>
/// <remarks>
/// 安全说明：
/// - 这是旧版 HMAC 对称密钥，旧签发工具和客户端共享同一密钥。
/// - 密钥**必须**通过环境变量注入，绝不写入程序集——否则反编译即可提取密钥并离线自签任意机器码的激活码。
/// - 生成方式（PowerShell）：
///     $bytes = New-Object byte[] 32
///     [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
///     [Convert]::ToBase64String($bytes)
/// - 新版 ECDSA 正式激活不依赖此密钥；它只用于旧激活码和兼容的 HMAC 状态文件。
/// </remarks>
/// <remarks>
/// 不混淆：LicenseIssuer.* 工具直接引用此类的常量（TotalSize/PayloadSize 等），混淆重命名会导致外部程序集 TypeLoadException。
/// </remarks>
public static class EmbeddedKey
{
    /// <summary>环境变量名：用于外部加载 HMAC 密钥（Base64 编码 32 字节）</summary>
    public const string EnvKeyName = "KANBAN_HMAC_KEY";

    /// <summary>
    /// 尝试读取 HMAC 密钥。试用期可以在没有密钥的全新机器上运行，
    /// 因此试用状态持久化不能直接访问会抛异常的 <see cref="HmacKey"/>。
    /// 旧版 HMAC 激活码验签仍使用 <see cref="HmacKey"/>；新版 ECDSA 激活不需要环境变量。
    /// </summary>
    public static bool TryGetHmacKey(out byte[] key)
    {
        var envValue = Environment.GetEnvironmentVariable(EnvKeyName);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            key = Array.Empty<byte>();
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(envValue.Trim());
            if (bytes.Length != 32)
            {
                key = Array.Empty<byte>();
                return false;
            }

            key = bytes;
            return true;
        }
        catch (FormatException)
        {
            key = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>
    /// 解码后的 HMAC 密钥字节（32 字节）。从 KANBAN_HMAC_KEY 环境变量加载；未配置或非法时抛异常。
    /// 进程内缓存（Lazy 保证只解析一次）。
    /// </summary>
    public static byte[] HmacKey => _hmacKeyLazy.Value;

    private static readonly Lazy<byte[]> _hmacKeyLazy = new(LoadKey);

    private static byte[] LoadKey()
    {
        var envValue = Environment.GetEnvironmentVariable(EnvKeyName);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            throw new InvalidOperationException(
                $"未配置环境变量 {EnvKeyName}。HMAC 密钥必须通过环境变量注入（32 字节 Base64），禁止写入程序集（防反编译自签）。");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(envValue);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"环境变量 {EnvKeyName} 不是合法的 Base64 编码。请使用 32 字节随机密钥的 Base64 形式。");
        }

        if (bytes.Length != 32)
        {
            throw new InvalidOperationException(
                $"环境变量 {EnvKeyName} 长度非法（期望 32 字节 Base64，实际 {bytes.Length} 字节）。请重新生成密钥后配置。");
        }

        return bytes;
    }

    /// <summary>当前密钥来源描述（用于日志/调试）</summary>
    public static string KeySource
    {
        get
        {
            var envValue = Environment.GetEnvironmentVariable(EnvKeyName);
            return string.IsNullOrWhiteSpace(envValue)
                ? "未配置"
                : $"环境变量 {EnvKeyName}";
        }
    }

    /// <summary>激活码负载长度（字节）：5 机器码哈希 + 2 过期日期 = 7 字节</summary>
    public const int PayloadSize = 7;

    /// <summary>HMAC 标签长度（字节）：截断到 16 字节（128 位），提供 2^128 抗碰撞</summary>
    public const int TagSize = 16;

    /// <summary>完整激活码字节数：负载 + HMAC 标签 = 23 字节</summary>
    public const int TotalSize = PayloadSize + TagSize;

    /// <summary>Base32 编码后字符数：23 字节 → 37 字符</summary>
    public const int EncodedLength = 37;

    /// <summary>分组后的激活码字符数：37 字符 + 1 个校验位 = 38 字符</summary>
    public const int FormattedLength = 38;
}
