using System.Text;

namespace LicenseManager.Crypto;

/// <summary>
/// Base32 编解码（RFC 4648 字母表，不含填充符）。
/// 用于将激活码二进制负载编码为可人工输入的字符序列。
/// 字母表：A-Z 2-7（共 32 字符），剔除 0/1/O/I 避免视觉混淆。
/// 不混淆：LicenseIssuer.* 工具直接引用此类，混淆重命名会导致外部程序集 TypeLoadException。
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int BitsPerChar = 5;
    private const int BytesPerBlock = 5;
    private const int CharsPerBlock = 8;

    /// <summary>
    /// 将字节数组编码为 Base32 字符串（不含填充符）。
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;

        var outputLen = (bytes.Length * 8 + BitsPerChar - 1) / BitsPerChar;
        var sb = new StringBuilder(outputLen);

        var buffer = 0;
        var bitsLeft = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= BitsPerChar)
            {
                var index = (buffer >> (bitsLeft - BitsPerChar)) & 0x1F;
                sb.Append(Alphabet[index]);
                bitsLeft -= BitsPerChar;
            }
        }
        if (bitsLeft > 0)
        {
            var index = (buffer << (BitsPerChar - bitsLeft)) & 0x1F;
            sb.Append(Alphabet[index]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 将 Base32 字符串解码为字节数组。忽略大小写、空格、连字符。
    /// 解码失败（含非法字符）返回 false。
    /// </summary>
    public static bool TryDecode(string input, out byte[] output)
    {
        output = Array.Empty<byte>();
        if (string.IsNullOrEmpty(input)) return true;

        // 清理输入：转大写、移除分隔符和空格
        var cleaned = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c == '-' || c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
            var upper = char.ToUpperInvariant(c);
            cleaned.Append(upper);
        }
        var cleanStr = cleaned.ToString();
        if (cleanStr.Length == 0) return true;

        // 反向映射表
        Span<int> reverseMap = stackalloc int[128];
        reverseMap.Fill(-1);
        for (var i = 0; i < Alphabet.Length; i++)
            reverseMap[Alphabet[i]] = i;

        var outputLen = cleanStr.Length * BitsPerChar / 8;
        var result = new byte[outputLen];
        var buffer = 0;
        var bitsLeft = 0;
        var outIdx = 0;

        foreach (var c in cleanStr)
        {
            if (c >= 128 || reverseMap[c] < 0) return false;
            buffer = (buffer << BitsPerChar) | reverseMap[c];
            bitsLeft += BitsPerChar;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                result[outIdx++] = (byte)((buffer >> bitsLeft) & 0xFF);
            }
        }
        output = result;
        return outIdx == outputLen;
    }
}
