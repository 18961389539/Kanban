using System.Security.Cryptography;
using System.Text;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 密码哈希服务：PBKDF2-SHA256 + 随机 salt（100,000 迭代，可配置）。
/// 哈希格式：pbkdf2${iterations}${saltBase64}${hashBase64}
/// 兼容旧版 SHA256 格式（saltBase64:hashBase64），验证旧哈希时仍可通过（迁移场景）。
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16; // 128 bit
    private const int HashSize = 32; // 256 bit
    private const int Iterations = 100_000;

    private const string Pbkdf2Prefix = "pbkdf2";

    /// <summary>对明文密码生成 PBKDF2 哈希字符串（格式：pbkdf2${iterations}${salt}${hash}）。</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Pbkdf2Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>验证明文密码是否匹配存储哈希（自动识别 PBKDF2 与旧版 SHA256）。常量时间比较防时序攻击。</summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;

        if (storedHash.StartsWith(Pbkdf2Prefix + "$", StringComparison.Ordinal))
            return VerifyPbkdf2(password, storedHash);

        return VerifyLegacySha256(password, storedHash);
    }

    private static bool VerifyPbkdf2(string password, string storedHash)
    {
        // 格式：pbkdf2${iterations}${saltBase64}${hashBase64}
        var parts = storedHash.Split('$');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyLegacySha256(string password, string storedHash)
    {
        // 旧版格式：saltBase64:hashBase64（SHA256(salt || password)）
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
