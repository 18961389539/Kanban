using System.Globalization;
using System.Windows.Data;
using MainAPP.Converters;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 设备状态码 → 中文文本（待机/运行/报警/暂停/未知）。
/// 转换器按 int 判断，故直接传入 DeviceStatus 常量。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class StateToTextConverterTests
{
    private static readonly StateToTextConverter _conv = new();

    [Theory]
    [InlineData((int)DeviceStatus.Unknown, "初始")]
    [InlineData((int)DeviceStatus.Running, "运行")]
    [InlineData((int)DeviceStatus.Alarm, "报警")]
    [InlineData((int)DeviceStatus.Paused, "待机")]
    public void KnownState_ReturnsLabel(int state, string expected)
    {
        var result = (string)_conv.Convert(state, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public void UnknownState_ReturnsUnknown(int state)
    {
        var result = (string)_conv.Convert(state, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal("未知", result);
    }

    [Fact]
    public void NonIntValue_ReturnsUnknown()
    {
        var result = (string)_conv.Convert("running", typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal("未知", result);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        Assert.Throws<NotImplementedException>(() =>
            _conv.ConvertBack("运行", typeof(int), null!, CultureInfo.InvariantCulture));
    }
}
