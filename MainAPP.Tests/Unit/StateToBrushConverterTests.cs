using System.Globalization;
using System.Windows.Media;
using MainAPP.Converters;
using MainAPP.Tests.Integration;
using Kanban.Core.Models;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 设备状态码 → 画刷颜色（运行绿 / 报警红 / 暂停黄 / 其它灰）。
/// 画刷来自 Application.Current.FindResource，需 WpfStaFixture 注入主题资源。
/// 资源色：SuccessBrush=#FF34D399, DangerBrush=#FFF87171, WarningBrush=#FFFBBF24,
///         SecondaryBorderBrush=#FF3A4453（见 WpfStaFixture.InjectResources）。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class StateToBrushConverterTests
{
    private static readonly StateToBrushConverter _conv = new();

    [Theory]
    [InlineData((int)DeviceStatus.Running, "#FF34D399")]
    [InlineData((int)DeviceStatus.Alarm, "#FFF87171")]
    [InlineData((int)DeviceStatus.Paused, "#FFFBBF24")]
    [InlineData((int)DeviceStatus.Unknown, "#FF3A4453")]
    public void KnownState_ReturnsExpectedBrush(int state, string expectedHex)
    {
        var brush = (SolidColorBrush)_conv.Convert(state, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expectedHex, brush.Color.ToString());
    }

    [Fact]
    public void UnknownStateValue_ReturnsSecondaryBorderBrush()
    {
        var brush = (SolidColorBrush)_conv.Convert(99, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal("#FF3A4453", brush.Color.ToString());
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        Assert.Throws<NotImplementedException>(() =>
            _conv.ConvertBack(Brushes.Gray, typeof(int), null!, CultureInfo.InvariantCulture));
    }
}
