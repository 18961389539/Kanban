using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// PLC 地址解析器单元测试
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class PlcAddressParserTests
{
    // ──────────── Parse: 合法 D 地址 ────────────

    [Theory]
    [InlineData("D100", "D100")]
    [InlineData("D12001", "D12001")]
    [InlineData("d100", "D100")]       // 小写规范化
    [InlineData("D1", "D1")]         // 最小数字
    [InlineData("  D100  ", "D100")]   // 带空白
    public void Parse_ValidDWord_ReturnsValid(string address, string expectedOriginal)
    {
        var result = PlcAddressParser.Parse(address);
        Assert.True(result.IsValid);
        Assert.Equal(PlcAddressType.DWord, result.Type);
        Assert.Equal(expectedOriginal, result.Original);
    }

    // ──────────── Parse: 合法 M 地址 ────────────

    [Theory]
    [InlineData("M100", "M100")]
    [InlineData("M10001", "M10001")]
    [InlineData("m100", "M100")]
    [InlineData("  M100  ", "M100")]
    public void Parse_ValidMBit_ReturnsValid(string address, string expectedOriginal)
    {
        var result = PlcAddressParser.Parse(address);
        Assert.True(result.IsValid);
        Assert.Equal(PlcAddressType.MBit, result.Type);
        Assert.Equal(expectedOriginal, result.Original);
    }

    // ──────────── Parse: 非法地址 ────────────

    [Theory]
    [InlineData("")]              // 空字符串
    [InlineData("   ")]           // 全空白
    [InlineData(null)]            // null
    [InlineData("100")]           // 无字母前缀
    [InlineData("D")]             // 无数字
    [InlineData("D100A")]         // 字母混在数字里
    [InlineData("X100")]          // 不支持的前缀
    [InlineData("DM100")]         // 双字母前缀
    [InlineData("D-100")]         // 负号
    public void Parse_Invalid_ReturnsInvalidWithError(string? address)
    {
        var result = PlcAddressParser.Parse(address);
        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // ──────────── IsDWord / IsMBit ────────────

    [Theory]
    [InlineData("D100", true)]
    [InlineData("M100", false)]
    [InlineData("X100", false)]
    [InlineData("", false)]
    public void IsDWord_ReturnsExpected(string address, bool expected)
    {
        Assert.Equal(expected, PlcAddressParser.IsDWord(address));
    }

    [Theory]
    [InlineData("M100", true)]
    [InlineData("D100", false)]
    [InlineData("X100", false)]
    [InlineData("", false)]
    public void IsMBit_ReturnsExpected(string address, bool expected)
    {
        Assert.Equal(expected, PlcAddressParser.IsMBit(address));
    }

    [Fact]
    public void Parse_Null_ReturnsInvalidWithEmptyOriginal()
    {
        var result = PlcAddressParser.Parse(null);
        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, result.Original);
    }

    // ──────────── PlcAddressParseResult 工厂方法（直接覆盖 struct 本身）────────────

    [Fact]
    public void PlcAddressParseResult_Valid_Factory_SetsPropertiesAndTrims()
    {
        var r = PlcAddressParseResult.Valid("  D12001  ", PlcAddressType.DWord);
        Assert.True(r.IsValid);
        Assert.Equal(PlcAddressType.DWord, r.Type);
        Assert.Equal("D12001", r.Original);          // 去首尾空白
        Assert.Null(r.ErrorMessage);                 // 有效结果无错误信息（工厂未赋值，默认为 null）
    }

    [Fact]
    public void PlcAddressParseResult_Invalid_Factory_TrimsOriginalAndSetsMessage()
    {
        var r = PlcAddressParseResult.Invalid("  M999  ", "地址无法识别");
        Assert.False(r.IsValid);
        Assert.Equal("M999", r.Original);             // 去首尾空白
        Assert.Equal("地址无法识别", r.ErrorMessage);
    }

    [Fact]
    public void PlcAddressParseResult_Invalid_WithNullOriginal_YieldsEmptyOriginal()
    {
        // Invalid 内部对 null 做了 address?.Trim() ?? string.Empty 保护
        var r = PlcAddressParseResult.Invalid(null!, "地址不能为空");
        Assert.False(r.IsValid);
        Assert.Equal(string.Empty, r.Original);
        Assert.Equal("地址不能为空", r.ErrorMessage);
    }

    [Theory]
    [InlineData("X99")]
    [InlineData("D-100")]
    [InlineData("DM100")]
    public void Parse_UnrecognizedFormat_ErrorMessageContainsTrimmedOriginal(string address)
    {
        var r = PlcAddressParser.Parse(address);
        Assert.False(r.IsValid);
        Assert.Contains(address.ToUpperInvariant(), r.ErrorMessage);
    }

    [Theory]
    [InlineData("D100", PlcAddressType.DWord)]
    [InlineData("M100", PlcAddressType.MBit)]
    public void Parse_ResultMatchesValidFactory(string address, PlcAddressType expected)
    {
        // 跨构造路径一致性：Parse 内部 new 的 struct 与工厂产出应完全一致
        var fromParse = PlcAddressParser.Parse(address);
        var fromFactory = PlcAddressParseResult.Valid(address, expected);
        Assert.Equal(fromParse.IsValid, fromFactory.IsValid);
        Assert.Equal(fromParse.Type, fromFactory.Type);
        Assert.Equal(fromParse.Original, fromFactory.Original);
    }
}
