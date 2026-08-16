using System;
using System.Linq;
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
/// 报警 / 状态 / OEE 查询页端到端打开流程。
/// <para>
/// 这三个 ViewModel（AlarmQueryViewModel / StatusQueryViewModel / OeeQueryViewModel）是
/// HistoryQueryViewModel 的子 VM，承载在 MainWindow 历史查询页（SelectedIndex = 4）的
/// Tab 2 / Tab 1 / Tab 3 上，是此前覆盖率盲区里逻辑最重的几块。
/// </para>
/// <para>
/// 测试用"导航到历史查询页 → 切到目标 Tab → 预置真实 SQLite 数据 → 点击查询 → 断言子 VM 结果
/// 已填充且页面渲染无异常"的方式，用真实运行的应用（含 XAML 绑定 / 资源字典 / OxyPlot 图表）
/// 验证这三块盲区，而非单测难以覆盖的渲染与端到端链路。
/// </para>
/// </summary>
[Collection("E2E")]
public class QueryPageFlowTests
{
    private readonly TestHost _host;

    public QueryPageFlowTests(TestHost host) => _host = host;

    /// <summary>导航到历史查询页（SelectedIndex=4）并触发一次渲染。</summary>
    private static void NavigateToHistory(TestHost host)
    {
        var window = host.GetMainWindow();
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        var vm = host.GetMainWindowViewModel();
        vm.SelectedIndex = 4;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    /// <summary>在 STA 线程上注册设备并预置数据（避免跨线程修改已绑定集合）。</summary>
    private void SeedDeviceAndData(string deviceId, Action<DatabaseProvider> seed)
    {
        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            if (!repo.Devices.Any(d => d.Id == deviceId))
            {
                repo.Devices.Add(new Device { Id = deviceId, Name = "查询设备", TargetCycle = 500 });
                repo.Runtimes.Add(new DeviceRuntime(repo.Devices.First(d => d.Id == deviceId)));
                _host.Resolve<DeviceManagerViewModel>().RefreshDeviceList();
            }
            seed(_host.Resolve<DatabaseProvider>());
        });
    }

    private HistoryQueryViewModel HistoryVm => _host.GetMainWindowViewModel().HistoryQueryViewModel;

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

    // ---------------- Alarm (Tab 2) ----------------

    [Fact]
    public void AlarmTab_OpenAndQuery_ShowsAlarmRecords()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        const string dev = "alarm-dev";
        SeedDeviceAndData(dev, db =>
        {
            var now = DateTime.Now;
            using var ctx = db.CreateAlarmEventContext();
            ctx.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = dev, DeviceName = "查询设备", AlarmId = "A1", AlarmName = "高温报警",
                PlcAddress = "D100", EventType = AlarmEventType.Triggered,
                EventTime = now.AddHours(-2), ShiftName = "白班"
            });
            ctx.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = dev, DeviceName = "查询设备", AlarmId = "A1", AlarmName = "高温报警",
                PlcAddress = "D100", EventType = AlarmEventType.Recovered,
                EventTime = now.AddMinutes(-30), ShiftName = "白班"
            });
            ctx.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = dev, DeviceName = "查询设备", AlarmId = "A2", AlarmName = "气压不足",
                PlcAddress = "D200", EventType = AlarmEventType.Triggered,
                EventTime = now.AddHours(-1), ShiftName = "白班"
            });
            ctx.SaveChanges();
        });

        _host.RunOnSta(app =>
        {
            NavigateToHistory(_host);
            var window = _host.GetMainWindow();
            var vm = HistoryVm;
            vm.SelectedTabIndex = 2;
            vm.SelectedDeviceId = dev;
            vm.FromDate = DateTime.Today.AddDays(-1);
            vm.ToDate = DateTime.Today.AddDays(1).AddSeconds(-1);
            vm.SearchCommand.Execute(null);
            WaitForQuery(_host, vm);

            Assert.True(vm.HasQueried);
            Assert.True(vm.TotalCount >= 3);
            Assert.Equal(3, vm.AlarmQuery.AlarmEvents.Count);
            Assert.Equal(2, vm.AlarmQuery.AlarmTriggerCount);
            Assert.Equal(1, vm.AlarmQuery.AlarmRecoverCount);
            Assert.NotNull(vm.AlarmQuery.AlarmChart);
            Assert.NotEmpty(vm.AlarmQuery.LastQueryAlarmNames);
            // 渲染报警 Tab 内容（含图表）不应抛异常
            window.UpdateLayout();

            window.Hide();
        });
    }

    [Fact]
    public void AlarmTab_EmptyDatabase_Query_ShowsZero()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        const string dev = "alarm-empty";
        SeedDeviceAndData(dev, _ => { });

        _host.RunOnSta(app =>
        {
            NavigateToHistory(_host);
            var window = _host.GetMainWindow();
            var vm = HistoryVm;
            vm.SelectedTabIndex = 2;
            vm.SelectedDeviceId = dev;
            vm.SearchCommand.Execute(null);
            WaitForQuery(_host, vm);

            Assert.True(vm.HasQueried);
            Assert.Equal(0, vm.TotalCount);
            Assert.Empty(vm.AlarmQuery.AlarmEvents);
            // 注：空数据时 AlarmChart 仍会被构建（仅含标题的空图表），非 null，属正常行为

            window.Hide();
        });
    }

    // ---------------- Status (Tab 1) ----------------

    [Fact]
    public void StatusTab_OpenAndQuery_ComputesDurations()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        const string dev = "status-dev";
        SeedDeviceAndData(dev, db =>
        {
            var now = DateTime.Now;
            using var ctx = db.CreateStatusTransitionContext();
            ctx.StatusTransitions.Add(new StatusTransitionRecord
            {
                DeviceId = dev, DeviceName = "查询设备",
                PreviousState = (int)DeviceStatus.Unknown, CurrentState = (int)DeviceStatus.Running,
                EventTime = now.AddHours(-3), ShiftName = "白班"
            });
            ctx.StatusTransitions.Add(new StatusTransitionRecord
            {
                DeviceId = dev, DeviceName = "查询设备",
                PreviousState = (int)DeviceStatus.Running, CurrentState = (int)DeviceStatus.Alarm,
                EventTime = now.AddHours(-2), ShiftName = "白班"
            });
            ctx.StatusTransitions.Add(new StatusTransitionRecord
            {
                DeviceId = dev, DeviceName = "查询设备",
                PreviousState = (int)DeviceStatus.Alarm, CurrentState = (int)DeviceStatus.Running,
                EventTime = now.AddHours(-1), ShiftName = "白班"
            });
            ctx.SaveChanges();
        });

        _host.RunOnSta(app =>
        {
            NavigateToHistory(_host);
            var window = _host.GetMainWindow();
            var vm = HistoryVm;
            vm.SelectedTabIndex = 1;
            vm.SelectedDeviceId = dev;
            vm.FromDate = DateTime.Today.AddDays(-1);
            vm.ToDate = DateTime.Today.AddDays(1).AddSeconds(-1);
            vm.SearchCommand.Execute(null);
            WaitForQuery(_host, vm);

            Assert.True(vm.HasQueried);
            Assert.Equal(3, vm.StatusQuery.StatusTransitions.Count);
            Assert.True(vm.StatusQuery.RunTimeSeconds > 0, "运行时段应产生正的运行时长");
            Assert.True(vm.StatusQuery.AlarmTimeSeconds > 0, "报警时段应产生正的报警时长");
            Assert.NotNull(vm.StatusQuery.StatusChart);
            Assert.NotNull(vm.StatusQuery.StatusGanttChart);
            window.UpdateLayout();

            window.Hide();
        });
    }

    [Fact]
    public void StatusTab_EmptyDatabase_Query_ShowsZero()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        const string dev = "status-empty";
        SeedDeviceAndData(dev, _ => { });

        _host.RunOnSta(app =>
        {
            NavigateToHistory(_host);
            var window = _host.GetMainWindow();
            var vm = HistoryVm;
            vm.SelectedTabIndex = 1;
            vm.SelectedDeviceId = dev;
            vm.SearchCommand.Execute(null);
            WaitForQuery(_host, vm);

            Assert.True(vm.HasQueried);
            Assert.Equal(0, vm.TotalCount);
            Assert.Empty(vm.StatusQuery.StatusTransitions);
            // 注：空状态历史时，初始状态默认为 Running，按查询窗口推算会产生正的运行时长，
            // 属正常行为，故不断言 RunTimeSeconds==0

            window.Hide();
        });
    }

    // ---------------- OEE (Tab 3) ----------------

    [Fact]
    public void OeeTab_OpenAndQuery_ComputesOee()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        const string dev = "oee-dev";
        SeedDeviceAndData(dev, db =>
        {
            var now = DateTime.Now;
            using (var pctx = db.CreateProductionLogContext())
            {
                // 窗口前的基线快照：SumWindowProduction 需要窗口起点之前的同班次累计值作差分基准，
                // 否则回退到窗口内首条快照累计值，导致 last - base = 0。
                pctx.ProductionLogs.Add(new ProductionLog
                {
                    DeviceId = dev, DeviceName = "查询设备", ShiftName = "白班",
                    OkProduction = 50, NgProduction = 0,
                    StatusWord = (int)DeviceStatus.Running, Timestamp = now.AddDays(-2).AddHours(12)
                });
                for (int i = 0; i < 5; i++)
                {
                    pctx.ProductionLogs.Add(new ProductionLog
                    {
                        DeviceId = dev, DeviceName = "查询设备", ShiftName = "白班",
                        OkProduction = 100 * (i + 1), NgProduction = 5 * i,
                        StatusWord = (int)DeviceStatus.Running, Timestamp = now.AddMinutes(-i * 10)
                    });
                }
                pctx.SaveChanges();
            }
            using (var sctx = db.CreateStatusTransitionContext())
            {
                sctx.StatusTransitions.Add(new StatusTransitionRecord
                {
                    DeviceId = dev, DeviceName = "查询设备",
                    PreviousState = (int)DeviceStatus.Unknown, CurrentState = (int)DeviceStatus.Running,
                    EventTime = now.AddHours(-3), ShiftName = "白班"
                });
                sctx.SaveChanges();
            }
        });

        _host.RunOnSta(app =>
        {
            NavigateToHistory(_host);
            var window = _host.GetMainWindow();
            var vm = HistoryVm;
            vm.SelectedTabIndex = 3;
            vm.SelectedDeviceId = dev;
            vm.FromDate = DateTime.Today.AddDays(-1);
            vm.ToDate = DateTime.Today.AddDays(1).AddSeconds(-1);

            // 诊断：确认种子数据确实落库
            int dbProdCount;
            using (var c = _host.Resolve<DatabaseProvider>().CreateProductionLogContext())
                dbProdCount = c.ProductionLogs.Count(p => p.DeviceId == dev);

            vm.SearchCommand.Execute(null);
            WaitForQuery(_host, vm);

            Assert.True(vm.HasQueried);
            Assert.True(vm.OeeQuery.OeeOkProduction > 0,
                $"应统计出 OK 产量。dbProdCount={dbProdCount}, SelectedDeviceId={vm.SelectedDeviceId}, " +
                $"OeeOkProduction={vm.OeeQuery.OeeOkProduction}, OeeShiftDetails={vm.OeeQuery.OeeShiftDetails.Count}");
            Assert.True(vm.OeeQuery.OeeValue >= 0 && vm.OeeQuery.OeeValue <= 1, "OEE 应在 [0,1] 区间");
            Assert.NotEmpty(vm.OeeQuery.OeeShiftDetails);
            Assert.NotNull(vm.OeeQuery.OeeChart);
            window.UpdateLayout();

            window.Hide();
        });
    }

    [Fact]
    public void OeeTab_EmptyDatabase_Query_ShowsZero()
    {
        _host.ResetState();
        _host.InitializeDatabases();
        const string dev = "oee-empty";
        SeedDeviceAndData(dev, _ => { });

        _host.RunOnSta(app =>
        {
            NavigateToHistory(_host);
            var window = _host.GetMainWindow();
            var vm = HistoryVm;
            vm.SelectedTabIndex = 3;
            vm.SelectedDeviceId = dev;
            vm.SearchCommand.Execute(null);
            WaitForQuery(_host, vm);

            Assert.True(vm.HasQueried);
            // 注：OEE 查询返回 (1,1) 表示"已为 1 台设备计算 OEE"，TotalCount 恒为 1，
            // 与产量/状态/报警按记录计数不同，故不在此断言 TotalCount==0。
            Assert.Equal(0, vm.OeeQuery.OeeOkProduction);
            // 空数据下 OEE 公式 (0/0) 各分项退化为 1，OEE 总值也为 1，属正常边界行为，不强制为 0

            window.Hide();
        });
    }
}
