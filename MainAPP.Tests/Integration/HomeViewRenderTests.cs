using System.Windows;
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

            // 真实断言（审查修复 2026-08-13：原实现只断言 conn.IsConnected==false，
            // 与测试名"横幅可见性绑定"无关，横幅从未被验证）：
            // 断线 → 横幅 Visible；连接后 DataTrigger 折叠
            var banner = view.FindName("DisconnectBanner") as System.Windows.Controls.Border;
            Assert.NotNull(banner);
            Assert.Equal(System.Windows.Visibility.Visible, banner.Visibility);

            conn.EnsureConnected(); // FakePlcDriver 连接成功 → IsConnected=true → 触发器折叠横幅
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            win.UpdateLayout();
            Assert.Equal(System.Windows.Visibility.Collapsed, banner.Visibility);

            win.Close();
        });
    }
}
