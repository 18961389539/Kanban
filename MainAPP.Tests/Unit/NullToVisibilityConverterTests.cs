using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MainAPP.Converters;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// null 转 Visibility：非 null 显示，null 隐藏。反向转换恒返回 DoNothing。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class NullToVisibilityConverterTests
{
    private static readonly NullToVisibilityConverter _conv = new();

    [Fact]
    public void NonNull_ReturnsVisible()
    {
        var result = _conv.Convert("anything", typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData("")]
    [InlineData(false)]
    public void FalsyButNonNull_ReturnsVisible(object value)
    {
        var result = _conv.Convert(value, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void Null_ReturnsCollapsed()
    {
        var result = _conv.Convert(null!, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = _conv.ConvertBack(Visibility.Visible, typeof(object), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Binding.DoNothing, result);
    }
}
