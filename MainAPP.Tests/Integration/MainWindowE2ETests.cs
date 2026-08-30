using System;
using System.IO;
using System.Windows;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using LicenseManager.Models;
using LicenseManager.Services;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.Tests;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 完整窗口级 E2E：用与 App 完全相同的组合根（AddMainAppCore/PresentationServices）构建真实
/// MainWindow，验证此前单元/渲染级无法覆盖的两条真实事件通道：
/// 1) 侧边栏跳转 → MainWindow.OnNavigationSelectionChanged（TryLeaveWithDirtyCheck）拦截；
/// 2) 窗口关闭 → MainWindow.OnMainWindowClosing（TryCloseWithDirtyCheck）拦截；
/// 3) 快捷键/程序化导航 → MainWindowViewModel.Navigate（MayDiscardUnsavedAndLeave）拦截。
/// 不启动 Host（<see cref="IHost.StartAsync"/>）与后台采集，只做窗口+导航+事件层验证。
/// 授权/对话框/持久化目录均替换为临时隔离版，避免触碰真实机器状态。
/// </summary>
[Collection("WpfUi")]
[Trait("Category", "Integration")]
[Trait("Speed", "Slow")]
[Trait("Requires", "STA")]
public class MainWindowE2ETests : WpfTestHost, IDisposable
{
    private string? _tempDir;
    private IHost? _host;

    public MainWindowE2ETests(WpfStaFixture fixture) : base(fixture) { }

