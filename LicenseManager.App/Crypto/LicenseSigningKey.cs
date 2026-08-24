using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LicenseManager.Crypto;

/// <summary>
/// 正式授权的非对称签名密钥边界。
/// MainAPP 只包含公钥；签发端私钥保存在签发机的当前用户目录，不随发布包分发。
/// </summary>
public static class LicenseSigningKey
{
    private const string PrivateKeyFileName = "license-signing-key.pem";

    // SubjectPublicKeyInfo 编码的 P-256 公钥。公钥可以公开，私钥绝不写入客户端。
    private const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQSmmWlNpsQ2UnVi90cKUvAOmdAy1CbynqbmzorrwwrMwsAKMVIo0pPODqNJducHfszlv9nvovp+N4EXJTULmnQ==";

    /// <summary>P-256 IEEE P1363 签名长度（r 和 s 各 32 字节）。</summary>
    public const int SignatureSize = 64;

    /// <summary>创建仅包含内置公钥的验签器。</summary>
    public static ECDsa CreateVerifier()
    {
        var verifier = ECDsa.Create();
        try
        {
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
            return verifier;
        }
        catch
        {
            verifier.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 从签发机当前用户的 AppData 加载私钥。
    /// 私钥只用于 LicenseIssuer，不应复制到 MainAPP 或发布包。
    /// </summary>
    public static ECDsa LoadSigner()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kanban",
            PrivateKeyFileName);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"签发私钥不存在：{path}。请先在签发机初始化 license-signing-key.pem。");
        }

        var signer = ECDsa.Create();
        try
        {
            signer.ImportFromPem(File.ReadAllText(path, Encoding.ASCII));

            var expectedPublicKey = Convert.FromBase64String(PublicKeyBase64);
            var actualPublicKey = signer.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(actualPublicKey, expectedPublicKey))
            {
                throw new InvalidOperationException(
                    "签发私钥与客户端内置公钥不匹配。请恢复正确的 license-signing-key.pem，或重新发布包含匹配公钥的客户端。");
            }

            return signer;
        }
        catch (Exception ex)
        {
            signer.Dispose();
            throw new InvalidOperationException($"签发私钥无法加载：{path}。", ex);
        }
    }
}