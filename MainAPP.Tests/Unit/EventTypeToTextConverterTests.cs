using System.Globalization;
using System.Windows.Data;
using MainAPP.Converters;
using MainAPP.Entities;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 报警事件类型 → 文本：1=触发 / 2=恢复 / 3=班次切换。
/// 主路径接收 AlarmEventType 枚举；兼容 int 兜底（旧绑定直接绑库原始字段）。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class EventTypeToTextConverterTests
{
    private static readonly EventTypeToTextConverter _conv = new();

    [Theory]
    [InlineData(AlarmEventType.Triggered, "触发")]
    [InlineData(AlarmEventType.Recovered, "恢复")]
    [InlineData(AlarmEventType.ShiftChange, "班次切换")]
    public void EnumValue_ReturnsLabel(AlarmEventType type, string expected)
    {
        var result = (string)_conv.Convert(type, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1, "触发")]
    [InlineData(2, "恢复")]
    [InlineData(3, "班次切换")]
    public void IntValue_ReturnsLabel(int value, string expected)
    {
        var result = (string)_conv.Convert(value, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(99)]
    public void UnknownInt_ReturnsUnknown(int value)
    {
        var result = (string)_conv.Convert(value, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal("未知", result);
    }

    [Fact]
    public void UnknownEnum_ReturnsUnknown()
    {
        var result = (string)_conv.Convert((AlarmEventType)99, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal("未知", result);
    }

    [Fact]
    public void NonEnumNonInt_ReturnsUnknown()
    {
        var result = (string)_conv.Convert("triggered", typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal("未知", result);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        Assert.Throws<NotImplementedException>(() =>
            _conv.ConvertBack("触发", typeof(int), null!, CultureInfo.InvariantCulture));
    }
}
