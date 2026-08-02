using System;
using System.Globalization;
using System.Windows.Data;
using MainAPP.Converters;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 多值相等比较转换器单元测试。
/// 用于设备卡片选中态：values[0] == values[1] 时返回 true。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class EqualityConverterTests
{
    private readonly EqualityConverter _converter = new();

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, false)]
    [InlineData(null, null, true)]
    [InlineData(null, 5, false)]
    [InlineData(5, null, false)]
    public void SingleValueConvert_ReturnsExpectedEquality(object? value, object? parameter, bool expected)
    {
        var result = _converter.Convert(value!, typeof(bool), parameter!, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, false)]
    [InlineData("a", "a", true)]
    [InlineData("a", "b", false)]
    [InlineData(1.5, 1.5, true)]
    [InlineData(1.5, 2.5, false)]
    public void Convert_ReturnsCorrectEquality(object a, object b, bool expected)
    {
        var values = new object[] { a, b };
        var result = _converter.Convert(values, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_BothNull_ReturnsTrue()
    {
        var values = new object[2];
        var result = _converter.Convert(values, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.True((bool)result);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(5, null)]
    public void Convert_OneNull_ReturnsFalse(object? a, object? b)
    {
        var values = new object[2];
        if (a != null) values[0] = a;
        if (b != null) values[1] = b;
        var result = _converter.Convert(values, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void Convert_InsufficientValues_ReturnsFalse()
    {
        var result = _converter.Convert(new object[1], typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void Convert_NullValues_ReturnsFalse()
    {
        var result = _converter.Convert(null!, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void SingleValueConvertBack_ReturnsDoNothing()
    {
        var result = ((IValueConverter)_converter).ConvertBack(
            true, typeof(object), null!, CultureInfo.InvariantCulture);

        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }

    [Fact]
    public void Convert_SameStringReference_ReturnsTrue()
    {
        // 字符串驻留：相同字面量引用相等
        var s = "hello";
        var result = _converter.Convert(new object[] { s, s }, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.True((bool)result);
    }

    [Fact]
    public void Convert_DifferentNumericTypes_EqualValue_ReturnsFalse()
    {
        // int 5 与 double 5.0 类型不同，Equals 返回 false
        var result = _converter.Convert(new object[] { 5, 5.0 }, typeof(bool), null!, CultureInfo.InvariantCulture);
        Assert.False((bool)result);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        Assert.Throws<NotImplementedException>(() =>
            _converter.ConvertBack(true, new[] { typeof(object), typeof(object) }, null!, CultureInfo.InvariantCulture));
    }
}
