using System;
using System.Globalization;
using System.Windows.Data;
using MainAPP.Converters;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// TimeSpan ↔ "HH:mm" 双向转换测试。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class TimeSpanToTimeStringConverterTests
{
    private static readonly TimeSpanToTimeStringConverter _conv = new();

    [Theory]
    [InlineData(1, 30, "01:30")]
    [InlineData(0, 5, "00:05")]
    [InlineData(9, 0, "09:00")]
    [InlineData(23, 59, "23:59")]
    public void Convert_FormatsHourMinute(int h, int m, string expected)
    {
        var result = (string)_conv.Convert(new TimeSpan(h, m, 0), typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_NonTimeSpan_ReturnsZeroString()
    {
        var result = (string)_conv.Convert("garbage", typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal("00:00", result);
    }

    [Fact]
    public void Convert_Null_ReturnsZeroString()
    {
        var result = (string)_conv.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal("00:00", result);
    }

    [Theory]
    [InlineData("01:30", 1, 30)]
    [InlineData("00:05", 0, 5)]
    [InlineData("25:00", 25, 0)] // 超过 24h 仍可解析
    public void ConvertBack_ParsesTimeString(string text, int h, int m)
    {
        var result = (TimeSpan)_conv.ConvertBack(text, typeof(TimeSpan), null!, CultureInfo.InvariantCulture);
        Assert.Equal(new TimeSpan(h, m, 0), result);
    }

    [Fact]
    public void ConvertBack_Invalid_ReturnsZero()
    {
        var result = (TimeSpan)_conv.ConvertBack("abc", typeof(TimeSpan), null!, CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void ConvertBack_Empty_ReturnsZero()
    {
        var result = (TimeSpan)_conv.ConvertBack("", typeof(TimeSpan), null!, CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, result);
    }
}
