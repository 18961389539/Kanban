using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// DevicePlcCommandHandler 单元测试：覆盖四类 PLC 操作的前置校验与危险操作二次确认分支。
/// 构造仅依赖 FakePlcDriver（内存桩）+ 可控 IsConnected 的 PlcConnectionManager；
/// 校验/取消分支均在其成功路径触及 _dataAcquisitionService 之前返回，故该依赖以 null 注入无碍。
/// 成功写路径（WriteRecipe/ReadPlcValue/ResetCountAlarm）由 FakePlcDriver 承接，不触碰数据服务。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DevicePlcCommandHandlerTests
{
    private static (DevicePlcCommandHandler handler, FakePlcDriver plc, PlcConnectionManager conn) NewHandler(bool connected)
    {
        var plc = new FakePlcDriver();
        var conn = new PlcConnectionManager(plc, new AppSettings());
        conn.IsConnected = connected;
        // 校验/取消分支在触达 _dataAcquisitionService 前即返回，注入 null 安全
        var handler = new DevicePlcCommandHandler(plc, conn, null!);
        return (handler, plc, conn);
    }

    private static Device SampleDevice(string recipeAddress = "", int recipeValue = 0, string resetAddress = "")
        => new()
        {
            Id = "dev-1",
            Name = "测试设备",
            RecipeAddress = recipeAddress,
            RecipeValue = recipeValue,
            ProductionResetAddress = resetAddress,
        };

    // ──────────── WriteRecipeAsync ────────────

    [Fact]
    public async Task WriteRecipe_NotConnected_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: false);
        var dev = SampleDevice(recipeAddress: "D100", recipeValue: 100);

        var r = await handler.WriteRecipeAsync(dev);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("未连接", r.Message);
    }

    [Fact]
    public async Task WriteRecipe_NoRecipeAddress_ReturnsInfo()
    {
        var (handler, _, _) = NewHandler(connected: true);
        var dev = SampleDevice(recipeAddress: "", recipeValue: 100);

        var r = await handler.WriteRecipeAsync(dev);

        Assert.Equal(PlcOpStatus.Info, r.Status);
    }

    [Fact]
    public async Task WriteRecipe_InvalidAddress_NotDWord_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: true);
        var dev = SampleDevice(recipeAddress: "M100", recipeValue: 100); // M 位非 D 字

        var r = await handler.WriteRecipeAsync(dev);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("格式无效", r.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public async Task WriteRecipe_ValueOutOfRange_ReturnsWarning(int value)
    {
        var (handler, _, _) = NewHandler(connected: true);
        var dev = SampleDevice(recipeAddress: "D100", recipeValue: value);

        var r = await handler.WriteRecipeAsync(dev);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("超出合理范围", r.Message);
    }

    [Fact]
    public async Task WriteRecipe_ValidConfig_WritesAndReturnsSuccess()
    {
        var (handler, plc, _) = NewHandler(connected: true);
        var dev = SampleDevice(recipeAddress: "D100", recipeValue: 500);

        var r = await handler.WriteRecipeAsync(dev);

        Assert.Equal(PlcOpStatus.Success, r.Status);
        Assert.Single(plc.WriteHistory);
        Assert.Equal("D100", plc.WriteHistory[0].Address);
        Assert.Equal(500, plc.WriteHistory[0].Value);
    }

    // ──────────── ResetProductionAsync（危险操作，二次确认） ────────────

    [Fact]
    public async Task ResetProduction_NotConnected_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: false);
        var dev = SampleDevice(resetAddress: "D200");

        var r = await handler.ResetProductionAsync(dev, _ => true);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("未连接", r.Message);
    }

    [Fact]
    public async Task ResetProduction_NoResetAddress_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: true);
        var dev = SampleDevice(resetAddress: "");

        var r = await handler.ResetProductionAsync(dev, _ => true);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("未配置", r.Message);
    }

    [Fact]
    public async Task ResetProduction_InvalidAddress_NotDWord_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: true);
        var dev = SampleDevice(resetAddress: "M200"); // M 位非 D 字

        var r = await handler.ResetProductionAsync(dev, _ => true);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("格式无效", r.Message);
    }

    [Fact]
    public async Task ResetProduction_ConfirmRejected_ReturnsCancelled()
    {
        var (handler, _, _) = NewHandler(connected: true);
        var dev = SampleDevice(resetAddress: "D200");

        var r = await handler.ResetProductionAsync(dev, _ => false);

        Assert.Equal(PlcOpStatus.Cancelled, r.Status);
        Assert.Contains("取消", r.Message);
    }

    // ──────────── ReadPlcValueAsync ────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadPlcValue_EmptyAddress_ReturnsWarning(string? address)
    {
        var (handler, _, _) = NewHandler(connected: true);

        var r = await handler.ReadPlcValueAsync(address);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("地址为空", r.Message);
    }

    [Fact]
    public async Task ReadPlcValue_NotConnected_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: false);

        var r = await handler.ReadPlcValueAsync("D100");

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("未连接", r.Message);
    }

    [Fact]
    public async Task ReadPlcValue_InvalidAddress_NotDWord_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: true);

        var r = await handler.ReadPlcValueAsync("M100");

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("格式无效", r.Message);
    }

    [Fact]
    public async Task ReadPlcValue_ValidAddress_ReturnsSuccessWithReadValue()
    {
        var (handler, plc, _) = NewHandler(connected: true);
        plc.SetInt32("D100", 42);

        var r = await handler.ReadPlcValueAsync("D100");

        Assert.Equal(PlcOpStatus.Success, r.Status);
        Assert.Equal(42, r.ReadValue);
    }

    // ──────────── ResetCountAlarmValueAsync ────────────

    [Fact]
    public async Task ResetCountAlarm_NullAlarm_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: true);

        var r = await handler.ResetCountAlarmValueAsync(null!);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("地址为空", r.Message);
    }

    [Fact]
    public async Task ResetCountAlarm_EmptyAddress_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: true);
        var alarm = new CountAlarm { Name = "计数报警", PlcAddress = "" };

        var r = await handler.ResetCountAlarmValueAsync(alarm);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
    }

    [Fact]
    public async Task ResetCountAlarm_NotConnected_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: false);
        var alarm = new CountAlarm { Name = "计数报警", PlcAddress = "D300" };

        var r = await handler.ResetCountAlarmValueAsync(alarm);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("未连接", r.Message);
    }

    [Fact]
    public async Task ResetCountAlarm_InvalidAddress_NotDWord_ReturnsWarning()
    {
        var (handler, _, _) = NewHandler(connected: true);
        var alarm = new CountAlarm { Name = "计数报警", PlcAddress = "M300" };

        var r = await handler.ResetCountAlarmValueAsync(alarm);

        Assert.Equal(PlcOpStatus.Warning, r.Status);
        Assert.Contains("格式无效", r.Message);
    }

    [Fact]
    public async Task ResetCountAlarm_ValidAddress_WritesZeroAndResetsCurrentValue()
    {
        var (handler, plc, _) = NewHandler(connected: true);
        var alarm = new CountAlarm { Name = "计数报警", PlcAddress = "D300", CurrentValue = 17 };

        var r = await handler.ResetCountAlarmValueAsync(alarm);

        Assert.Equal(PlcOpStatus.Success, r.Status);
        Assert.Equal(0, alarm.CurrentValue);
        Assert.Single(plc.WriteHistory);
        Assert.Equal("D300", plc.WriteHistory[0].Address);
        Assert.Equal(0, plc.WriteHistory[0].Value);
    }
}
