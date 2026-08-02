using System.Windows;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using MainAPP.Views;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 产线页 UI 渲染冒烟测试：验证 ProductionLineView 在 STA 线程中可加载、无 XAML 解析异常。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class ProductionLineRenderTests : WpfTestHost
{
    public ProductionLineRenderTests(WpfStaFixture fixture) : base(fixture) { }

    [Fact]
    public void View_Loads_WithDevices_WithoutException()
    {
        var appSettings = new AppSettings();
        var repo = new DeviceRepository(appSettings);
        var d1 = new Device { Name = "注塑机A1", RecipeName = "外壳-GE-204" };
        var d2 = new Device { Name = "焊接机器人B2", RecipeName = "支架-WD-118" };
        repo.Devices.Add(d1); repo.Runtimes.Add(new DeviceRuntime(d1));
        repo.Devices.Add(d2); repo.Runtimes.Add(new DeviceRuntime(d2));
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        Assert.Equal(2, lineVm.LineDevices.Count);
        Assert.True(lineVm.IsLargeCardsLayout);

        RunOnSta(app =>
        {
            var view = new ProductionLineView { DataContext = lineVm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        // 未抛异常即视为通过
    }

    [Fact]
    public void EmptyState_Visible_When_NoDevices()
    {
        var repo = new DeviceRepository(new AppSettings());
        var conn = new PlcConnectionManager(new FakePlcDriver(), new AppSettings());
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, new AppSettings(), null!, selection);
        var lineVm = new ProductionLineViewModel(repo, selection);

        Assert.True(lineVm.HasNoDevices);
        Assert.True(lineVm.IsLargeCardsLayout);

        RunOnSta(app =>
        {
            var view = new ProductionLineView { DataContext = lineVm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });
    }
}
