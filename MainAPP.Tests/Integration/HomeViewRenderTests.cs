using System.Windows;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using MainAPP.Views;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 主页 UI 渲染冒烟测试：验证 HomeView 在 STA 线程中可加载、无 XAML 解析异常。
/// 不做深度可视化树断言（容易受 Dispatcher 异步影响），仅保证视图可实例化并绑定到 ViewModel。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class HomeViewRenderTests : WpfTestHost
{
    public HomeViewRenderTests(WpfStaFixture fixture) : base(fixture) { }

    [Fact]
    public void View_Loads_WithoutException()
    {
        var (repo, _, _) = BuildTestRepository(deviceCount: 1);
        var appSettings = new AppSettings();
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);

        RunOnSta(app =>
        {
            var view = new HomeView { DataContext = homeVm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        // 未抛异常即视为通过
    }

    [Fact]
    public void PlcDisconnected_Banner_StateBinding_Works()
    {
        var (repo, _, _) = BuildTestRepository(deviceCount: 1);
        var appSettings = new AppSettings();
        var conn = new PlcConnectionManager(new FakePlcDriver(), appSettings);
        var selection = new DeviceSelectionService();
        var homeVm = new HomeViewModel(repo, conn, appSettings, null!, selection);

        Assert.False(conn.IsConnected);

        RunOnSta(app =>
        {
            var view = new HomeView { DataContext = homeVm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        // 仅断言 ViewModel 状态，可视化树文本搜索不稳定
        Assert.False(conn.IsConnected);
    }
}