    public void Dispose()
    {
        if (_host != null)
        {
            // 容器释放前先冲刷共享 STA Dispatcher，让在途操作（timer 回调 / async 封送）
            // 在 IServiceProvider 存活时完成；释放后再次冲刷，处理释放过程新入队的操作。
            // 否则这些操作会在后续测试的 Invoke 里访问已释放的 IServiceProvider，
            // 抛 ObjectDisposedException('IServiceProvider')（间歇性“下一测试失败”）。
            DrainDispatcher();
            try { _host.Dispose(); } catch { /* best-effort */ }
            DrainDispatcher();
            _host = null;
        }
        if (_tempDir != null)
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
            _tempDir = null;
        }
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", null);
    }

    /// <summary>
    /// 组装与被测应用完全一致的容器（生产与测试共用同一组合根），仅替换：
    /// IDialogService → FakeDialogService（可编程返回结果）、授权三件套 → 临时目录版本。
    /// 返回真实窗口 + 调用方需要的 VM/仓库引用。
    /// </summary>
    private (IHost Host, MainWindow Window, MainWindowViewModel RootVm, DeviceManagerViewModel DeviceVm, FakeDialogService Dialog, DeviceRepository Repo)
        BuildRealWindow()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kanban_mainwindow_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Config")); // ProductionBaselineStore 写 baselines.json 到 Config 子目录
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _tempDir);

        var appSettings = new AppSettings { ConfigDirectory = _tempDir };
        var dialog = new FakeDialogService();
        var licenseStore = new LicenseStore(_tempDir);
        var trialRegistryBackup = new TrialRegistryBackupStub();
        var activationTracker = new ActivationAttemptTracker(_tempDir);

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMainAppCoreServices(appSettings);
                services.AddMainAppPresentationServices();

                // ── 测试隔离替换（其余全部保持真实注册） ──
                services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialog));
                // 新增设备向导弹真实 Window（无 UI 输入会返回 null → SelectedDevice 不设置 → 脏检查无法触发）。
                // 换为自动返回新设备的 stub，等价于用户确认向导。
                services.Replace(ServiceDescriptor.Singleton<IDeviceSetupWizardService>(
                    new FakeDeviceSetupWizardService()));
                services.Replace(ServiceDescriptor.Singleton(licenseStore));
                services.Replace(ServiceDescriptor.Singleton<LicenseManager.Services.TrialRegistryBackup>(trialRegistryBackup));
                services.Replace(ServiceDescriptor.Singleton(activationTracker));
            })
            .Build();

        // 数据库表初始化（主页 VM 加载时会访问工单/历史等空表；与 MainWindowViewModelTests 一致）
        var db = host.Services.GetRequiredService<DatabaseProvider>();
        db.EnsureCreatedAll();

        // 角色门禁：设备管理页需 Engineer+，先以管理员登录再实例化 MainWindow（其构造会解析 RootVm）
        var userSession = host.Services.GetRequiredService<Services.UserSession>();
        userSession.Login(new User { Username = "admin", DisplayName = "管理员", Role = UserRole.Admin });

        var repo = host.Services.GetRequiredService<DeviceRepository>();
        repo.Devices.Add(new Device { Name = "E2E测试机A" });
        repo.Devices.Add(new Device { Name = "E2E测试机B" });

        var window = host.Services.GetRequiredService<MainWindow>();
        // XAML 默认全屏无边框，测试中归一化为普通窗口，避免吞屏
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.ResizeMode = ResizeMode.CanResize;
        window.Width = 1280;
        window.Height = 800;

        var rootVm = host.Services.GetRequiredService<MainWindowViewModel>();
        _host = host;
        return (host, window, rootVm, rootVm.DeviceManagerViewModel, dialog, repo);
    }

    private void DirtyDeviceManager(DeviceManagerViewModel deviceVm)
    {
        deviceVm.AddDeviceCommand.Execute(null);
        deviceVm.SelectedDevice!.Name = "未保存改名";
    }

    // ───────────── 用例 ─────────────

    [Fact]
    public void Window_Shows_WithHomePage_Smoke()
    {
        RunOnSta(app =>
        {
            var (_, win, vm, _, _, _) = BuildRealWindow();
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);

            win.Close();
        });
    }

    /// <summary>侧边栏真实通道：ListBox 选中 → OnNavigationSelectionChanged → 拒绝停留。</summary>
    [Fact]
    public void Sidebar_DirtyDeviceManager_Rejected_StaysOnDeviceManager()
    {
        RunOnSta(app =>
        {
            var (_, win, vm, deviceVm, dialog, _) = BuildRealWindow();
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            // 通过 VM 触发侧边栏 ListBox 选中（SelectedIndex 双向绑定，SelectionChanged 会走真实 handler）
            vm.SelectedIndex = NavigationPageCatalog.DeviceManager.Index;
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(NavigationPageCatalog.DeviceManager.Index, vm.SelectedIndex);

            DirtyDeviceManager(deviceVm);
            Assert.True(deviceVm.IsDirty);

            // 尝试切回主页：视图层 handler（TryLeaveWithDirtyCheck）拒绝 → 回退
            dialog.ShowResult = MessageBoxResult.No;
            vm.SelectedIndex = NavigationPageCatalog.Home.Index;
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);

            Assert.Equal(NavigationPageCatalog.DeviceManager.Index, vm.SelectedIndex);
            Assert.Contains(dialog.ShowCalls, c => c.Message.Contains("未保存", StringComparison.Ordinal));

            win.Close();
        });
    }

    /// <summary>快捷键/程序化导航拦截：Navigate 守卫在真实窗口中生效。</summary>
    [Fact]
    public void HotkeyNavigate_DirtyDeviceManager_Rejected_Stays()
    {
        RunOnSta(app =>
        {
            var (_, win, vm, deviceVm, dialog, _) = BuildRealWindow();
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            vm.Navigate(NavigationPageCatalog.DeviceManager.Key);
            Assert.Equal(NavigationPageCatalog.DeviceManager.Index, vm.SelectedIndex);

            DirtyDeviceManager(deviceVm);
            Assert.True(deviceVm.IsDirty);

            dialog.ShowResult = MessageBoxResult.No;
            vm.Navigate(NavigationPageCatalog.Home.Key);

            Assert.Equal(NavigationPageCatalog.DeviceManager.Index, vm.SelectedIndex);
            Assert.Contains(dialog.ShowCalls, c => c.Message.Contains("离开设备管理页", StringComparison.Ordinal));

            win.Close();
        });
    }

    /// <summary>关闭属真实通道：Closing → TryCloseWithDirtyCheck → 拒绝时窗口保持打开。</summary>
    [Fact]
    public void CloseWindow_DirtyDeviceManager_Rejected_WindowStaysOpen()
    {
        RunOnSta(app =>
        {
            var (_, win, _, deviceVm, dialog, _) = BuildRealWindow();
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            DirtyDeviceManager(deviceVm);
            Assert.True(deviceVm.IsDirty);

            var closed = false;
            win.Closed += (_, _) => closed = true;

            dialog.ShowResult = MessageBoxResult.No;
            win.Close();

            Assert.False(closed);   // Closing 被取消，窗口未关闭
            Assert.Contains(dialog.ShowCalls, c => c.Message.Contains("未保存", StringComparison.Ordinal));

            // 恢复允许关闭，避免遗留窗口（再次触发 Closing 确认）
            dialog.ShowResult = MessageBoxResult.Yes;
            win.Close();
            Assert.True(closed);
        });
    }

    /// <summary>关闭属真实通道：确认后正常关闭。</summary>
    [Fact]
    public void CloseWindow_DirtyDeviceManager_Confirmed_WindowCloses()
    {
        RunOnSta(app =>
        {
            var (_, win, _, deviceVm, dialog, _) = BuildRealWindow();
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            DirtyDeviceManager(deviceVm);
            Assert.True(deviceVm.IsDirty);

            var closed = false;
            win.Closed += (_, _) => closed = true;

            dialog.ShowResult = MessageBoxResult.Yes;
            win.Close();

            Assert.True(closed);
        });
    }

    /// <summary>
    /// 新增设备向导 stub：等价于用户确认向导，自动生成一台唯一的设备返回给调用方，
    /// 让 DeviceManagerViewModel.AddDevice 走"向导已确认"分支（设置 SelectedDevice + 置脏）。
    /// </summary>
    private sealed class FakeDeviceSetupWizardService : IDeviceSetupWizardService
    {
        private int _seq;

        public Device? Show(IReadOnlyList<Device> existingDevices, IPlcAddressCodec addressCodec)
        {
            var name = DeviceManagerViewModel.EnsureUniqueName($"向导设备{++_seq}",
                existingDevices.Select(d => d.Name));
            return new Device { Name = name };
        }
    }
}