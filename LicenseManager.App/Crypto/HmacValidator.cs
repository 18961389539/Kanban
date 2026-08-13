using System.Security.Cryptography;

namespace LicenseManager.Crypto;

/// <summary>
/// HMAC-SHA256 签发与验证：对激活码负载生成截断标签，客户端用同一密钥验证。
/// 不混淆：LicenseIssuer.* 工具直接引用此类，混淆重命名会导致外部程序集 TypeLoadException。
/// </summary>
public static class HmacValidator
{
    /// <summary>对负载计算 HMAC-SHA256，截断到 <see cref="EmbeddedKey.TagSize"/> 字节。</summary>
    public static byte[] ComputeTag(ReadOnlySpan<byte> payload)
    {
        var fullTag = HMACSHA256.HashData(EmbeddedKey.HmacKey, payload);
        var truncated = new byte[EmbeddedKey.TagSize];
        fullTag.AsSpan(0, EmbeddedKey.TagSize).CopyTo(truncated);
        return truncated;
    }

    /// <summary>常量时间比较两个字节序列，防止时序攻击。</summary>
    public static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
