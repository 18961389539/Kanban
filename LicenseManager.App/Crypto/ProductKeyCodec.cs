using System.Text;
using LicenseManager.Models;

namespace LicenseManager.Crypto;

/// <summary>
/// 激活码编解码：将 { 机器码哈希, 过期日期 } 编码为产品密钥字符串。
/// </summary>
/// <remarks>
/// 激活码结构（23 字节）：
///   [0..4]  机器码哈希（5 字节，SHA256 截断）
///   [5..6]  过期日期（2 字节，自 2025-01-01 起的天数偏移；0xFFFF 表示永久）
///   [7..22] HMAC-SHA256(负载) 截断到 16 字节（128 位）
///
/// Base32 编码后 37 字符，加 1 字符校验位（取所有字符 Base32 值之和对 32 取模映射到字母表），
/// 最终按 5 字符一组格式化（末组末位为校验位）。
/// </remarks>
/// <remarks>
/// 不混淆：LicenseIssuer.* 工具直接引用此类，混淆重命名会导致外部程序集 TypeLoadException。
/// </remarks>
public static class ProductKeyCodec
{
    /// <summary>永久授权的过期日期编码值</summary>
    public const ushort PermanentMarker = 0xFFFF;

    /// <summary>过期日期编码基准日（2025-01-01 UTC）</summary>
    public static readonly DateTime EpochUtc = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 编码激活码：将机器码哈希 + 过期日期 + HMAC 标签组合为 25 字符字符串。
    /// </summary>
    /// <param name="machineCodeHash">机器码哈希（5 字节）</param>
    /// <param name="expireDate">过期日期（UTC），null 表示永久</param>
    /// <returns>格式化激活码：XXXXX-XXXXX-XXXXX-XXXXX-XXXXX</returns>
    public static string Encode(byte[] machineCodeHash, DateTime? expireDate)
    {
        if (machineCodeHash == null || machineCodeHash.Length != 5)
            throw new ArgumentException("机器码哈希必须为 5 字节", nameof(machineCodeHash));

        var payload = new byte[EmbeddedKey.PayloadSize];
        Buffer.BlockCopy(machineCodeHash, 0, payload, 0, 5);

        // 向上取整到天：确保"有效至某日当天结束"不会被截断提前失效（时区偏移导致的分数天被抹掉是旧 bug）。
        var daysSinceEpoch = expireDate.HasValue
            ? (ushort)Math.Clamp(Math.Ceiling((expireDate.Value.ToUniversalTime() - EpochUtc).TotalDays), 0, PermanentMarker - 1)
            : PermanentMarker;
        payload[5] = (byte)(daysSinceEpoch >> 8);
        payload[6] = (byte)(daysSinceEpoch & 0xFF);

        var tag = HmacValidator.ComputeTag(payload);
        var full = new byte[EmbeddedKey.TotalSize];
        Buffer.BlockCopy(payload, 0, full, 0, EmbeddedKey.PayloadSize);
        Buffer.BlockCopy(tag, 0, full, EmbeddedKey.PayloadSize, EmbeddedKey.TagSize);

        var encoded = Base32.Encode(full);  // 15 字节 → 24 字符
        var checkChar = ComputeCheckChar(encoded);
        var raw = encoded + checkChar;       // 25 字符
        return Format(raw);
    }

    /// <summary>
    /// 解码并验证激活码：返回负载信息；验签失败返回 null。
    /// </summary>
    public static LicenseInfo? TryDecode(string input, string currentMachineCodeHash)
    {
        if (!TryParseRaw(input, out var raw) || raw.Length != EmbeddedKey.FormattedLength)
            return null;

        // 移除校验位后解码
        var withoutCheck = raw[..^1];
        if (!Base32.TryDecode(withoutCheck, out var bytes) || bytes.Length != EmbeddedKey.TotalSize)
            return null;

        // 校验位验证
        var expectedCheck = ComputeCheckChar(withoutCheck);
        if (expectedCheck != raw[^1])
            return null;

        // 分离负载和标签
        var payload = bytes.AsSpan(0, EmbeddedKey.PayloadSize);
        var tag = bytes.AsSpan(EmbeddedKey.PayloadSize, EmbeddedKey.TagSize);

        // HMAC 验签
        var expectedTag = HmacValidator.ComputeTag(payload);
        if (!HmacValidator.ConstantTimeEquals(tag, expectedTag))
            return null;

        // 解析机器码哈希
        var machineHashStr = Base32.Encode(payload[..5]);

        // 解析过期日期
        var daysSinceEpoch = (ushort)((payload[5] << 8) | payload[6]);
        DateTime? expireDate = daysSinceEpoch == PermanentMarker
            ? null
            : EpochUtc.AddDays(daysSinceEpoch);

        // 机器码绑定校验
        if (!string.Equals(machineHashStr, currentMachineCodeHash, StringComparison.OrdinalIgnoreCase))
            return null;

        return new LicenseInfo
        {
            MachineCodeHash = machineHashStr,
            ExpireDate = expireDate,
            ActivatedAt = DateTime.UtcNow,
            ProductKey = Format(raw),
        };
    }

    /// <summary>从用户输入中提取原始 Base32 字符（25 字符，无分隔符，已转大写）。</summary>
    public static bool TryParseRaw(string input, out string raw)
    {
        raw = string.Empty;
        if (string.IsNullOrEmpty(input)) return false;

        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c == '-' || c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        raw = sb.ToString();
        return raw.Length == EmbeddedKey.FormattedLength;
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
