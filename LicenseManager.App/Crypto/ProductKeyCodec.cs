using System.Security.Cryptography;
using System.Text;
using LicenseManager.Models;

namespace LicenseManager.Crypto;

/// <summary>
/// 激活码编解码：将 { 机器码哈希, 过期日期, 签名类型 } 编码为产品密钥字符串。
/// </summary>
/// <remarks>
/// 旧 HMAC 激活码结构（23 字节）：
///   [0..4]  机器码哈希（5 字节，SHA256 截断）
///   [5..6]  过期日期（2 字节，自 2025-01-01 起的天数偏移；0xFFFF 表示永久）
///   [7..22] HMAC-SHA256(负载) 截断到 16 字节（128 位）
///
/// 新 ECDSA 激活码结构（72 字节）：
///   [0..6]  机器码哈希 + 过期日期
///   [7]     签名格式版本
///   [8..71] P-256 IEEE P1363 签名（64 字节）
///
/// 新激活码使用 8 字节签名载荷和 ECDSA 签名，客户端只需公钥即可验证；
/// 旧 HMAC 激活码继续由 TryDecode 兼容验证。
/// </remarks>
/// <remarks>
/// 不混淆：LicenseIssuer.* 工具直接引用此类，混淆重命名会导致外部程序集 TypeLoadException。
/// </remarks>
public static class ProductKeyCodec
{
    private const int MachineCodeHashSize = 5;
    private const byte EcdsaVersion = 0x01;
    private const int EcdsaPayloadSize = EmbeddedKey.PayloadSize + 1;
    private const int EcdsaTotalSize = EcdsaPayloadSize + LicenseSigningKey.SignatureSize;
    private const int EcdsaRawLength = (EcdsaTotalSize * 8 + 4) / 5 + 1;

    /// <summary>当前 ECDSA 激活码的未分组字符长度。</summary>
    public const int SignedRawLength = EcdsaRawLength;

    /// <summary>当前 ECDSA 激活码按每 5 个字符分组后的长度。</summary>
    public const int SignedFormattedLength = SignedRawLength + (SignedRawLength - 1) / 5;

    /// <summary>旧 HMAC 激活码按每 5 个字符分组后的长度。</summary>
    public const int LegacyFormattedLength = EmbeddedKey.FormattedLength + (EmbeddedKey.FormattedLength - 1) / 5;

    /// <summary>激活窗口允许的最大显示长度，覆盖旧 HMAC 与新 ECDSA 激活码。</summary>
    public const int MaxFormattedLength = SignedFormattedLength;

    /// <summary>永久授权的过期日期编码值</summary>
    public const ushort PermanentMarker = 0xFFFF;

    /// <summary>过期日期编码基准日（2025-01-01 UTC）</summary>
    public static readonly DateTime EpochUtc = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 编码旧版 HMAC 激活码。新激活码应使用 <see cref="EncodeSigned"/>。
    /// </summary>
    /// <param name="machineCodeHash">机器码哈希（5 字节）</param>
    /// <param name="expireDate">过期日期（UTC），null 表示永久</param>
    /// <returns>旧版 HMAC 格式化激活码。</returns>
    public static string Encode(byte[] machineCodeHash, DateTime? expireDate)
    {
        var payload = CreatePayload(machineCodeHash, expireDate);

        var tag = HmacValidator.ComputeTag(payload);
        var full = new byte[EmbeddedKey.TotalSize];
        Buffer.BlockCopy(payload, 0, full, 0, EmbeddedKey.PayloadSize);
        Buffer.BlockCopy(tag, 0, full, EmbeddedKey.PayloadSize, EmbeddedKey.TagSize);

        var encoded = Base32.Encode(full);  // 23 字节 → 37 字符
        var checkChar = ComputeCheckChar(encoded);
        var raw = encoded + checkChar;       // 25 字符
        return Format(raw);
    }

