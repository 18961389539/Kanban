using System;
using System.Globalization;
using System.Windows.Media;
using MainAPP.Converters;
using MainAPP.Tests.Integration;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// OEE/合格率阈值转换器单元测试。
/// 注意：转换器静态构造依赖 Application.Current.FindResource 查找画刷资源，
/// FindResource 找不到时会抛异常（非 null），故需 WpfStaFixture 先注入资源。
/// 通过 [Collection("WpfUi")] 复用 fixture，避免类型初始化器在无资源时失败并被缓存。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ThresholdConvertersTests
{
    // ═══════════════ OeeThresholdConverter ═══════════════
    // 资源由 WpfStaFixture 注入：
    //   SuccessBrush = #FF34D399, WarningBrush = #FFFBBF24, DangerBrush = #FFF87171

    [Theory]
    [InlineData(1.0, "#FF34D399")]   // >=0.85 → Success
    [InlineData(0.85, "#FF34D399")]  // boundary
    [InlineData(0.70, "#FFFBBF24")]  // >=0.60 → Warning
    [InlineData(0.60, "#FFFBBF24")]  // boundary
    [InlineData(0.30, "#FFF87171")]  // <0.60 → Danger
    [InlineData(0.0, "#FFF87171")]   // zero → Danger
    [InlineData(-0.1, "#FFF87171")]  // negative → Danger
    public void OeeThreshold_ConvertsCorrectly(double input, string expectedHex)
    {
        var cvt = new OeeThresholdConverter();
        var brush = (SolidColorBrush)cvt.Convert(input, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Equal(expectedHex, brush.Color.ToString());
    }

    [Fact]
    public void OeeThreshold_NonDouble_ReturnsBrush()
    {
        var cvt = new OeeThresholdConverter();
        var brush = cvt.Convert("invalid", typeof(Brush), null, CultureInfo.InvariantCulture) as Brush;
        Assert.NotNull(brush);
    }

    [Fact]
    public void OeeThreshold_ConvertBack_Throws()
    {
        var cvt = new OeeThresholdConverter();
        Assert.Throws<NotSupportedException>(() =>
            cvt.ConvertBack(null!, null!, null, CultureInfo.InvariantCulture));
    }

    // ═══════════════ RatioThresholdConverter ═══════════════

    [Theory]
    [InlineData(1.0, "#FF34D399")]   // >=0.90 → Success
    [InlineData(0.90, "#FF34D399")]  // boundary
    [InlineData(0.80, "#FFFBBF24")]  // >=0.70 → Warning
    [InlineData(0.70, "#FFFBBF24")]  // boundary
    [InlineData(0.50, "#FFF87171")]  // <0.70 → Danger
    [InlineData(0.0, "#FFF87171")]   // zero → Danger
    public void RatioThreshold_ConvertsCorrectly(double input, string expectedHex)
    {
        var cvt = new RatioThresholdConverter();
        var brush = (SolidColorBrush)cvt.Convert(input, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Equal(expectedHex, brush.Color.ToString());
    }

    [Fact]
    public void RatioThreshold_ConvertBack_Throws()
    {
        var cvt = new RatioThresholdConverter();
        Assert.Throws<NotSupportedException>(() =>
            cvt.ConvertBack(null!, null!, null, CultureInfo.InvariantCulture));
    }

    // ═══════════════ RatioToWidthConverter ═══════════════

    [Theory]
    [InlineData(0.5, "60", 30)]
    [InlineData(1.0, "60", 60)]
    [InlineData(0.0, "60", 0)]
    [InlineData(0.27, "100", 27)]
    [InlineData(0.5, "invalid", 50)]   // 参数解析失败 → fallback 100 → 0.5*100=50
    public void RatioToWidth_ConvertsCorrectly(double ratio, string parameter, double expected)
    {
        var cvt = new RatioToWidthConverter();
        var width = (double)cvt.Convert(ratio, typeof(double), parameter, CultureInfo.InvariantCulture);
        Assert.Equal(expected, width);
    }

    [Fact]
    public void RatioToWidth_NonDouble_ReturnsZero()
    {
        var cvt = new RatioToWidthConverter();
        var width = (double)cvt.Convert("not-a-number", typeof(double), "100", CultureInfo.InvariantCulture);
        Assert.Equal(0, width);
    }

    [Fact]
    public void RatioToWidth_ConvertBack_Throws()
    {
        var cvt = new RatioToWidthConverter();
        Assert.Throws<NotSupportedException>(() =>
            cvt.ConvertBack(null!, null!, null, CultureInfo.InvariantCulture));
    }

    // ═══════════════ InverseRatioThresholdConverter ═══════════════
    // 低=好：不良率越高越红。>=0.90 → Danger(红 #FFF87171), >=0.70 → Warning(橙 #FFFBBF24), else → Success(绿 #FF34D399)

    [Theory]
    [InlineData(0.95, "#FFF87171")]  // 高不良率 → Danger
    [InlineData(0.90, "#FFF87171")]  // boundary
    [InlineData(0.80, "#FFFBBF24")]  // >=0.70 → Warning
    [InlineData(0.70, "#FFFBBF24")]  // boundary
    [InlineData(0.50, "#FF34D399")]  // 低不良率 → Success
    [InlineData(0.0, "#FF34D399")]   // zero → Success
    public void InverseRatioThreshold_ConvertsCorrectly(double input, string expectedHex)
    {
        var cvt = new InverseRatioThresholdConverter();
        var brush = (SolidColorBrush)cvt.Convert(input, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Equal(expectedHex, brush.Color.ToString());
    }

    [Fact]
    public void InverseRatioThreshold_NonDouble_ReturnsGreen()
    {
        var cvt = new InverseRatioThresholdConverter();
        var brush = cvt.Convert("invalid", typeof(Brush), null, CultureInfo.InvariantCulture) as Brush;
        Assert.NotNull(brush);
        Assert.Equal("#FF34D399", ((SolidColorBrush)brush!).Color.ToString());
    }

    [Fact]
    public void InverseRatioThreshold_ConvertBack_Throws()
    {
        var cvt = new InverseRatioThresholdConverter();
        Assert.Throws<NotSupportedException>(() =>
            cvt.ConvertBack(null!, null!, null, CultureInfo.InvariantCulture));
    }
}
