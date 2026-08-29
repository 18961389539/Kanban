using System.Globalization;
using System.Windows.Media;
using MainAPP.Converters;
using MainAPP.Tests.Integration;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 设备状态码 → 画刷颜色（运行绿 / 报警红 / 暂停黄 / 其它灰）。
/// 画刷来自 Application.Current.FindResource，需 WpfStaFixture 注入主题资源。
/// 审查修复 2026-08-13：断言从硬编码 hex 改为与**生产 Brushes.xaml 资源字典引用**比较
/// （Fixture 已改为加载生产资源；此前断言的是 Fixture 自造颜色）。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class StateToBrushConverterTests
{
    private static readonly StateToBrushConverter _conv = new();

    private static Brush ResolveBrush(string key)
    {
        var brush = System.Windows.Application.Current.TryFindResource(key) as Brush;
        Assert.NotNull(brush);
        return brush!;
    }

    [Theory]
    [InlineData((int)DeviceStatus.Running, "StatusRunBrush")]
    [InlineData((int)DeviceStatus.Alarm, "StatusAlarmBrush")]
    [InlineData((int)DeviceStatus.Paused, "StatusPauseBrush")]
    [InlineData((int)DeviceStatus.Offline, "SecondaryBorderBrush")]
    public void KnownState_ReturnsExpectedBrush(int state, string expectedKey)
    {
        var brush = (Brush)_conv.Convert(state, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Same(ResolveBrush(expectedKey), brush);
    }

    [Fact]
    public void UnknownStateValue_ReturnsSecondaryBorderBrush()
    {
        var brush = (Brush)_conv.Convert(99, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Same(ResolveBrush("SecondaryBorderBrush"), brush);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        Assert.Throws<NotImplementedException>(() =>
            _conv.ConvertBack(Brushes.Gray, typeof(int), null!, CultureInfo.InvariantCulture));
    }
}
