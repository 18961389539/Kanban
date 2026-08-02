using System.Globalization;
using System.Windows.Data;
using MainAPP.Converters;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 通用数值阈值比较 Converter：ConverterParameter 形如 "&gt;=5"、">5"、"&lt;=0.6"、"==10"、"!=3"。
/// 比较成立返回 true，解析失败/类型不符返回 false。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ComparisonConverterTests
{
    private static readonly ComparisonConverter _conv = new();

    [Theory]
    [InlineData(5, ">=5", true)]
    [InlineData(6, ">=5", true)]
    [InlineData(4, ">=5", false)]
    [InlineData(6, ">5", true)]
    [InlineData(5, ">5", false)]
    [InlineData(0.6, "<=0.6", true)]
    [InlineData(0.7, "<=0.6", false)]
    [InlineData(0.5, "<0.6", true)]
    [InlineData(0.6, "<0.6", false)]
    [InlineData(10, "==10", true)]
    [InlineData(9, "==10", false)]
    [InlineData(5, "!=5", false)]
    [InlineData(6, "!=5", true)]
    public void Comparison_ReturnsExpected(object value, string expr, bool expected)
    {
        var result = (bool)_conv.Convert(value, typeof(bool), expr, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NullValue_ReturnsFalse()
    {
        var result = (bool)_conv.Convert(null!, typeof(bool), ">=5", CultureInfo.InvariantCulture);
        Assert.False(result);
    }

    [Fact]
    public void EmptyParameter_ReturnsFalse()
    {
        var result = (bool)_conv.Convert(5, typeof(bool), "", CultureInfo.InvariantCulture);
        Assert.False(result);
    }

    [Fact]
    public void NonNumericValue_ReturnsFalse()
    {
        var result = (bool)_conv.Convert("abc", typeof(bool), ">=5", CultureInfo.InvariantCulture);
        Assert.False(result);
    }

    [Fact]
    public void NonNumericThreshold_ReturnsFalse()
    {
        var result = (bool)_conv.Convert(5, typeof(bool), ">=abc", CultureInfo.InvariantCulture);
        Assert.False(result);
    }

    [Fact]
    public void UnsupportedOperator_ReturnsFalse()
    {
        var result = (bool)_conv.Convert(5, typeof(bool), "@5", CultureInfo.InvariantCulture);
        Assert.False(result);
    }

    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = _conv.ConvertBack(true, typeof(object), null!, CultureInfo.InvariantCulture);
        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }
}
