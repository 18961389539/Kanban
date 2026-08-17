using System.Windows.Threading;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// 历史查询端到端流程：
/// 1. 预置 SQLite 数据库数据 → 切到历史查询页 → 调用 Search → 验证 TotalCount
/// 2. 空数据库查询 → 验证 IsEmptyResult
/// 3. Tab 切换查询不同类型（产量/状态/报警/OEE）
/// 4. Export 导出 CSV 文件验证
/// </summary>
[Collection("E2E")]
public class HistoryQueryFlowTests
{
    private readonly TestHost _host;

    public HistoryQueryFlowTests(TestHost host) => _host = host;

    private static void WaitForQuery(TestHost host, HistoryQueryViewModel vm, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var frame = new DispatcherFrame();

        void Pump()
        {
            if (!vm.IsLoading || DateTime.UtcNow >= deadline)
            {
                frame.Continue = false;
                return;
            }
            host.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Pump));
        }

        host.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Pump));
        Dispatcher.PushFrame(frame);
        Assert.False(vm.IsLoading, $"历史查询在 {timeoutMs}ms 内未完成");
    }

    /// <summary>预置产量历史记录到数据库（STA 线程，避免跨线程）。
    /// 同时添加对应设备到 DeviceRepository，使查询时 SelectedDeviceId 可匹配。</summary>
    private void SeedProductionLogs(int count = 5)
    {
        _host.Run(() =>
        {
            // 添加设备（ProductionQuery.Query 要求 deviceId 非空才返回结果）
            var repo = _host.Resolve<Kanban.Collector.Core.Data.DeviceRepository>();
            if (!repo.Devices.Any(d => d.Id == "dev-001"))
            {
                repo.Devices.Add(new Device { Id = "dev-001", Name = "测试设备A", TargetCycle = 500 });
                repo.Runtimes.Add(new DeviceRuntime(repo.Devices.First(d => d.Id == "dev-001")));
                _host.Resolve<DeviceManagerViewModel>().DeviceList.RefreshDeviceList();
            }

            var db = _host.Resolve<DatabaseProvider>();
            using var ctx = db.CreateProductionLogContext();
            var now = DateTime.Now;
            for (int i = 0; i < count; i++)
            {
                ctx.ProductionLogs.Add(new ProductionLog
                {
                    DeviceId = "dev-001",
                    DeviceName = "测试设备A",
                    ShiftName = "白班",
                    OkProduction = 100 * (i + 1),
                    NgProduction = i,
                    StatusWord = 1,
                    Timestamp = now.AddMinutes(-i * 10)
                });
            }
            ctx.SaveChanges();
        });
    }

    [Fact]
    public void PreseededData_QueryProduction_ReturnsCorrectCount()
    {
        _host.ResetState();
        SeedProductionLogs(count: 5);

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = 4;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            var historyVm = vm.HistoryQueryViewModel;
            historyVm.SelectedTabIndex = 0;
            historyVm.SelectedDeviceId = "dev-001";
            historyVm.FromDate = DateTime.Today.AddDays(-1);
            historyVm.ToDate = DateTime.Today.AddDays(1).AddSeconds(-1);
            historyVm.SearchCommand.Execute(null);
            WaitForQuery(_host, historyVm);

            Assert.True(historyVm.HasQueried);
            Assert.Equal(5, historyVm.TotalCount);

            window.Hide();
        });
    }

    [Fact]
    public void EmptyDatabase_Query_ShowsEmptyResult()
    {
        _host.ResetState();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = 4;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var historyVm = vm.HistoryQueryViewModel;
            historyVm.SelectedTabIndex = 0;
            historyVm.SearchCommand.Execute(null);
            WaitForQuery(_host, historyVm);

            Assert.True(historyVm.HasQueried);
            Assert.True(historyVm.IsEmptyResult);
            Assert.Equal(0, historyVm.TotalCount);

            window.Hide();
        });
    }

    [Fact]
    public void TabSwitch_QueryAllTabs_NoException()
    {
        _host.ResetState();
        SeedProductionLogs(count: 3);

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = 4;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var historyVm = vm.HistoryQueryViewModel;
            historyVm.FromDate = DateTime.Today.AddDays(-1);
            historyVm.ToDate = DateTime.Today.AddDays(1).AddSeconds(-1);

            for (int tab = 0; tab < 4; tab++)
            {
                historyVm.SelectedTabIndex = tab;
                historyVm.SearchCommand.Execute(null);
                WaitForQuery(_host, historyVm);
                Assert.True(historyVm.HasQueried);
            }

            window.Hide();
        });
    }

    [Fact]
    public void Export_ProductionTab_CreatesCsvFile()
    {
        _host.ResetState();
        SeedProductionLogs(count: 5);

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = 4;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var historyVm = vm.HistoryQueryViewModel;
            historyVm.SelectedTabIndex = 0;
            historyVm.SelectedDeviceId = "dev-001";
            historyVm.FromDate = DateTime.Today.AddDays(-1);
            historyVm.ToDate = DateTime.Today.AddDays(1).AddSeconds(-1);
            historyVm.SearchCommand.Execute(null);
            WaitForQuery(_host, historyVm);
            Assert.Equal(5, historyVm.TotalCount);

            historyVm.ExportCommand.Execute(null);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

            var exportDir = System.IO.Path.Combine(_host.AppSettings.ConfigDirectory, "Exports");
            Assert.True(System.IO.Directory.Exists(exportDir));
            var csvFiles = System.IO.Directory.GetFiles(exportDir, "产量_*.csv");
            Assert.NotEmpty(csvFiles);

            var csv = System.IO.File.ReadAllText(csvFiles[0]);
            Assert.False(string.IsNullOrEmpty(csv));

            window.Hide();
        });
    }

    [Fact]
    public void DeviceFilter_Populated_AfterDeviceLoad()
    {
        _host.ResetState();

        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            repo.Devices.Add(new Device { Name = "测试设备X", TargetCycle = 500 });
            repo.Runtimes.Add(new DeviceRuntime(repo.Devices[0]));
            _host.Resolve<DeviceManagerViewModel>().DeviceList.RefreshDeviceList();
        });

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = 4;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            var historyVm = vm.HistoryQueryViewModel;
            Assert.True(historyVm.DeviceFilterItems.Count >= 1);

            window.Hide();
        });
    }
}
