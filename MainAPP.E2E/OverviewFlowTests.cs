using System.Diagnostics;
using System.Windows.Threading;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// 概览页端到端流程：
/// 1. 空数据刷新 → KPI 全零、HasData=False、设备明细/Top 报警为空
/// 2. 预置生产/状态/报警数据 → 刷新 → 聚合 KPI 正确、设备明细 + Top 报警填充
/// 3. 切换时间范围 → 触发刷新
/// 4. 点击设备行（FocusDeviceCommand）→ 触发 FocusDeviceRequested → 主窗口 SelectedIndex=0
/// 5. 导航到概览页渲染无异常
/// </summary>
[Collection("E2E")]
public class OverviewFlowTests
{
    private readonly TestHost _host;

    public OverviewFlowTests(TestHost host) => _host = host;

    /// <summary>等待 OverviewViewModel.IsLoading 变为 false。
    /// RefreshAsync 内部用 Task.Run 在线程池执行查询，结尾通过 _uiDispatcher.Invoke 回写 UI；
    /// 测试线程在轮询间隙通过 host.Run 派发空动作以驱动 STA 队列推进。</summary>
    private static void WaitForRefresh(OverviewViewModel vm, TestHost host, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (vm.IsLoading && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(20);
            host.Run(() => { });
        }
        Assert.False(vm.IsLoading, $"概览页刷新在 {timeoutMs}ms 内未完成");
        // 再派发一次以让 _uiDispatcher.Invoke 排队的更新和 await 续体跑完
        host.Run(() => { });
    }

    /// <summary>排空 OverviewViewModel 构造函数 Background BeginInvoke 触发的首轮刷新，
    /// 避免它与后续显式刷新产生竞态（BeginInvoke 用默认 SelectedTimeRange 查询，
    /// 若不排空可能在测试设置新时间范围后才运行，覆盖测试期望状态）。</summary>
    private OverviewViewModel SetupOverviewViewModel()
    {
        var vm = _host.Resolve<OverviewViewModel>();
        // 强制 Background 优先级队列推进：构造函数的 BeginInvoke 会被处理
        _host.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        _host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        WaitForRefresh(vm, _host);
        return vm;
    }

    /// <summary>显式触发一次刷新并等待完成，确保 KPI 反映当前已 seed 的数据。
    /// RefreshCommand.Execute 必须在 STA 线程执行：AsyncRelayCommand 入口会同步置 IsLoading=true
    /// 并引发 CanExecuteChanged，UI 按钮（绑定到该命令）的订阅处理器会访问 ButtonBase.Command，
    /// 若在测试线程调用会触发跨线程异常。</summary>
    private static void RefreshAndWait(OverviewViewModel vm, TestHost host)
    {
        WaitForRefresh(vm, host);
        host.Run(() => vm.RefreshCommand.Execute(null));
        WaitForRefresh(vm, host);
    }

