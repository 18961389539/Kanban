using System.Windows;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using MainAPP.Views;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 主页卡片 + 产线布局的 UI 渲染冒烟测试：验证这些纯 XAML 视图可加载、绑定不抛异常。
/// 这些 View 在覆盖率分析里被标记为"从未被测试引用"（真实缺口），本文件闭合该缺口。
/// 4 个 DeviceManager*Tab 已随 DeviceManagerViewRenderTests 加载父 View 时间接触达，故不重复覆盖。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class HomeAndProductionLineRenderTests : WpfTestHost
{
    public HomeAndProductionLineRenderTests(WpfStaFixture fixture) : base(fixture) { }

    private static HomeViewModel BuildHomeViewModel()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        repo.Devices.Add(new Device { Id = "d1", Name = "设备1" });
        repo.Runtimes.Add(new DeviceRuntime(repo.Devices[0]));
        var plc = new FakePlcDriver();
        var conn = new PlcConnectionManager(plc, appSettings);
        var selection = new DeviceSelectionService();
        // plcService 传 null!：避免构造时启动后台采集轮询（与 HomeViewModelTests 一致）
        return new HomeViewModel(repo, conn, appSettings, null!, selection);
    }

    private static ProductionLineViewModel BuildProductionLineViewModel()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        repo.Devices.Add(new Device { Id = "d1", Name = "设备1" });
        repo.Runtimes.Add(new DeviceRuntime(repo.Devices[0]));
        return new ProductionLineViewModel(repo, new DeviceSelectionService());
    }

    private static void RenderInWindow(FrameworkElement view)
    {
        var win = new Window { Content = view, Width = 1280, Height = 800 };
        win.Show();
        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        win.UpdateLayout();
        win.Close();
    }

    // ---------- 主页 6 张卡片 ----------

    [Fact]
    public void HomeProductionOverviewCard_Loads_WithoutException()
    {
        var vm = BuildHomeViewModel();
        RunOnSta(app => RenderInWindow(new HomeProductionOverviewCard { DataContext = vm }));
    }

    [Fact]
    public void HomeDeviceStatusCard_Loads_WithoutException()
    {
        var vm = BuildHomeViewModel();
        RunOnSta(app => RenderInWindow(new HomeDeviceStatusCard { DataContext = vm }));
    }

    [Fact]
    public void HomeOeeCard_Loads_WithoutException()
    {
        var vm = BuildHomeViewModel();
        RunOnSta(app => RenderInWindow(new HomeOeeCard { DataContext = vm }));
    }

    [Fact]
    public void HomeQualityRateCard_Loads_WithoutException()
    {
        var vm = BuildHomeViewModel();
        RunOnSta(app => RenderInWindow(new HomeQualityRateCard { DataContext = vm }));
    }

    [Fact]
    public void HomeDefectParetoCard_Loads_WithoutException()
    {
        var vm = BuildHomeViewModel();
        RunOnSta(app => RenderInWindow(new HomeDefectParetoCard { DataContext = vm }));
    }

    [Fact]
    public void HomeRealtimeAlarmsCard_Loads_WithoutException()
    {
        var vm = BuildHomeViewModel();
        RunOnSta(app => RenderInWindow(new HomeRealtimeAlarmsCard { DataContext = vm }));
    }

    // ---------- 产线 3 种布局 ----------

    [Fact]
    public void ProductionLineView_Loads_WithoutException()
    {
        var vm = BuildProductionLineViewModel();
        RunOnSta(app => RenderInWindow(new ProductionLineView { DataContext = vm }));
    }
}
