using System.Collections.Generic;
using MainAPP.Converters;
using Kanban.Core.Models;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// DeviceRuntimeStatusConverter 纯单元测试：验证（Device + 运行时映射）→ 状态文本 / 画刷的正确性。
/// 画刷分支依赖 Application.Current.FindResource，纯单测环境无 App，此处仅覆盖文本分支（与 StateToTextConverter 口径一致）。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DeviceRuntimeStatusConverterTests
{
    private static (Device device, Dictionary<string, DeviceRuntime> map) MakeMap(int status)
    {
        var device = new Device { Id = "D1" };
        var rt = new DeviceRuntime(device) { StatusWord = status };
        var map = new Dictionary<string, DeviceRuntime> { ["D1"] = rt };
        return (device, map);
    }

    [Theory]
    [InlineData((int)DeviceStatus.Running, "运行")]
    [InlineData((int)DeviceStatus.Alarm, "报警")]
    [InlineData((int)DeviceStatus.Paused, "待机")]
    [InlineData((int)DeviceStatus.Unknown, "初始")]
    public void Convert_Text_ReturnsStatusLabel(int status, string expected)
    {
        var (device, map) = MakeMap(status);
        var converter = new DeviceRuntimeStatusConverter();
        var text = converter.Convert(new object[] { device, map }, null!, "Text", null!);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Convert_Text_MissingRuntime_ReturnsInitial()
    {
        var device = new Device { Id = "X" };
        var map = new Dictionary<string, DeviceRuntime>();
        var converter = new DeviceRuntimeStatusConverter();
        var text = converter.Convert(new object[] { device, map }, null!, "Text", null!);
        Assert.Equal("初始", text);
    }

    [Theory]
    [InlineData((int)DeviceStatus.Running)]
    [InlineData((int)DeviceStatus.Alarm)]
    [InlineData((int)DeviceStatus.Paused)]
    [InlineData((int)DeviceStatus.Unknown)]
    public void Convert_Brush_ReturnsNonNullForEveryStatus(int status)
    {
        var (device, map) = MakeMap(status);
        var converter = new DeviceRuntimeStatusConverter();

        var brush = converter.Convert(new object[] { device, map }, null!, null!, null!);

        Assert.NotNull(brush);
    }

    [Fact]
    public void Convert_Text_NullInputs_ReturnsInitial()
    {
        var converter = new DeviceRuntimeStatusConverter();

        var text = converter.Convert(Array.Empty<object>(), null!, "Text", null!);

        Assert.Equal("初始", text);
    }

    [Fact]
    public void Convert_Brush_ReturnsNonNullBrush()
    {
        var (device, map) = MakeMap((int)DeviceStatus.Running);
        var converter = new DeviceRuntimeStatusConverter();
        // 画刷分支在 App 存在时返回主题画刷；无 App 时回退 Gray，均不应为 null
        var brush = converter.Convert(new object[] { device, map }, null!, null!, null!);
        Assert.NotNull(brush);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        var converter = new DeviceRuntimeStatusConverter();

        Assert.Throws<System.NotSupportedException>(() =>
            converter.ConvertBack(null!, new[] { typeof(object) }, null!, null!));
    }
}