    private void SeedOverviewData()
    {
        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            if (!repo.Devices.Any(d => d.Id == "ovr-A"))
            {
                repo.Devices.Add(new Device { Id = "ovr-A", Name = "概览设备A", TargetCycle = 600 });
                repo.Runtimes.Add(new DeviceRuntime(repo.Devices.First(d => d.Id == "ovr-A")));
            }
            if (!repo.Devices.Any(d => d.Id == "ovr-B"))
            {
                repo.Devices.Add(new Device { Id = "ovr-B", Name = "概览设备B", TargetCycle = 500 });
                repo.Runtimes.Add(new DeviceRuntime(repo.Devices.First(d => d.Id == "ovr-B")));
            }
            _host.Resolve<DeviceManagerViewModel>().RefreshDeviceList();

            var db = _host.Resolve<DatabaseProvider>();
            var now = DateTime.Now;

            // 设备 A 生产快照（OkProduction 是班次累计值，末条作为总量）
            using (var ctx = db.CreateProductionLogContext())
            {
                ctx.ProductionLogs.Add(new ProductionLog
                {
                    DeviceId = "ovr-A", DeviceName = "概览设备A", ShiftName = "白班",
                    OkProduction = 100, NgProduction = 5, StatusWord = (int)DeviceStatus.Running,
                    Timestamp = now.AddHours(-2)
                });
                ctx.ProductionLogs.Add(new ProductionLog
                {
                    DeviceId = "ovr-A", DeviceName = "概览设备A", ShiftName = "白班",
                    OkProduction = 300, NgProduction = 15, StatusWord = (int)DeviceStatus.Running,
                    Timestamp = now.AddMinutes(-30)
                });
                ctx.ProductionLogs.Add(new ProductionLog
                {
                    DeviceId = "ovr-B", DeviceName = "概览设备B", ShiftName = "白班",
                    OkProduction = 600, NgProduction = 25, StatusWord = (int)DeviceStatus.Running,
                    Timestamp = now.AddMinutes(-20)
                });
                ctx.SaveChanges();
            }

            // 状态转换：设备全程 Running（区间外初始状态也设为 Running）
            using (var ctx = db.CreateStatusTransitionContext())
            {
                ctx.StatusTransitions.Add(new StatusTransitionRecord
                {
                    DeviceId = "ovr-A", DeviceName = "概览设备A",
                    PreviousState = (int)DeviceStatus.Unknown, CurrentState = (int)DeviceStatus.Running,
                    EventTime = now.AddHours(-3), ShiftName = "白班"
                });
                ctx.StatusTransitions.Add(new StatusTransitionRecord
                {
                    DeviceId = "ovr-B", DeviceName = "概览设备B",
                    PreviousState = (int)DeviceStatus.Unknown, CurrentState = (int)DeviceStatus.Running,
                    EventTime = now.AddHours(-3), ShiftName = "白班"
                });
                ctx.SaveChanges();
            }

            // 报警事件：
            //   设备 A: 温度过高(触发→恢复) + 压力异常(触发，未恢复 → 待处理)
            //   设备 B: 气压不足(触发→恢复)
            using (var ctx = db.CreateAlarmEventContext())
            {
                ctx.AlarmEvents.Add(new AlarmEventRecord
                {
                    DeviceId = "ovr-A", DeviceName = "概览设备A",
                    AlarmId = "A-temp", AlarmName = "温度过高", PlcAddress = "D100",
                    EventType = AlarmEventType.Triggered, EventTime = now.AddHours(-2), ShiftName = "白班"
                });
                ctx.AlarmEvents.Add(new AlarmEventRecord
                {
                    DeviceId = "ovr-A", DeviceName = "概览设备A",
                    AlarmId = "A-temp", AlarmName = "温度过高", PlcAddress = "D100",
                    EventType = AlarmEventType.Recovered, EventTime = now.AddMinutes(-90), ShiftName = "白班"
                });
                ctx.AlarmEvents.Add(new AlarmEventRecord
                {
                    DeviceId = "ovr-A", DeviceName = "概览设备A",
                    AlarmId = "A-pres", AlarmName = "压力异常", PlcAddress = "D200",
                    EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-30), ShiftName = "白班"
                });
                ctx.AlarmEvents.Add(new AlarmEventRecord
                {
                    DeviceId = "ovr-B", DeviceName = "概览设备B",
                    AlarmId = "B-air", AlarmName = "气压不足", PlcAddress = "D300",
                    EventType = AlarmEventType.Triggered, EventTime = now.AddHours(-1), ShiftName = "白班"
                });
                ctx.AlarmEvents.Add(new AlarmEventRecord
                {
                    DeviceId = "ovr-B", DeviceName = "概览设备B",
                    AlarmId = "B-air", AlarmName = "气压不足", PlcAddress = "D300",
                    EventType = AlarmEventType.Recovered, EventTime = now.AddMinutes(-30), ShiftName = "白班"
                });
                ctx.SaveChanges();
            }
        });
    }

    [Fact]
    public void EmptyData_Refresh_ShowsZeroKpis()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        var vm = SetupOverviewViewModel();
        RefreshAndWait(vm, _host);

        Assert.Equal(0, vm.TotalOk);
        Assert.Equal(0, vm.TotalNg);
        Assert.Equal(0, vm.AlarmCount);
        Assert.Equal(0, vm.PendingAlarmCount);
        Assert.False(vm.HasData);
        Assert.Empty(vm.DeviceSummaries);
        Assert.Empty(vm.TopAlarms);
        Assert.Null(vm.TrendChart);
    }

    [Fact]
    public void SeededData_Refresh_AggregatesKpis()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        SeedOverviewData();

        var vm = SetupOverviewViewModel();
        _host.Run(() => vm.SelectedDeviceId = "ovr-A");
        RefreshAndWait(vm, _host);

        // 复盘页只统计当前设备 A：窗口内差分 300-100=200/10。
        Assert.Equal(200, vm.TotalOk);
        Assert.Equal(10, vm.TotalNg);
        // 设备 A 有 2 个 Triggered 事件
        Assert.Equal(2, vm.AlarmCount);
        // A-pres 未恢复 → 1 个待处理
        Assert.Equal(1, vm.PendingAlarmCount);
        Assert.Equal(1, vm.AlarmCount - vm.PendingAlarmCount); // RecoveredAlarmCount
        Assert.Single(vm.DeviceSummaries);
        // 当前设备 A 有 2 个报警组合
        Assert.Equal(2, vm.TopAlarms.Count);
        Assert.True(vm.HasData);
        Assert.NotNull(vm.TrendChart);
    }

    [Fact]
    public void SeededData_TopAlarms_OrderedByTriggerCountDescending()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        SeedOverviewData();

        var vm = SetupOverviewViewModel();
        _host.Run(() => vm.SelectedDeviceId = "ovr-A");
        RefreshAndWait(vm, _host);

        // 当前设备 A：温度过高(1 次) + 压力异常(1 次)，均 1 次。
        // 主要验证排序不崩溃且每条记录字段完整。
        var top = vm.TopAlarms;
        Assert.Equal(2, top.Count);
        foreach (var a in top)
            Assert.True(a.TriggerCount >= 1);
        // 验证降序（允许相等，只要非升序）
        for (int i = 1; i < top.Count; i++)
            Assert.True(top[i].TriggerCount <= top[i - 1].TriggerCount);
    }

    [Fact]
    public void SwitchTimeRange_TriggersRefresh()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        SeedOverviewData();

        var vm = SetupOverviewViewModel();
        _host.Run(() => vm.SelectedDeviceId = "ovr-A");
        RefreshAndWait(vm, _host);

        // 切到近1小时：触发 OnSelectedTimeRangeChanged → RefreshAsync。
        // 必须在 STA 线程设置：OnSelectedTimeRangeChanged 会调用 RefreshAsync，
        // 后者同步置 IsLoading=true 并引发 CanExecuteChanged，UI 按钮订阅 handler
        // 会访问 ButtonBase.Command，在测试线程设置会触发跨线程异常。
        _host.Run(() => vm.SelectedTimeRange = OverviewTimeRange.Hour1);
        RefreshAndWait(vm, _host);

        Assert.Equal(OverviewTimeRange.Hour1, vm.SelectedTimeRange);
        Assert.True(vm.IsHour1);
        Assert.False(vm.IsHours24);
        // 近1小时窗口仍包含 T-30min 的数据，所以 TotalOk 应仍非零
        Assert.True(vm.TotalOk > 0);
    }

    [Fact]
    public void FocusDeviceCommand_NavigatesToDeviceDetail()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        SeedOverviewData();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var mainVm = _host.GetMainWindowViewModel();
            // 先到概览页（索引 5）
            mainVm.SelectedIndex = 5;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var overviewVm = mainVm.OverviewViewModel;
            var selection = _host.Resolve<IDeviceSelectionService>();
            selection.SelectedDeviceId = null; // 重置

            // 模拟点击设备行
            overviewVm.FocusDeviceCommand.Execute("ovr-A");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            // 事件链：FocusDeviceRequested → MainWindowViewModel 跳转设备详情页（Index 9）
            Assert.Equal(NavigationPageCatalog.DeviceDetail.Index, mainVm.SelectedIndex);
            Assert.Equal("ovr-A", selection.SelectedDeviceId);

            window.Hide();
        });
    }

    [Fact]
    public void FocusDeviceCommand_NullOrEmptyDeviceId_IsNoOp()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        SeedOverviewData();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var mainVm = _host.GetMainWindowViewModel();
            mainVm.SelectedIndex = 5;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var overviewVm = mainVm.OverviewViewModel;
            var selection = _host.Resolve<IDeviceSelectionService>();
            selection.SelectedDeviceId = null; // 重置

            // null/空 deviceId 应被忽略，不触发跳转
            overviewVm.FocusDeviceCommand.Execute(null);
            overviewVm.FocusDeviceCommand.Execute("");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            Assert.Equal(5, mainVm.SelectedIndex); // 仍在概览页
            Assert.Null(selection.SelectedDeviceId);

            window.Hide();
        });
    }

    [Fact]
    public void NavigateToOverviewPage_RendersWithoutException()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        SeedOverviewData();

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = 5;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();

            Assert.Equal(5, vm.SelectedIndex);

            window.Hide();
        });
    }

    [Fact]
    public void RefreshCommand_IsLoading_TogglesDuringRefresh()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        SeedOverviewData();

        var vm = SetupOverviewViewModel();
        // 启动刷新后 IsLoading 应立即为 true（在 Task.Run 完成前）。
        // Execute 必须在 STA 线程：AsyncRelayCommand 入口同步置 IsLoading=true 并引发
        // CanExecuteChanged，UI 按钮订阅 handler 会访问 ButtonBase.Command。
        _host.Run(() =>
        {
            vm.RefreshCommand.Execute(null);
            Assert.True(vm.IsLoading);
        });
        WaitForRefresh(vm, _host);
        Assert.False(vm.IsLoading);
    }
}
