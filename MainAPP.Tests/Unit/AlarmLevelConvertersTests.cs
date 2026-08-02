using System.Globalization;
using System.Windows.Media;
using MainAPP.Converters;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 报警级别转换器：画刷（High=红/Medium=橙/Low=黄，内联创建，无需主题资源）
/// 与文本（High=高/Medium=中/Low=低）双重覆盖。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class AlarmLevelConvertersTests
{
    private static readonly AlarmLevelToBrushConverter _brush = new();
    private static readonly AlarmLevelToTextConverter _text = new();

    [Theory]
    [InlineData(AlarmLevel.High, "#FFF87171")]
    [InlineData(AlarmLevel.Medium, "#FFFBBF24")]
    [InlineData(AlarmLevel.Low, "#FFFB923C")]
    public void Brush_KnownLevel_ReturnsExpected(AlarmLevel level, string expectedHex)
    {
        var brush = (SolidColorBrush)_brush.Convert(level, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expectedHex, brush.Color.ToString());
    }

    [Fact]
    public void Brush_UnknownLevel_ReturnsGray()
    {
        var brush = (SolidColorBrush)_brush.Convert((AlarmLevel)99, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal("#FF808080", brush.Color.ToString());
    }

    [Fact]
    public void Brush_NonLevelValue_ReturnsGray()
    {
        var brush = (SolidColorBrush)_brush.Convert("High", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal("#FF808080", brush.Color.ToString());
    }

    [Theory]
    [InlineData(AlarmLevel.High, "高")]
    [InlineData(AlarmLevel.Medium, "中")]
    [InlineData(AlarmLevel.Low, "低")]
    public void Text_KnownLevel_ReturnsLabel(AlarmLevel level, string expected)
    {
        var result = (string)_text.Convert(level, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Text_UnknownLevel_ReturnsEmpty()
    {
        var result = (string)_text.Convert((AlarmLevel)99, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(string.Empty, result);
    }
}
