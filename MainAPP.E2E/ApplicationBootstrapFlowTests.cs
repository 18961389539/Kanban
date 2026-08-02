using System.Windows;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using MainAPP.Views;
using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// 应用启动端到端流程：验证完整 DI Host 启动、MainWindow 可实例化、
/// 4 个 View 单例注入正确、首帧渲染无异常、PLC 默认未连接。
/// 所有 UI 操作在 STA 线程执行，断言也在 STA 线程内避免跨线程访问。
/// MainWindow 是 DI 单例，一旦 Close 不能再 Show，测试中用 Hide() 保持可重用。
/// </summary>
[Collection("E2E")]
public class ApplicationBootstrapFlowTests
{
    private readonly TestHost _host;

    public ApplicationBootstrapFlowTests(TestHost host) => _host = host;

    [Fact]
    public void Host_Starts_AllServicesResolvable()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        Assert.NotNull(_host.Resolve<AppSettings>());
        Assert.NotNull(_host.Resolve<DatabaseProvider>());
        Assert.NotNull(_host.Resolve<DeviceRepository>());
        Assert.NotNull(_host.Resolve<IPlcDriver>());
        Assert.NotNull(_host.Resolve<PlcConnectionManager>());
        Assert.NotNull(_host.Resolve<PlcDataAcquisitionService>());
        Assert.NotNull(_host.Resolve<HistoryService>());
        Assert.NotNull(_host.Resolve<IDeviceSelectionService>());
        Assert.NotNull(_host.Resolve<IDialogService>());
    }

    [Fact]
    public void MainWindow_CanInstantiate_OnStaThread()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            Assert.NotNull(window);
            Assert.NotNull(window.DataContext);
            Assert.IsType<MainWindowViewModel>(window.DataContext);
        });
    }

    [Fact]
    public void MainWindow_AllViews_InjectedAsSingletons()
    {
        _host.ResetState();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            Assert.Equal(NavigationPageCatalog.All.Count, window.Pages.Count);
            Assert.All(window.Pages, page => Assert.NotNull(page.ViewModel));
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            var activePage = window.Pages.Single(page => page.Definition.Index == 0);
            Assert.NotNull(activePage.View);
            Assert.Same(activePage.ViewModel, (activePage.View as FrameworkElement)?.DataContext);
            window.Hide();
        });
    }

    [Fact]
    public void MainWindow_FirstFrameRender_NoException()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        _host.LoadDevices();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            window.UpdateLayout();
            window.Hide();
        });
    }

    [Fact]
    public void PlcConnectionManager_DefaultNotConnected()
    {
        _host.ResetState();

        var conn = _host.Resolve<PlcConnectionManager>();
        Assert.False(conn.IsConnected);
    }

    [Fact]
    public void MainWindowViewModel_InitialIndex_IsZero()
    {
        _host.ResetState();

        var vm = _host.GetMainWindowViewModel();
        Assert.Equal(0, vm.SelectedIndex);
    }
}
