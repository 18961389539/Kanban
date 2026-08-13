using System.Security.Cryptography;
using System.Text;

namespace Kanban.Core.Services;

/// <summary>
/// 密码哈希服务：SHA256 + 随机 salt。
/// 遵守项目硬约束"密码不可硬编码，使用配置 + SHA256+salt 哈希"。
/// 哈希格式：{saltBase64}:{hashBase64}，salt 为 16 字节随机数。
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16; // 128 bit
    private const int HashSize = 32; // 256 bit (SHA256)

    /// <summary>对明文密码生成 salt+hash 字符串（格式：saltBase64:hashBase64）。</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var salted = new byte[salt.Length + passwordBytes.Length];
        salt.CopyTo(salted, 0);
        passwordBytes.CopyTo(salted, salt.Length);
        var hash = SHA256.HashData(salted);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>验证明文密码是否匹配已存储的哈希。常量时间比较防止时序攻击。</summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        var parts = storedHash.Split(':');
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var salted = new byte[salt.Length + passwordBytes.Length];
            salt.CopyTo(salted, 0);
            passwordBytes.CopyTo(salted, salt.Length);
            var actual = SHA256.HashData(salted);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
