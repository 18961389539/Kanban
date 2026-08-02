using LicenseManager.Crypto;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// HMAC 验证器单元测试：覆盖签名生成、常量时间比较、篡改检测。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class HmacValidatorTests
{
    // ──────────── ComputeTag: 一致性 ────────────

    [Fact]
    public void ComputeTag_SamePayload_ReturnsSameTag()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

        var tag1 = HmacValidator.ComputeTag(payload);
        var tag2 = HmacValidator.ComputeTag(payload);

        Assert.Equal(tag1, tag2);
    }

    [Fact]
    public void ComputeTag_DifferentPayload_ReturnsDifferentTag()
    {
        var payload1 = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var payload2 = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x08 };  // 仅末位不同

        var tag1 = HmacValidator.ComputeTag(payload1);
        var tag2 = HmacValidator.ComputeTag(payload2);

        Assert.NotEqual(tag1, tag2);
    }

    [Fact]
    public void ComputeTag_ReturnsCorrectLength()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        var tag = HmacValidator.ComputeTag(payload);

        Assert.Equal(EmbeddedKey.TagSize, tag.Length);
    }

    [Fact]
    public void ComputeTag_EmptyPayload_ReturnsNonEmptyTag()
    {
        var tag = HmacValidator.ComputeTag(Array.Empty<byte>());

        Assert.Equal(EmbeddedKey.TagSize, tag.Length);
        Assert.NotEmpty(tag);
    }

    // ──────────── ConstantTimeEquals ────────────

    [Fact]
    public void ConstantTimeEquals_SameBytes_ReturnsTrue()
    {
        var a = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var b = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        Assert.True(HmacValidator.ConstantTimeEquals(a, b));
    }

    [Fact]
    public void ConstantTimeEquals_DifferentBytes_ReturnsFalse()
    {
        var a = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var b = new byte[] { 0x01, 0x02, 0x03, 0x05 };

        Assert.False(HmacValidator.ConstantTimeEquals(a, b));
    }

    [Fact]
    public void ConstantTimeEquals_DifferentLength_ReturnsFalse()
    {
        var a = new byte[] { 0x01, 0x02, 0x03 };
        var b = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        Assert.False(HmacValidator.ConstantTimeEquals(a, b));
    }

    [Fact]
    public void ConstantTimeEquals_EmptyArrays_ReturnsTrue()
    {
        Assert.True(HmacValidator.ConstantTimeEquals(Array.Empty<byte>(), Array.Empty<byte>()));
    }
}
