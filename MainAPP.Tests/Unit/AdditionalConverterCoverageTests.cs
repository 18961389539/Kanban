using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MainAPP.Converters;
using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class AdditionalConverterCoverageTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(DefectCategory.Appearance, "外观")]
    [InlineData(DefectCategory.Dimension, "尺寸")]
    [InlineData(DefectCategory.Function, "功能")]
    [InlineData(DefectCategory.Packaging, "包装")]
    [InlineData(DefectCategory.Other, "其他")]
    public void DefectCategoryToText_FormatsKnownCategory(DefectCategory category, string expected)
    {
        var converter = new DefectCategoryToTextConverter();
        Assert.Equal(expected, converter.Convert(category, typeof(string), null!, Culture));
    }

    [Fact]
    public void DefectCategoryToText_HandlesUnknownAndInvalidValues()
    {
        var converter = new DefectCategoryToTextConverter();
        Assert.Equal("123", converter.Convert((DefectCategory)123, typeof(string), null!, Culture));
        Assert.Equal(string.Empty, converter.Convert("Other", typeof(string), null!, Culture));
    }

    [Fact]
    public void AlarmLevelToText_HandlesInvalidValueAndConvertBack()
    {
        var converter = new AlarmLevelToTextConverter();
        Assert.Equal(string.Empty, converter.Convert("High", typeof(string), null!, Culture));
        Assert.Throws<NotImplementedException>(() =>
            converter.ConvertBack("高", typeof(AlarmLevel), null!, Culture));
    }

    [Theory]
    [InlineData(null, null, "Collapsed")]
    [InlineData("value", null, "Visible")]
    [InlineData(null, "Invert", "Visible")]
    [InlineData("value", "Invert", "Collapsed")]
    public void NullToVisibility_HandlesDefaultAndInvertedModes(object? value, string? parameter, string expected)
    {
        var converter = new NullToVisibilityConverter();
        var result = converter.Convert(value!, typeof(Visibility), parameter!, Culture);
        Assert.Equal(expected == "Visible" ? Visibility.Visible : Visibility.Collapsed, result);
    }

    [Fact]
    public void NullToVisibility_ConvertBackReturnsDoNothing()
    {
        var converter = new NullToVisibilityConverter();
        Assert.Equal(System.Windows.Data.Binding.DoNothing,
            converter.ConvertBack(Visibility.Visible, typeof(object), null!, Culture));
    }

    [Theory]
    [InlineData("", "数量", "数量不能为空")]
    [InlineData("abc", "数量", "数量必须是整数")]
    [InlineData("9", "数量", "数量必须在 10-20 之间")]
    [InlineData("21", "数量", "数量必须在 10-20 之间")]
    public void IntegerRangeValidation_RejectsInvalidInput(string value, string fieldName, string expected)
    {
        var rule = new IntegerRangeValidationRule { MinValue = 10, MaxValue = 20, FieldName = fieldName };
        var result = rule.Validate(value, Culture);
        Assert.False(result.IsValid);
        Assert.Equal(expected, result.ErrorContent);
    }

    [Fact]
    public void IntegerRangeValidation_AcceptsValueInRange()
    {
        var rule = new IntegerRangeValidationRule { MinValue = 10, MaxValue = 20 };
        Assert.Equal(ValidationResult.ValidResult, rule.Validate("15", Culture));
    }

    [Theory]
    [InlineData("", PlcAddressType.DWord)]
    [InlineData("D100", PlcAddressType.DWord)]
    [InlineData("M100", PlcAddressType.MBit)]
    public void PlcAddressValidation_AcceptsOptionalAndMatchingAddresses(string address, PlcAddressType expectedType)
    {
        var rule = new PlcAddressValidationRule { ExpectedType = expectedType };
        Assert.True(rule.Validate(address, Culture).IsValid);
    }

    [Theory]
    [InlineData("X100", PlcAddressType.DWord)]
    [InlineData("D100", PlcAddressType.MBit)]
    [InlineData("M100", PlcAddressType.DWord)]
    public void PlcAddressValidation_RejectsInvalidOrWrongType(string address, PlcAddressType expectedType)
    {
        var rule = new PlcAddressValidationRule { ExpectedType = expectedType };
        Assert.False(rule.Validate(address, Culture).IsValid);
    }

    [Fact]
    public void DeviceAddressConflict_FormatsVisibilityAndText()
    {
        var converter = new DeviceAddressConflictConverter();
        var device = new Device { Id = "device-1", Name = "设备1" };
        IReadOnlyDictionary<string, string> summaries = new Dictionary<string, string>
        {
            [device.Id] = "D100 与设备2冲突"
        };

        Assert.Equal(Visibility.Visible, converter.Convert(new object[] { device, summaries }, typeof(Visibility), null!, Culture));
        Assert.Equal("地址冲突：D100 与设备2冲突", converter.Convert(new object[] { device, summaries }, typeof(string), "Text", Culture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(new object[] { device, new Dictionary<string, string>() }, typeof(Visibility), null!, Culture));
        Assert.Equal(string.Empty, converter.Convert(new object[] { device }, typeof(string), "Text", Culture));
    }
}
