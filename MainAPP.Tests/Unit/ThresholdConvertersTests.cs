using System;
using System.Globalization;
using System.Windows;
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
/// 审查修复 2026-08-13：断言从硬编码 hex 改为与**生产 Brushes.xaml 资源字典引用**比较
/// （此前断言的是 Fixture 自造颜色，生产颜色变更不可检测）。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ThresholdConvertersTests
{
    /// <summary>解析生产资源字典中的语义画刷（Success/Warning/Danger）；缺失即失败。</summary>
    private static Brush ResolveBrush(string bucket)
    {
        var brush = Application.Current.TryFindResource(bucket + "Brush") as Brush;
        Assert.NotNull(brush);
        return brush!;
    }

    // ═══════════════ OeeThresholdConverter ═══════════════

    [Theory]
    [InlineData(1.0, "Success")]   // >=0.85 → Success
    [InlineData(0.85, "Success")]  // boundary
    [InlineData(0.70, "Warning")]  // >=0.60 → Warning
    [InlineData(0.60, "Warning")]  // boundary
    [InlineData(0.30, "Danger")]   // <0.60 → Danger
    [InlineData(0.0, "Danger")]    // zero → Danger
    [InlineData(-0.1, "Danger")]   // negative → Danger
    public void OeeThreshold_ConvertsCorrectly(double input, string expectedBucket)
    {
        var cvt = new OeeThresholdConverter();
        var brush = (Brush)cvt.Convert(input, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Same(ResolveBrush(expectedBucket), brush);
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
    [InlineData(1.0, "Success")]   // >=0.90 → Success
    [InlineData(0.90, "Success")]  // boundary
    [InlineData(0.80, "Warning")]  // >=0.70 → Warning
    [InlineData(0.70, "Warning")]  // boundary
    [InlineData(0.50, "Danger")]   // <0.70 → Danger
    [InlineData(0.0, "Danger")]    // zero → Danger
    public void RatioThreshold_ConvertsCorrectly(double input, string expectedBucket)
    {
        var cvt = new RatioThresholdConverter();
        var brush = (Brush)cvt.Convert(input, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Same(ResolveBrush(expectedBucket), brush);
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
    // 低=好：不良率越高越红。>=0.90 → Danger, >=0.70 → Warning, else → Success

    [Theory]
    [InlineData(0.95, "Danger")]   // 高不良率 → Danger
    [InlineData(0.90, "Danger")]   // boundary
    [InlineData(0.80, "Warning")]  // >=0.70 → Warning
    [InlineData(0.70, "Warning")]  // boundary
    [InlineData(0.50, "Success")]  // 低不良率 → Success
    [InlineData(0.0, "Success")]   // zero → Success
    public void InverseRatioThreshold_ConvertsCorrectly(double input, string expectedBucket)
    {
        var cvt = new InverseRatioThresholdConverter();
        var brush = (Brush)cvt.Convert(input, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Same(ResolveBrush(expectedBucket), brush);
    }

    [Fact]
    public void InverseRatioThreshold_NonDouble_ReturnsGreen()
    {
        var cvt = new InverseRatioThresholdConverter();
        var brush = cvt.Convert("invalid", typeof(Brush), null, CultureInfo.InvariantCulture) as Brush;
        Assert.NotNull(brush);
        Assert.Same(ResolveBrush("Success"), brush);
    }

    [Fact]
    public void InverseRatioThreshold_ConvertBack_Throws()
    {
        var cvt = new InverseRatioThresholdConverter();
        Assert.Throws<NotSupportedException>(() =>
            cvt.ConvertBack(null!, null!, null, CultureInfo.InvariantCulture));
    }
}

// ═══════════════ QualityThresholdConverter（2026-08-11 方案 E：良品率 ≥95% 绿 / 未达标红） ═══════════════

[Collection("WpfUi")]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class QualityThresholdConverterTests
{
    private static Brush ResolveBrush(string bucket)
    {
        var brush = Application.Current.TryFindResource(bucket + "Brush") as Brush;
        Assert.NotNull(brush);
        return brush!;
    }

    [Theory]
    [InlineData(0.95, "Success")]   // 达标边界 → Success
    [InlineData(0.96, "Success")]
    [InlineData(0.9499, "Danger")]  // 未达标 → Danger
    [InlineData(0.0, "Danger")]
    public void QualityThreshold_ConvertsCorrectly(double input, string expectedBucket)
    {
        var cvt = new QualityThresholdConverter();
        var brush = (Brush)cvt.Convert(input, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.Same(ResolveBrush(expectedBucket), brush);
    }

    [Fact]
    public void QualityThreshold_NonDouble_ReturnsGreen()
    {
        var cvt = new QualityThresholdConverter();
        var brush = cvt.Convert("invalid", typeof(Brush), null, CultureInfo.InvariantCulture) as Brush;
        Assert.NotNull(brush);
        Assert.Same(ResolveBrush("Success"), brush);
    }

    [Fact]
    public void QualityThreshold_ConvertBack_Throws()
    {
        var cvt = new QualityThresholdConverter();
        Assert.Throws<NotSupportedException>(() =>
            cvt.ConvertBack(null!, null!, null, CultureInfo.InvariantCulture));
    }
}
