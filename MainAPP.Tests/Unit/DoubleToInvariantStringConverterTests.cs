using System.Globalization;
using MainAPP.Converters;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// double ↔ 不变字符串（InvariantCulture）互转转换器单元测试。
/// 该转换器用于设置页“界面字号”ComboBox：SelectedValue 绑定 double 类型的 UiScale，
/// ComboBoxItem.Tag 为字符串（"1" / "1.15" / "1.3"）。
/// 双向转换保证初始项能正确匹配选中（否则 1.0 与 "1" 不等导致初始无选中）。
/// 纯函数、不依赖 Application.Current，可独立运行。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DoubleToInvariantStringConverterTests
{
    private static readonly DoubleToInvariantStringConverter _conv = new();

    // ═══════════════ Convert（double → string）═══════════════

    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(1.15, "1.15")]
    [InlineData(1.3, "1.3")]
    [InlineData(0.5, "0.5")]
    [InlineData(-2.5, "-2.5")]
    public void Convert_Double_ReturnsInvariantString(double input, string expected)
    {
        var result = (string)_conv.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-double")]
    [InlineData(5)] // int，不是 double → 走 fallback
    public void Convert_NonDouble_ReturnsFallbackOne(object? input)
    {
        var result = (string)_conv.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("1", result);
    }

    // ═══════════════ ConvertBack（string → double）═══════════════

    [Theory]
    [InlineData("1", 1.0)]
    [InlineData("1.15", 1.15)]
    [InlineData("1.3", 1.3)]
    public void ConvertBack_String_ReturnsDouble(string input, double expected)
    {
        var result = (double)_conv.ConvertBack(input, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]        // 空字符串 → TryParse 失败 → fallback
    [InlineData("abc")]     // 非数字 → fallback
    [InlineData("1,5")]     // 逗号分隔（非不变文化格式）→ 解析失败 → fallback
    public void ConvertBack_Invalid_ReturnsFallbackOne(string input)
    {
        var result = (double)_conv.ConvertBack(input, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ConvertBack_NonString_ReturnsFallbackOne()
    {
        var result = (double)_conv.ConvertBack(123, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(1.0, result);
    }

    // ═══════════════ 设置页 ComboBox 场景往返 ════════════════

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.15)]
    [InlineData(1.3)]
    public void RoundTrip_UiScale_KeepsSelection(double scale)
    {
        // 复现设置页：UiScale ↔ ComboBoxItem.Tag，保证初始项正确选中
        var tag = (string)_conv.Convert(scale, typeof(string), null, CultureInfo.InvariantCulture);
        var back = (double)_conv.ConvertBack(tag, typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(scale, back);
    }
}
