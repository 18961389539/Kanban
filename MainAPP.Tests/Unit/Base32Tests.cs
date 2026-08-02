using LicenseManager.Crypto;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// Base32 编解码单元测试：覆盖空输入、往返、分隔符清理、非法字符等场景。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class Base32Tests
{
    // ──────────── Encode: 基本编码 ────────────

    [Fact]
    public void Encode_EmptyInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Base32.Encode(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(new byte[] { 0x66 }, "MY")]                    // 1 字节
    [InlineData(new byte[] { 0x66, 0x6F }, "MZXQ")]            // 2 字节
    [InlineData(new byte[] { 0x66, 0x6F, 0x6F }, "MZXW6")]     // 3 字节
    [InlineData(new byte[] { 0x66, 0x6F, 0x6F, 0x62 }, "MZXW6YQ")]  // 4 字节
    [InlineData(new byte[] { 0x66, 0x6F, 0x6F, 0x62, 0x61 }, "MZXW6YTB")]  // 5 字节（完整块）
    public void Encode_KnownInputs_ReturnsExpected(byte[] input, string expected)
    {
        Assert.Equal(expected, Base32.Encode(input));
    }

    // ──────────── TryDecode: 往返测试 ────────────

    [Fact]
    public void TryDecode_EmptyInput_ReturnsTrueAndEmptyArray()
    {
        var result = Base32.TryDecode("", out var output);
        Assert.True(result);
        Assert.Empty(output);
    }

    [Theory]
    [InlineData(new byte[] { 0x66 })]
    [InlineData(new byte[] { 0x66, 0x6F })]
    [InlineData(new byte[] { 0x66, 0x6F, 0x6F })]
    [InlineData(new byte[] { 0x66, 0x6F, 0x6F, 0x62 })]
    [InlineData(new byte[] { 0x66, 0x6F, 0x6F, 0x62, 0x61 })]
    [InlineData(new byte[] { 0x00, 0xFF, 0xAA, 0x55, 0x12, 0x34, 0xAB, 0xCD })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void TryDecode_RoundTrip_ReturnsOriginalBytes(byte[] original)
    {
        var encoded = Base32.Encode(original);
        var result = Base32.TryDecode(encoded, out var decoded);
        Assert.True(result);
        Assert.Equal(original, decoded);
    }

    // ──────────── TryDecode: 分隔符与大小写 ────────────

    [Fact]
    public void TryDecode_IgnoresHyphensAndSpaces()
    {
        var original = new byte[] { 0x66, 0x6F, 0x6F, 0x62, 0x61 };
        var encoded = Base32.Encode(original);
        var withSeparators = $"{encoded[..4]}-{encoded[4..]}";

        var result = Base32.TryDecode(withSeparators, out var decoded);
        Assert.True(result);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void TryDecode_AcceptsLowercase()
    {
        var original = new byte[] { 0x66, 0x6F, 0x6F, 0x62, 0x61 };
        var encoded = Base32.Encode(original).ToLowerInvariant();

        var result = Base32.TryDecode(encoded, out var decoded);
        Assert.True(result);
        Assert.Equal(original, decoded);
    }

    // ──────────── TryDecode: 非法输入 ────────────

    [Theory]
    [InlineData("0")]              // 0 不在字母表
    [InlineData("1")]              // 1 不在字母表
    [InlineData("8")]              // 8 不在字母表
    [InlineData("!@#$%")]          // 特殊字符
    [InlineData("ABCD1")]          // 含非法字符
    public void TryDecode_InvalidChars_ReturnsFalse(string input)
    {
        var result = Base32.TryDecode(input, out _);
        Assert.False(result);
    }
}
