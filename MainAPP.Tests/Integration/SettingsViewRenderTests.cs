using System.Windows;
using LicenseManager.Services;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using MainAPP.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 设置页 UI 渲染冒烟测试：验证 SettingsView 可加载、配置绑定无异常。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class SettingsViewRenderTests : WpfTestHost
{
    public SettingsViewRenderTests(WpfStaFixture fixture) : base(fixture) { }

    private static (AppSettings settings, SettingsViewModel vm) BuildViewModel()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KanbanSettingsUITests_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        var appSettings = new AppSettings { ConfigDirectory = tempDir };
        var plc = new FakePlcDriver();
        var conn = new PlcConnectionManager(plc, appSettings);
        var dialog = new StubDialogService();
        // 授权管理：用临时目录 + 内存版 RegistryBackup，避免污染测试机器
        var licenseStore = new LicenseStore(tempDir);
        var registryBackup = new TrialRegistryBackupStub();
        var trialTracker = new TrialTracker(licenseStore, registryBackup);
        var attemptTracker = new ActivationAttemptTracker(tempDir);
        var licenseGate = new LicenseGate(licenseStore, trialTracker, attemptTracker);
        licenseGate.CheckStatus();
        var services = new ServiceCollection().BuildServiceProvider();
        var vm = new SettingsViewModel(appSettings, conn, dialog, licenseGate, services);
        return (appSettings, vm);
    }

    [Fact]
    public void View_Loads_WithoutException()
    {
        var (_, vm) = BuildViewModel();

        RunOnSta(app =>
        {
            var view = new SettingsView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        Assert.Equal("192.168.1.2", vm.AppSettings.PlcConfig.IpAddress);
    }

    [Fact]
    public void View_BindsShiftsList()
    {
        var (settings, vm) = BuildViewModel();

        RunOnSta(app =>
        {
            var view = new SettingsView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        Assert.Equal(2, settings.Shifts.Count);
        Assert.Equal("白班", settings.Shifts[0].Name);
    }

    [Fact]
    public void View_BindsPollingInterval()
    {
        var (_, vm) = BuildViewModel();

        RunOnSta(app =>
        {
            var view = new SettingsView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        Assert.Equal(200, vm.AppSettings.PollingIntervalMs);
    }

    /// <summary>不弹窗的 IDialogService 桩。</summary>
    private sealed class StubDialogService : IDialogService
    {
        public MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
            => MessageBoxResult.OK;
        public void NotifySuccess(string message) { }
        public void NotifyWarning(string message) { }
        public void NotifyError(string message) { }
        public void NotifyInfo(string message) { }
        public string? ShowSaveFileDialog(string title, string defaultFileName, string filter) => null;
        public string? ShowOpenFileDialog(string title, string filter) => null;
        public MainAPP.Models.DeviceConfigError? ShowConfigErrors(System.Collections.Generic.IReadOnlyList<MainAPP.Models.DeviceConfigError> errors) => null;
        public string? ShowPasswordInput(string title, string message) => null;
        public Kanban.Core.Entities.WorkOrder? ShowWorkOrderEditor(Kanban.Core.Entities.WorkOrder? template, IReadOnlyList<(string Id, string Name)>? availableDevices = null) => null;
    }
}
