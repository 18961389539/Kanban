using System.Globalization;
using System.Windows.Controls;
using MainAPP.Converters;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 两个 WPF ValidationRule 的单元测试：
/// - PlcAddressValidationRule：按期望类型（DWord/MBit）校验 PLC 地址，空值放行。
/// - IntegerRangeValidationRule：整数范围校验，空/非整数/越界分别给出对应错误信息。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ValidationRulesTests
{
    // ───────────── PlcAddressValidationRule ─────────────

    [Fact]
    public void PlcAddress_Empty_IsValid()
    {
        var rule = new PlcAddressValidationRule { ExpectedType = PlcAddressType.DWord };
        var result = rule.Validate(null, CultureInfo.InvariantCulture);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("D100", PlcAddressType.DWord)]
    [InlineData("D0", PlcAddressType.DWord)]
    [InlineData("M10", PlcAddressType.MBit)]
    [InlineData("M1", PlcAddressType.MBit)]
    public void PlcAddress_CorrectType_IsValid(string address, PlcAddressType type)
    {
        var rule = new PlcAddressValidationRule { ExpectedType = type };
        var result = rule.Validate(address, CultureInfo.InvariantCulture);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void PlcAddress_TypeMismatch_IsInvalid()
    {
        var rule = new PlcAddressValidationRule { ExpectedType = PlcAddressType.MBit };
        var result = rule.Validate("D100", CultureInfo.InvariantCulture);
        Assert.False(result.IsValid);
        Assert.Contains("期望", result.ErrorContent?.ToString());
    }

    [Fact]
    public void PlcAddress_UnrecognizedFormat_IsInvalid()
    {
        var rule = new PlcAddressValidationRule { ExpectedType = PlcAddressType.DWord };
        var result = rule.Validate("xyz", CultureInfo.InvariantCulture);
        Assert.False(result.IsValid);
        Assert.Contains("无法识别", result.ErrorContent?.ToString());
    }

    // ───────────── IntegerRangeValidationRule ─────────────

    [Fact]
    public void IntegerRange_Empty_IsInvalid()
    {
        var rule = new IntegerRangeValidationRule { FieldName = "端口", MinValue = 1, MaxValue = 65535 };
        var result = rule.Validate("", CultureInfo.InvariantCulture);
        Assert.False(result.IsValid);
        Assert.Contains("不能为空", result.ErrorContent?.ToString());
    }

    [Fact]
    public void IntegerRange_NonInteger_IsInvalid()
    {
        var rule = new IntegerRangeValidationRule { FieldName = "端口" };
        var result = rule.Validate("abc", CultureInfo.InvariantCulture);
        Assert.False(result.IsValid);
        Assert.Contains("必须是整数", result.ErrorContent?.ToString());
    }

    [Fact]
    public void IntegerRange_OutOfRange_IsInvalid()
    {
        var rule = new IntegerRangeValidationRule { FieldName = "端口", MinValue = 1, MaxValue = 10 };
        var result = rule.Validate("15", CultureInfo.InvariantCulture);
        Assert.False(result.IsValid);
        Assert.Contains("之间", result.ErrorContent?.ToString());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("1")]
    [InlineData("10")]
    public void IntegerRange_InRange_IsValid(string text)
    {
        var rule = new IntegerRangeValidationRule { FieldName = "端口", MinValue = 1, MaxValue = 10 };
        var result = rule.Validate(text, CultureInfo.InvariantCulture);
        Assert.True(result.IsValid);
    }
}
