using LicenseManager.Crypto;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 激活码编解码单元测试：覆盖往返、格式化、机器码绑定、过期日期、篡改检测。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
[Collection("LicenseEnvironment")]
public class ProductKeyCodecTests
{
    // ──────────── 编解码往返 ────────────

    [Fact]
    public void Encode_Then_TryDecode_Permanent_RoundTrip()
    {
        var machineHash = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };
        var machineHashStr = Base32.Encode(machineHash);

        var productKey = ProductKeyCodec.Encode(machineHash, expireDate: null);

        var info = ProductKeyCodec.TryDecode(productKey, machineHashStr);

        Assert.NotNull(info);
        Assert.Equal(machineHashStr, info!.MachineCodeHash);
        Assert.Null(info.ExpireDate);  // 永久授权
        Assert.True(info.IsPermanent);
        Assert.False(info.IsExpired);
    }

    [Fact]
    public void TryDecode_SignedLicense_WithoutHmacKey_ReturnsInfo()
    {
        const string signedKey = "SBWYV-CNJ77-7QDAA-M5XER-234AE-QDOUB-2746N-YEXYO-L4T56-ITNF2-Q6Z66-47VGD-XBUGV-C2DXG-PGXGV-EULBX-TWVDS-JEWM6-U3I7Y-AO5L3-I3BM6-TV2GT-2UQOA-QY";
        var previousKey = Environment.GetEnvironmentVariable(EmbeddedKey.EnvKeyName);

        try
        {
            Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, null);

            var info = ProductKeyCodec.TryDecode(signedKey, "SBWYVCNJ");

            Assert.NotNull(info);
            Assert.Equal("SBWYVCNJ", info!.MachineCodeHash);
            Assert.Null(info.ExpireDate);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmbeddedKey.EnvKeyName, previousKey);
        }
    }

    [Fact]
    public void Encode_Then_TryDecode_WithExpiry_RoundTrip()
    {
        var machineHash = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var machineHashStr = Base32.Encode(machineHash);
        var expireDate = new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        var productKey = ProductKeyCodec.Encode(machineHash, expireDate);

        var info = ProductKeyCodec.TryDecode(productKey, machineHashStr);

        Assert.NotNull(info);
        Assert.Equal(machineHashStr, info!.MachineCodeHash);
        Assert.NotNull(info.ExpireDate);
        Assert.False(info.IsExpired);
        // 过期日期精度为天，比较时去掉时间部分
        Assert.Equal(expireDate.Date, info.ExpireDate.Value.Date);
    }

    // ──────────── 格式化 ────────────

    [Fact]
    public void Encode_ReturnsCorrectFormat()
    {
        var machineHash = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };

        var productKey = ProductKeyCodec.Encode(machineHash, expireDate: null);

        // 38 字符按 5 字符分组（末组 3 字符），共 8 组、7 个分隔符
        Assert.Equal(EmbeddedKey.FormattedLength, productKey.Replace("-", "").Length);
        var groups = productKey.Split('-');
        Assert.Equal(8, groups.Length);
        for (var i = 0; i < groups.Length - 1; i++)
            Assert.Equal(5, groups[i].Length);
        Assert.Equal(3, groups[^1].Length);
    }

    [Fact]
    public void TryParseRaw_StripsSeparatorsAndUpperCases()
    {
        var input = "abcde-fghij-klmno-pqrst-uvwxy-z2345-67abc-def";

        var result = ProductKeyCodec.TryParseRaw(input, out var raw);

        Assert.True(result);
        Assert.Equal(EmbeddedKey.FormattedLength, raw.Length);
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEF", raw);
        Assert.DoesNotContain("-", raw);
    }

    [Fact]
    public void TryParseRaw_TooShort_ReturnsFalse()
    {
        var result = ProductKeyCodec.TryParseRaw("ABCDE", out _);
        Assert.False(result);
    }

    [Fact]
    public void NormalizeForDisplay_RemovesWhitespaceAndLimitsLength()
    {
        var input = "abcde\r\nfghij" + new string('k', ProductKeyCodec.SignedRawLength);

        var formatted = ProductKeyCodec.NormalizeForDisplay(input);

        Assert.Equal(ProductKeyCodec.SignedFormattedLength, formatted.Length);
        Assert.DoesNotContain('\r', formatted);
        Assert.DoesNotContain('\n', formatted);
        Assert.Equal(formatted, formatted.ToUpperInvariant());
    }

    [Fact]
    public void TryDecodeMachineCode_CanonicalizesValidCode()
    {
        var machineHash = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };

        var result = ProductKeyCodec.TryDecodeMachineCode(
            Base32.Encode(machineHash).ToLowerInvariant(),
            out var decoded);

        Assert.True(result);
        Assert.Equal(machineHash, decoded);
    }

    [Fact]
    public void TryDecodeMachineCode_RejectsWrongLength()
    {
        var result = ProductKeyCodec.TryDecodeMachineCode("ABCDE", out _);

        Assert.False(result);
    }

    [Fact]
    public void Format_CorrectlyGroups()
    {
        var raw = "ABCDEFGHIJKLMNOPQRSTUVWXY";
        var formatted = ProductKeyCodec.Format(raw);
        Assert.Equal("ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", formatted);
    }

    // ──────────── 机器码绑定 ────────────

    [Fact]
    public void TryDecode_WrongMachineCode_ReturnsNull()
    {
        var machineHash1 = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };
        var machineHash2 = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9B };  // 仅末位不同
        var machineHashStr1 = Base32.Encode(machineHash1);
        var machineHashStr2 = Base32.Encode(machineHash2);

        var productKey = ProductKeyCodec.Encode(machineHash1, expireDate: null);

        // 用错误的机器码验证 → 应失败
        var info = ProductKeyCodec.TryDecode(productKey, machineHashStr2);
        Assert.Null(info);
    }

    [Fact]
    public void TryDecode_CorrectMachineCode_ReturnsInfo()
    {
        var machineHash = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };
        var machineHashStr = Base32.Encode(machineHash);

        var productKey = ProductKeyCodec.Encode(machineHash, expireDate: null);

        var info = ProductKeyCodec.TryDecode(productKey, machineHashStr);
        Assert.NotNull(info);
    }

    // ──────────── 篡改检测 ────────────

    [Fact]
    public void TryDecode_TamperedKey_ReturnsNull()
    {
        var machineHash = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };
        var machineHashStr = Base32.Encode(machineHash);

        var productKey = ProductKeyCodec.Encode(machineHash, expireDate: null);

        // 篡改一个字符（不影响校验位的合法 Base32 字符）
        var tampered = productKey.ToCharArray();
        // 找到第一个非分隔符且非校验位的字符替换
        for (var i = 0; i < tampered.Length - 1; i++)
        {
            if (tampered[i] == '-') continue;
            // 替换为不同的合法字符
            var newChar = tampered[i] == 'A' ? 'B' : 'A';
            tampered[i] = newChar;
            break;
        }
        var tamperedKey = new string(tampered);

        var info = ProductKeyCodec.TryDecode(tamperedKey, machineHashStr);
        Assert.Null(info);  // HMAC 验签或校验位失败
    }

    [Fact]
    public void TryDecode_TamperedCheckDigit_ReturnsNull()
    {
        var machineHash = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };
        var machineHashStr = Base32.Encode(machineHash);

        var productKey = ProductKeyCodec.Encode(machineHash, expireDate: null);

        // 篡改最后一位（校验位）
        var lastChar = productKey[^1];
        var newLastChar = lastChar == 'A' ? 'B' : 'A';
        var tamperedKey = productKey[..^1] + newLastChar;

        var info = ProductKeyCodec.TryDecode(tamperedKey, machineHashStr);
        Assert.Null(info);
    }

    // ──────────── 边界情况 ────────────

    [Fact]
    public void TryDecode_EmptyInput_ReturnsNull()
    {
        var info = ProductKeyCodec.TryDecode("", "ABCDEFGH");
        Assert.Null(info);
    }

    [Fact]
    public void TryDecode_NullInput_ReturnsNull()
    {
        var info = ProductKeyCodec.TryDecode(null!, "ABCDEFGH");
        Assert.Null(info);
    }

    [Fact]
    public void TryDecode_TooShort_ReturnsNull()
    {
        var info = ProductKeyCodec.TryDecode("ABCDE", "ABCDEFGH");
        Assert.Null(info);
    }

    [Fact]
    public void Encode_InvalidMachineHashLength_Throws()
    {
        var invalidHash = new byte[] { 0x01, 0x02, 0x03 };  // 3 字节，应为 5

        Assert.Throws<ArgumentException>(() =>
            ProductKeyCodec.Encode(invalidHash, expireDate: null));
    }

    [Fact]
    public void Encode_AllZeroHash_Succeeds()
    {
        var machineHash = new byte[] { 0, 0, 0, 0, 0 };
        var machineHashStr = Base32.Encode(machineHash);

        var productKey = ProductKeyCodec.Encode(machineHash, expireDate: null);
        var info = ProductKeyCodec.TryDecode(productKey, machineHashStr);

        Assert.NotNull(info);
    }

    [Fact]
    public void Encode_AllFFHash_Succeeds()
    {
        var machineHash = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var machineHashStr = Base32.Encode(machineHash);

        var productKey = ProductKeyCodec.Encode(machineHash, expireDate: null);
        var info = ProductKeyCodec.TryDecode(productKey, machineHashStr);

        Assert.NotNull(info);
    }
}