    /// <summary>使用签发端 ECDSA 私钥编码无需客户端密钥的激活码。</summary>
    public static string EncodeSigned(byte[] machineCodeHash, DateTime? expireDate)
    {
        if (machineCodeHash == null || machineCodeHash.Length != 5)
            throw new ArgumentException("机器码哈希必须为 5 字节", nameof(machineCodeHash));

        var payload = CreatePayload(machineCodeHash, expireDate);
        var signedPayload = new byte[EcdsaPayloadSize];
        Buffer.BlockCopy(payload, 0, signedPayload, 0, EmbeddedKey.PayloadSize);
        signedPayload[^1] = EcdsaVersion;

        using var signer = LicenseSigningKey.LoadSigner();
        var signature = signer.SignData(signedPayload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (signature.Length != LicenseSigningKey.SignatureSize)
            throw new InvalidOperationException("ECDSA 签名长度异常。");

        var full = new byte[EcdsaTotalSize];
        Buffer.BlockCopy(signedPayload, 0, full, 0, signedPayload.Length);
        Buffer.BlockCopy(signature, 0, full, signedPayload.Length, signature.Length);
        return FormatWithCheckDigit(Base32.Encode(full));
    }

    /// <summary>
    /// 解码并验证激活码：返回负载信息；验签失败返回 null。
    /// </summary>
    public static LicenseInfo? TryDecode(string input, string currentMachineCodeHash)
    {
        if (!TryParseRaw(input, out var raw))
            return null;

        // 移除校验位后解码
        var withoutCheck = raw[..^1];
        if (!Base32.TryDecode(withoutCheck, out var bytes))
            return null;

        if (bytes.Length == EcdsaTotalSize)
            return TryDecodeSigned(raw, withoutCheck, bytes, currentMachineCodeHash);

        if (bytes.Length != EmbeddedKey.TotalSize)
            return null;

        // 校验位验证
        var expectedCheck = ComputeCheckChar(withoutCheck);
        if (expectedCheck != raw[^1])
            return null;

        // 分离负载和标签
        var payload = bytes.AsSpan(0, EmbeddedKey.PayloadSize);
        var tag = bytes.AsSpan(EmbeddedKey.PayloadSize, EmbeddedKey.TagSize);

        // HMAC 验签
        if (!EmbeddedKey.TryGetHmacKey(out _))
            return null;
        var expectedTag = HmacValidator.ComputeTag(payload);
        if (!HmacValidator.ConstantTimeEquals(tag, expectedTag))
            return null;

        return DecodePayload(payload, raw, currentMachineCodeHash);
    }

    private static LicenseInfo? TryDecodeSigned(
        string raw,
        string withoutCheck,
        byte[] bytes,
        string currentMachineCodeHash)
    {
        var expectedCheck = ComputeCheckChar(withoutCheck);
        if (expectedCheck != raw[^1]) return null;

        var signedPayload = bytes.AsSpan(0, EcdsaPayloadSize);
        if (signedPayload[^1] != EcdsaVersion) return null;

        var signature = bytes.AsSpan(EcdsaPayloadSize, LicenseSigningKey.SignatureSize);
        using var verifier = LicenseSigningKey.CreateVerifier();
        if (!verifier.VerifyData(
                signedPayload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return null;
        }

        return DecodePayload(signedPayload[..EmbeddedKey.PayloadSize], raw, currentMachineCodeHash);
    }

    private static LicenseInfo? DecodePayload(
        ReadOnlySpan<byte> payload,
        string productKey,
        string currentMachineCodeHash)
    {
        var machineHashStr = Base32.Encode(payload[..5]);
        if (!string.Equals(machineHashStr, currentMachineCodeHash, StringComparison.OrdinalIgnoreCase))
            return null;

        var daysSinceEpoch = (ushort)((payload[5] << 8) | payload[6]);
        DateTime? expireDate = daysSinceEpoch == PermanentMarker
            ? null
            : EpochUtc.AddDays(daysSinceEpoch);

        return new LicenseInfo
        {
            MachineCodeHash = machineHashStr,
            ExpireDate = expireDate,
            ActivatedAt = DateTime.UtcNow,
            ProductKey = Format(productKey),
        };
    }

    private static byte[] CreatePayload(byte[] machineCodeHash, DateTime? expireDate)
    {
        if (machineCodeHash == null || machineCodeHash.Length != MachineCodeHashSize)
            throw new ArgumentException($"机器码哈希必须为 {MachineCodeHashSize} 字节", nameof(machineCodeHash));

        var payload = new byte[EmbeddedKey.PayloadSize];
        Buffer.BlockCopy(machineCodeHash, 0, payload, 0, MachineCodeHashSize);

        // 向上取整到天，确保有效至某日当天结束不会因时区偏移提前失效。
        var daysSinceEpoch = expireDate.HasValue
            ? (ushort)Math.Clamp(Math.Ceiling((expireDate.Value.ToUniversalTime() - EpochUtc).TotalDays), 0, PermanentMarker - 1)
            : PermanentMarker;
        payload[5] = (byte)(daysSinceEpoch >> 8);
        payload[6] = (byte)(daysSinceEpoch & 0xFF);
        return payload;
    }

    /// <summary>验证并解码 8 字符机器码。</summary>
    public static bool TryDecodeMachineCode(string input, out byte[] machineCodeHash)
    {
        var raw = RemoveSeparators(input);
        if (raw.Length == 8 && Base32.TryDecode(raw, out var decoded) && decoded.Length == MachineCodeHashSize)
        {
            machineCodeHash = decoded;
            return true;
        }

        machineCodeHash = Array.Empty<byte>();
        return false;
    }

    /// <summary>规范化激活码输入，供输入框显示使用。</summary>
    public static string NormalizeForDisplay(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var raw = RemoveSeparators(input);
        if (raw.Length > SignedRawLength)
            raw = raw[..SignedRawLength];

        return Format(raw);
    }

    private static string FormatWithCheckDigit(string encoded)
    {
        var raw = encoded + ComputeCheckChar(encoded);
        return Format(raw);
    }

    /// <summary>从用户输入中提取原始 Base32 字符（支持旧 HMAC 和新 ECDSA 长度）。</summary>
    public static bool TryParseRaw(string input, out string raw)
    {
        raw = string.Empty;
        if (string.IsNullOrEmpty(input)) return false;

        raw = RemoveSeparators(input);
        return raw.Length is EmbeddedKey.FormattedLength or EcdsaRawLength;
    }

    private static string RemoveSeparators(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>格式化为 5 字符一组（XXXXX-XXXXX-...）。</summary>
    public static string Format(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;

        var sb = new StringBuilder(raw.Length + raw.Length / 5);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    /// <summary>计算校验字符：所有字符 Base32 值之和对 32 取模，映射到字母表。</summary>
    private static char ComputeCheckChar(string encoded)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sum = 0;
        foreach (var c in encoded)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) idx = 0;
            sum += idx;
        }
        return alphabet[sum % 32];
    }
}
