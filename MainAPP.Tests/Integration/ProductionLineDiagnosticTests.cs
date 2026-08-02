using MainAPP.Data;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 诊断：验证 ProductionLineViewModel 是否从 DeviceRepository 正确填充 LineDevices。
/// 复刻 App.xaml.cs 的启动顺序（先 LoadAll 再构造 ViewModel）。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","None")]
public class ProductionLineDiagnosticTests
{
    [Fact]
    public void LineDevices_Populated_FromRepository()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);

        // 模拟 LoadAll：Devices + 同步 Runtimes
        var d1 = new Device { Name = "注塑机A1", RecipeName = "外壳-GE-204" };
        var d2 = new Device { Name = "焊接机器人B2", RecipeName = "支架-WD-118" };
        repo.Devices.Add(d1);
        repo.Runtimes.Add(new DeviceRuntime(d1));
        repo.Devices.Add(d2);
        repo.Runtimes.Add(new DeviceRuntime(d2));

        var connectionManager = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, connectionManager, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        Assert.Equal(2, lineVm.LineDevices.Count);
        Assert.Equal(2, lineVm.DeviceCount);
        Assert.True(lineVm.IsLargeCardsLayout);
    }
}
