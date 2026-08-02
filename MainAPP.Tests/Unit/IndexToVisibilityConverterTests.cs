using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MainAPP.Converters;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 索引转 Visibility：值等于 ConverterParameter 时显示，否则隐藏。
/// 非 int 值或参数无法解析时返回 Collapsed。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class IndexToVisibilityConverterTests
{
    private static readonly IndexToVisibilityConverter _conv = new();

    [Theory]
    [InlineData(2, "2")]
    [InlineData(0, "0")]
    [InlineData(10, "10")]
    public void MatchingIndex_ReturnsVisible(int index, string param)
    {
        var result = _conv.Convert(index, typeof(Visibility), param, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Theory]
    [InlineData(1, "2")]
    [InlineData(5, "0")]
    public void NonMatchingIndex_ReturnsCollapsed(int index, string param)
    {
        var result = _conv.Convert(index, typeof(Visibility), param, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void NonIntValue_ReturnsCollapsed()
    {
        var result = _conv.Convert("not-an-int", typeof(Visibility), "1", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void UnparseableParameter_ReturnsCollapsed()
    {
        var result = _conv.Convert(3, typeof(Visibility), "abc", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = _conv.ConvertBack(Visibility.Visible, typeof(object), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Binding.DoNothing, result);
    }
}
