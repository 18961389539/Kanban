using System.Windows;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Tests;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using MainAPP.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 设备管理页 UI 渲染冒烟测试：验证 DeviceManagerView 可加载、设备列表绑定、选中态切换无异常。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class DeviceManagerViewRenderTests : WpfTestHost, IDisposable
{
    private readonly List<string> _tempDirs = new();

    public DeviceManagerViewRenderTests(WpfStaFixture fixture) : base(fixture) { }

    /// <summary>渲染测试临时目录统一清理（审查修复 2026-08-13：此前每次 BuildViewModel 泄漏一个含 SQLite 的临时目录）。</summary>
    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    private (DeviceRepository repo, PlcDataAcquisitionService dacq, IDialogService dialog, DeviceManagerViewModel vm)
        BuildViewModel(int deviceCount = 2)
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KanbanDMTests_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);
        var appSettings = new AppSettings { ConfigDirectory = tempDir };
        var repo = new DeviceRepository(appSettings);
        var names = new[] { "注塑机A1", "焊接机器人B2", "检测机C3" };
        for (int i = 0; i < deviceCount && i < names.Length; i++)
        {
            var d = new Device { Name = names[i], RecipeName = $"配方-{i + 1}" };
            repo.Devices.Add(d);
            repo.Runtimes.Add(new DeviceRuntime(d));
        }
        var plc = new FakePlcDriver();
        var conn = new PlcConnectionManager(plc, appSettings);
        var db = new DatabaseProvider(appSettings);
        var history = new HistoryService(db, NullLogger<HistoryService>.Instance);
        var baselineStore = new ProductionBaselineStore(appSettings);
        var dacq = new PlcDataAcquisitionService(plc, conn, appSettings, history, repo, baselineStore, NullLogger<PlcDataAcquisitionService>.Instance);
        var dialog = new StubDialogService();
        // 设备管理器拆分后，构造 VM 前需先组装 IO 与 PLC 命令两个协作服务
        var configIO = new DeviceConfigIOService(repo, dialog);
        var plcCommands = new DevicePlcCommandHandler(plc, conn, dacq);
        var alarmCsvIO = new AlarmCsvIOService(dialog);
        var defectCsvIO = new DefectCsvIOService(dialog);
        var counterAlarmCsvIO = new CounterAlarmCsvIOService(dialog);
        var workOrderRepo = new WorkOrderRepository(db, TestMapper.Instance);
        var workOrderService = new WorkOrderService(workOrderRepo, repo, dialog, history);
        var vm = new DeviceManagerViewModel(repo, dacq, dialog, configIO, plcCommands, alarmCsvIO, defectCsvIO, counterAlarmCsvIO, workOrderRepo, workOrderService, new UserSession());
        return (repo, dacq, dialog, vm);
    }

    [Fact]
    public void View_Loads_WithDevices_WithoutException()
    {
        DeviceManagerViewModel vm = null!;
        RunOnSta(app =>
        {
            // VM 必须在 STA 线程构造：FilteredDevices = CollectionViewSource.GetDefaultView(Devices)
            // 返回的 ICollectionView 是 DispatcherObject，会绑定到构造线程的 Dispatcher，
            // 若在测试线程构造再跨线程绑定到视图会抛 InvalidOperationException。
            var (_, _, _, created) = BuildViewModel(deviceCount: 2);
            vm = created;
            var view = new DeviceManagerView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        Assert.Equal(2, vm.Devices.Count);
    }

    [Fact]
    public void View_Loads_EmptyDevices_WithoutException()
    {
        DeviceManagerViewModel vm = null!;
        RunOnSta(app =>
        {
            // VM 必须在 STA 线程构造（FilteredDevices 的 DispatcherObject 亲和性，详见上方测试注释）
            var (_, _, _, created) = BuildViewModel(deviceCount: 0);
            vm = created;
            var view = new DeviceManagerView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        Assert.Empty(vm.Devices);
    }

    [Fact]
    public void SelectDevice_UpdatesSelectedDevice()
    {
        Device? first = null;
        DeviceManagerViewModel vm = null!;
        RunOnSta(app =>
        {
            // VM 必须在 STA 线程构造（FilteredDevices 的 DispatcherObject 亲和性，详见上方测试注释）
            var (repo, _, _, created) = BuildViewModel(deviceCount: 2);
            vm = created;
            first = repo.Devices[0];
            var view = new DeviceManagerView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            vm.SelectedDevice = first;
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            win.Close();
        });

        Assert.Same(first, vm.SelectedDevice);
    }

    [Fact]
    public void SearchKeyword_FiltersDeviceList()
    {
        var (repo, _, _, vm) = BuildViewModel(deviceCount: 3);

        vm.DeviceList.SearchKeyword = "焊接";
        vm.DeviceList.FilteredDevices.Refresh();

        Assert.Single(vm.DeviceList.FilteredDevices);
    }

    /// <summary>不弹窗的 IDialogService 桩，避免测试中 MessageBox/Growl 阻塞。</summary>
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
        public Kanban.Collector.Core.Entities.WorkOrder? ShowWorkOrderEditor(Kanban.Collector.Core.Entities.WorkOrder? template, IReadOnlyList<(string Id, string Name)>? availableDevices = null) => null;
    }
}
