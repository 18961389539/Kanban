using System.Windows;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using MainAPP.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 历史查询页 UI 渲染冒烟测试：验证 HistoryQueryView 可加载、Tab/筛选/空状态绑定无异常。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","STA")]
public class HistoryQueryViewRenderTests : WpfTestHost, IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _db;
    private readonly HistoryService _historyService;
    private readonly DeviceRepository _deviceRepo;

    public HistoryQueryViewRenderTests(WpfStaFixture fixture) : base(fixture)
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KanbanHistoryUITests_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_appSettings);
        using (var ctx = _db.CreateProductionLogContext()) ctx.Database.EnsureCreated();
        using (var ctx = _db.CreateAlarmEventContext()) ctx.Database.EnsureCreated();
        using (var ctx = _db.CreateStatusTransitionContext()) ctx.Database.EnsureCreated();
        _historyService = new HistoryService(_db, NullLogger<HistoryService>.Instance);
        _deviceRepo = new DeviceRepository(_appSettings);
        _deviceRepo.Devices.Add(new Device { Id = "dev-001", Name = "设备A", TargetCycle = 600 });
        _deviceRepo.Devices.Add(new Device { Id = "dev-002", Name = "设备B", TargetCycle = 400 });
    }

    public void Dispose()
    {
        try
        {
            _historyService.Dispose();
            System.IO.Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private HistoryQueryViewModel BuildViewModel()
    {
        return new HistoryQueryViewModel(_historyService, _deviceRepo, _appSettings, new StubDialogService());
    }

    [Fact]
    public void View_Loads_WithoutException()
    {
        var vm = BuildViewModel();

        RunOnSta(app =>
        {
            var view = new HistoryQueryView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        Assert.Equal(2, vm.DeviceFilterItems.Count);
    }

    [Fact]
    public void View_SwitchTab_DoesNotThrow()
    {
        var vm = BuildViewModel();

        RunOnSta(app =>
        {
            var view = new HistoryQueryView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();

            // 切换到各 Tab
            for (int i = 0; i < 4; i++)
            {
                vm.SelectedTabIndex = i;
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            }
            win.Close();
        });

        Assert.True(vm.SelectedTabIndex >= 0);
    }

    [Fact]
    public void View_EmptyState_WhenNoQuery()
    {
        var vm = BuildViewModel();

        RunOnSta(app =>
        {
            var view = new HistoryQueryView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        Assert.False(vm.HasQueried);
        Assert.False(vm.IsEmptyResult);
    }

    [Fact]
    public void View_DeviceFilter_Populated()
    {
        var vm = BuildViewModel();

        RunOnSta(app =>
        {
            var view = new HistoryQueryView { DataContext = vm };
            var win = new Window { Content = view, Width = 1280, Height = 800 };
            win.Show();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            win.UpdateLayout();
            win.Close();
        });

        // DeviceFilterItems 应包含全部设备 + "全部设备" 选项
        Assert.True(vm.DeviceFilterItems.Count >= 2);
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
        public Kanban.Core.Entities.WorkOrder? ShowWorkOrderEditor(Kanban.Core.Entities.WorkOrder? template, IReadOnlyList<(string Id, string Name)>? availableDevices = null) => null;
    }
}
