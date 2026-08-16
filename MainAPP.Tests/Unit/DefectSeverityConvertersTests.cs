using System.Globalization;
using System.Windows.Media;
using MainAPP.Converters;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 缺陷严重度转换器：画刷（Critical=红/Major=橙/Minor=绿，内联创建）
/// 与文本（Critical=严重/Major=一般/Minor=轻微）双重覆盖。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DefectSeverityConvertersTests
{
    private static readonly DefectSeverityToBrushConverter _brush = new();
    private static readonly DefectSeverityToTextConverter _text = new();

    [Theory]
    [InlineData(DefectSeverity.Critical, "#FFF87171")]
    [InlineData(DefectSeverity.Major, "#FFFBBF24")]
    [InlineData(DefectSeverity.Minor, "#FF34D399")]
    public void Brush_KnownSeverity_ReturnsExpected(DefectSeverity severity, string expectedHex)
    {
        var brush = (SolidColorBrush)_brush.Convert(severity, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expectedHex, brush.Color.ToString());
    }

    [Fact]
    public void Brush_UnknownSeverity_ReturnsGray()
    {
        var brush = (SolidColorBrush)_brush.Convert((DefectSeverity)99, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal("#FF808080", brush.Color.ToString());
    }

    [Theory]
    [InlineData(DefectSeverity.Critical, "严重")]
    [InlineData(DefectSeverity.Major, "一般")]
    [InlineData(DefectSeverity.Minor, "轻微")]
    public void Text_KnownSeverity_ReturnsLabel(DefectSeverity severity, string expected)
    {
        var result = (string)_text.Convert(severity, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Text_UnknownSeverity_ReturnsEmpty()
    {
        var result = (string)_text.Convert((DefectSeverity)99, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(string.Empty, result);
    }
}
