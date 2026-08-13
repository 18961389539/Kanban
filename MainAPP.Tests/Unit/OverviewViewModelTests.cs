using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Integration;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// OverviewViewModel 单元测试：覆盖初始状态、FocusDevice 命令、时间范围切换、
/// RefreshAsync 命令、数据聚合（设备明细/Top 报警/峰值谷值/最长停机）以及待处理报警计数。
/// 由于被测构造函数捕获 Dispatcher.CurrentDispatcher 并在 RefreshAsync 内通过
/// _uiDispatcher.Invoke 修改 ObservableCollection，所有测试均通过 WpfStaFixture
/// 在共享 STA 线程上执行，确保 Dispatcher 消息泵在 await 期间能继续处理 Invoke 请求。
/// </summary>
[Collection("WpfUi")]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class OverviewViewModelTests
{
    private readonly WpfStaFixture _fixture;

    public OverviewViewModelTests(WpfStaFixture fixture)
    {
        _fixture = fixture;
    }

    // ──────────── STA 线程执行辅助 ────────────

    /// <summary>
    /// 在共享 STA 线程上同步执行测试代码。
    /// 用于不依赖 RefreshAsync 完成的纯逻辑测试（初始状态、FocusDevice、属性变更通知）。
    /// </summary>
    private void RunOnSta(Action action)
    {
        Exception? caught = null;
        _fixture.Dispatcher.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { caught = ex; }
        }, DispatcherPriority.Normal);
        if (caught != null) throw caught;
    }

    /// <summary>
    /// 在共享 STA 线程上执行异步测试代码。
    /// 通过 BeginInvoke(async ...) + ManualResetEventSlim 等待异步流程完成，
    /// 保证 STA Dispatcher 在 await 期间能继续消息泵送，避免 _uiDispatcher.Invoke 死锁。
    /// </summary>
    private void RunOnStaAsync(Func<Task> action)
    {
        Exception? caught = null;
        var done = new ManualResetEventSlim();
        _fixture.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await action(); }
            catch (Exception ex) { caught = ex; }
            finally { done.Set(); }
        }));
        done.Wait();
        if (caught != null) throw caught;
    }

    /// <summary>停止 OverviewViewModel 内部 60 秒 DispatcherTimer，避免跨测试定时器回调干扰。</summary>
    private static void StopTimer(OverviewViewModel vm)
    {
        var timerField = typeof(OverviewViewModel).GetField("_refreshTimer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (timerField?.GetValue(vm) is DispatcherTimer timer)
        {
            timer.Stop();
        }
    }

    /// <summary>
    /// 带死线的轮询等待（审查修复 2026-08-13）：原 while(IsLoading) + Task.Delay 无超时，
    /// IsLoading 卡死时测试无限挂起（整个测试进程被拖死）。
    /// </summary>
    private static async Task WaitUntilNotLoadingAsync(OverviewViewModel vm, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (vm.IsLoading && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.False(vm.IsLoading, $"等待 IsLoading 复位超时（{timeoutMs}ms）");
    }

    /// <summary>通过反射设置私有字段，用于测试 IsLoading 重入保护。</summary>
    private static void SetField<T>(OverviewViewModel vm, string fieldName, T value)
    {
        var field = typeof(OverviewViewModel).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(vm, value);
    }

    // ──────────── 构造辅助 ────────────

    private static AppSettings NewAppSettings() => new();

    private static OverviewViewModel CreateVm(
        InMemoryHistoryService? history = null,
        DeviceRepository? repo = null,
        AppSettings? appSettings = null,
        FakeDialogService? dialog = null,
        DeviceSelectionService? selection = null)
    {
        history ??= new InMemoryHistoryService();
        appSettings ??= NewAppSettings();
        repo ??= new DeviceRepository(appSettings);
        dialog ??= new FakeDialogService();
        selection ??= new DeviceSelectionService();
        return new OverviewViewModel(history, repo, appSettings, dialog, selection);
    }

    private static Device AddDevice(DeviceRepository repo, string id, string name, int targetCycle = 100)
    {
        var d = new Device { Id = id, Name = name, TargetCycle = targetCycle };
        repo.Devices.Add(d);
        repo.AddRuntime(d);
        return d;
    }

    /// <summary>构造一台运行设备的状态转换：从 from 之前最近一条记录设为 Running，
    /// 使 [from, to] 整段计入运行时长。返回插入的 StatusTransitionRecord。</summary>
    private static StatusTransitionRecord AddRunningTransition(InMemoryHistoryService history, string deviceId,
        string deviceName, DateTime eventTime)
    {
        var rec = new StatusTransitionRecord
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            PreviousState = (int)DeviceStatus.Unknown,
            CurrentState = (int)DeviceStatus.Running,
            EventTime = eventTime,
        };
        history.StatusTransitions.Add(rec);
        return rec;
    }

    // ═══════════════════════════════════════════════════════════
    // 1. 初始状态
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TotalOk_Default_Zero()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                Assert.Equal(0, vm.TotalOk);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void HasData_Default_False()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                // TotalOk=0, TotalNg=0, AlarmCount=0 → HasData=False
                Assert.Equal(0, vm.TotalOk);
                Assert.Equal(0, vm.TotalNg);
                Assert.Equal(0, vm.AlarmCount);
                Assert.False(vm.HasData);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void SelectedTimeRange_Default_Hours24()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                Assert.Equal(OverviewTimeRange.Hours24, vm.SelectedTimeRange);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void IsHour1_Default_False()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try { Assert.False(vm.IsHour1); }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void IsHours8_Default_False()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try { Assert.False(vm.IsHours8); }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void IsHours24_Default_True()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try { Assert.True(vm.IsHours24); }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void IsDays7_Default_False()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try { Assert.False(vm.IsDays7); }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void TimeRangeOptions_ContainsShiftAndCalendarShortcuts()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                Assert.Equal(7, vm.TimeRangeOptions.Count);
                Assert.Equal(OverviewTimeRange.CurrentShift, vm.TimeRangeOptions[0].Value);
                Assert.Equal(OverviewTimeRange.PreviousShift, vm.TimeRangeOptions[1].Value);
                Assert.Equal(OverviewTimeRange.Today, vm.TimeRangeOptions[2].Value);
                Assert.Equal(OverviewTimeRange.Hour1, vm.TimeRangeOptions[3].Value);
                Assert.Equal(OverviewTimeRange.Hours8, vm.TimeRangeOptions[4].Value);
                Assert.Equal(OverviewTimeRange.Hours24, vm.TimeRangeOptions[5].Value);
                Assert.Equal(OverviewTimeRange.Days7, vm.TimeRangeOptions[6].Value);
            }
            finally { StopTimer(vm); }
        });
    }

    // ═══════════════════════════════════════════════════════════
    // 2. FocusDevice 命令
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void FocusDevice_WithValidId_RaisesFocusDeviceRequested()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                string? received = null;
                vm.FocusDeviceRequested += id => received = id;

                vm.FocusDeviceCommand.Execute("dev-1");

                Assert.Equal("dev-1", received);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void FocusDevice_WithNull_DoesNotRaise()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                bool raised = false;
                vm.FocusDeviceRequested += _ => raised = true;

                vm.FocusDeviceCommand.Execute(null);

                Assert.False(raised);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void FocusDevice_WithEmptyString_DoesNotRaise()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                bool raised = false;
                vm.FocusDeviceRequested += _ => raised = true;

                vm.FocusDeviceCommand.Execute(string.Empty);

                Assert.False(raised);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void FocusDevice_SetsSelectedDeviceId_InSelectionService()
    {
        RunOnSta(() =>
        {
            var selection = new DeviceSelectionService();
            var vm = CreateVm(selection: selection);
            try
            {
                Assert.Null(selection.SelectedDeviceId);

                vm.FocusDeviceCommand.Execute("dev-42");

                Assert.Equal("dev-42", selection.SelectedDeviceId);
            }
            finally { StopTimer(vm); }
        });
    }

    // ═══════════════════════════════════════════════════════════
    // 3. 时间范围切换
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SelectedTimeRange_Change_RaisesPropertyChanged_ForIsFlags()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                var changed = new List<string?>();
                vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

                vm.SelectedTimeRange = OverviewTimeRange.Hour1;

                // NotifyPropertyChangedFor 生成的通知：IsHour1/IsHours8/IsHours24/IsDays7 都应触发
                Assert.Contains(nameof(vm.IsHour1), changed);
                Assert.Contains(nameof(vm.IsHours8), changed);
                Assert.Contains(nameof(vm.IsHours24), changed);
                Assert.Contains(nameof(vm.IsDays7), changed);
                Assert.Contains(nameof(vm.SelectedTimeRange), changed);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void SelectedTimeRange_Hour1_IsHour1True()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                vm.SelectedTimeRange = OverviewTimeRange.Hour1;

                Assert.True(vm.IsHour1);
                Assert.False(vm.IsHours8);
                Assert.False(vm.IsHours24);
                Assert.False(vm.IsDays7);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void SelectedTimeRange_Days7_IsDays7True()
    {
        RunOnSta(() =>
        {
            var vm = CreateVm();
            try
            {
                vm.SelectedTimeRange = OverviewTimeRange.Days7;

                Assert.False(vm.IsHour1);
                Assert.False(vm.IsHours8);
                Assert.False(vm.IsHours24);
                Assert.True(vm.IsDays7);
            }
            finally { StopTimer(vm); }
        });
    }

    // ═══════════════════════════════════════════════════════════
    // 4. RefreshAsync 命令
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void RefreshAsync_WithNoDevices_ClearsAllData()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            var vm = CreateVm(history: history, repo: repo);
            try
            {
                // 显式触发刷新（构造时已 BeginInvoke 一次，但被 IsLoading 守卫合并）
                await vm.RefreshCommand.ExecuteAsync(null);

                Assert.Equal(0, vm.TotalOk);
                Assert.Equal(0, vm.TotalNg);
                Assert.Equal(0, vm.AlarmCount);
                Assert.Equal(0, vm.PendingAlarmCount);
                Assert.Empty(vm.DeviceSummaries);
                Assert.Empty(vm.TopAlarms);
                Assert.False(vm.HasData);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void ExportReport_WritesCurrentSummaryCsv()
    {
        RunOnStaAsync(async () =>
        {
            var dialog = new FakeDialogService
            {
                SaveFilePath = Path.Combine(Path.GetTempPath(), $"kanban_overview_{Guid.NewGuid():N}.csv")
            };
            var vm = CreateVm(dialog: dialog);
            try
            {
                vm.TotalOk = 10;
                vm.TotalNg = 1;
                vm.DeviceSummaries.Add(new DeviceOverviewSummary
                {
                    DeviceName = "设备1",
                    OkCount = 10,
                    NgCount = 1,
                    QualityRate = 10.0 / 11.0,
                    Oee = 0.8,
                });

                await vm.ExportReportCommand.ExecuteAsync(null);

                var csv = File.ReadAllText(dialog.SaveFilePath);
                Assert.Contains("总合格产量,10", csv);
                Assert.Contains("设备1", csv);
                Assert.Contains("设备明细", csv);
                Assert.Contains("Top 报警", csv);
            }
            finally
            {
                StopTimer(vm);
                if (File.Exists(dialog.SaveFilePath)) File.Delete(dialog.SaveFilePath);
            }
        });
    }

    [Fact]
    public void RefreshAsync_WithDevices_UpdatesKpi()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "设备1", targetCycle: 100);

            var now = DateTime.Now;
            history.ProductionLogs.Add(new ProductionLog
            {
                DeviceId = "dev-1",
                DeviceName = "设备1",
                OkProduction = 100,
                NgProduction = 0,
                Timestamp = now.AddHours(-1),
            });
            AddRunningTransition(history, "dev-1", "设备1", now.AddHours(-23));

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                await vm.RefreshCommand.ExecuteAsync(null);

                Assert.Equal(100, vm.TotalOk);
                Assert.Equal(0, vm.TotalNg);
                Assert.True(vm.HasData);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void RefreshAsync_CrossShiftProduction_SumsEachShiftInstanceDelta()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "设备1", targetCycle: 100);

            var day = DateTime.Today.AddDays(-3);
            var records = new[]
            {
                (day.AddHours(10), "白班", 100),
                (day.AddHours(19), "白班", 180),
                (day.AddHours(21), "夜班", 20),
                (day.AddDays(1).AddHours(7), "夜班", 50),
                (day.AddDays(1).AddHours(10), "白班", 40),
                (day.AddDays(1).AddHours(19), "白班", 90),
                (day.AddDays(1).AddHours(21), "夜班", 10),
                (day.AddDays(2).AddHours(7), "夜班", 25),
            };
            foreach (var (timestamp, shift, ok) in records)
            {
                history.ProductionLogs.Add(new ProductionLog
                {
                    DeviceId = "dev-1",
                    DeviceName = "设备1",
                    ShiftName = shift,
                    OkProduction = ok,
                    Timestamp = timestamp,
                });
            }

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                vm.SelectedTimeRange = OverviewTimeRange.Days7;
                await WaitUntilNotLoadingAsync(vm);
                await vm.RefreshCommand.ExecuteAsync(null);
                await WaitUntilNotLoadingAsync(vm);

                // (180-100) + (50-20) + (90-40) + (25-10) = 175
                Assert.Equal(175, vm.TotalOk);
                Assert.Equal(175, vm.ShiftComparisons.Sum(s => s.OkCount));
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void RefreshAsync_WhenLoading_DoesNotReenter()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "设备1", targetCycle: 100);

            var now = DateTime.Now;
            history.ProductionLogs.Add(new ProductionLog
            {
                DeviceId = "dev-1",
                DeviceName = "设备1",
                OkProduction = 100,
                NgProduction = 0,
                Timestamp = now.AddHours(-1),
            });
            AddRunningTransition(history, "dev-1", "设备1", now.AddHours(-23));

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                // 第一次刷新：加载数据
                await vm.RefreshCommand.ExecuteAsync(null);
                Assert.Equal(100, vm.TotalOk);

                // 通过反射将 IsLoading 置为 true，模拟"正在加载"
                SetField(vm, "_isLoading", true);

                // 清空历史数据后再次刷新：因 IsLoading=true 应立即返回，不修改属性
                history.ProductionLogs.Clear();

                vm.RefreshCommand.Execute(null);

                // TotalOk 仍为 100（未被清空刷新）
                Assert.Equal(100, vm.TotalOk);
            }
            finally
            {
                SetField(vm, "_isLoading", false);
                StopTimer(vm);
            }
        });
    }

    // ═══════════════════════════════════════════════════════════
    // 5. 数据聚合
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void RefreshAsync_AggregatesDeviceSummaries()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            // 三台设备，通过不同 targetCycle 形成不同 OEE：
            // dev1 targetCycle=10  → performance=100/(10*23)≈0.435 → OEE≈0.435
            // dev2 targetCycle=100 → performance=100/(100*23)≈0.0435 → OEE≈0.0435
            // dev3 targetCycle=1000→ performance=100/(1000*23)≈0.00435 → OEE≈0.00435
            AddDevice(repo, "dev-1", "设备A", targetCycle: 10);
            AddDevice(repo, "dev-2", "设备B", targetCycle: 100);
            AddDevice(repo, "dev-3", "设备C", targetCycle: 1000);

            var now = DateTime.Now;
            foreach (var (id, name) in new[] { ("dev-1", "设备A"), ("dev-2", "设备B"), ("dev-3", "设备C") })
            {
                history.ProductionLogs.Add(new ProductionLog
                {
                    DeviceId = id,
                    DeviceName = name,
                    OkProduction = 100,
                    NgProduction = 0,
                    Timestamp = now.AddHours(-1),
                });
                AddRunningTransition(history, id, name, now.AddHours(-23));
            }

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                await vm.RefreshCommand.ExecuteAsync(null);

                // 复盘页只针对当前选中设备，默认选择首台 dev-1。
                Assert.Single(vm.DeviceSummaries);
                Assert.Equal("dev-1", vm.DeviceSummaries[0].DeviceId);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void RefreshAsync_CalculatesTopAlarms()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "设备1", targetCycle: 100);

            var now = DateTime.Now;
            // 6 个不同 (AlarmName, DeviceName, PlcAddress) 组合，触发次数 6/5/4/3/2/1
            // 每个报警 Triggered 与 Recovered 交替，确保最后状态为 Recovered（不挂起 pending）
            for (int i = 1; i <= 6; i++)
            {
                string alarmId = $"a{i}";
                string alarmName = $"报警{i}";
                string plcAddr = $"D{i}";
                for (int k = 0; k < i; k++)
                {
                    var triggerTime = now.AddMinutes(-(60 - k * 2 - i));
                    var recoverTime = triggerTime.AddSeconds(30);
                    history.AlarmEvents.Add(new AlarmEventRecord
                    {
                        DeviceId = "dev-1",
                        DeviceName = "设备1",
                        AlarmId = alarmId,
                        AlarmName = alarmName,
                        PlcAddress = plcAddr,
                        EventType = AlarmEventType.Triggered,
                        EventTime = triggerTime,
                    });
                    history.AlarmEvents.Add(new AlarmEventRecord
                    {
                        DeviceId = "dev-1",
                        DeviceName = "设备1",
                        AlarmId = alarmId,
                        AlarmName = alarmName,
                        PlcAddress = plcAddr,
                        EventType = AlarmEventType.Recovered,
                        EventTime = recoverTime,
                    });
                }
            }

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                await vm.RefreshCommand.ExecuteAsync(null);

                // 最多 5 个，按 TriggerCount 降序
                Assert.Equal(5, vm.TopAlarms.Count);
                Assert.Equal(6, vm.TopAlarms[0].TriggerCount);
                Assert.Equal(5, vm.TopAlarms[1].TriggerCount);
                Assert.Equal(4, vm.TopAlarms[2].TriggerCount);
                Assert.Equal(3, vm.TopAlarms[3].TriggerCount);
                Assert.Equal(2, vm.TopAlarms[4].TriggerCount);
                // 第 6 个（TriggerCount=1）应被截断
                Assert.DoesNotContain(vm.TopAlarms, a => a.TriggerCount == 1);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void RefreshAsync_BuildsStatusTimelineAndHealthIssues()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "设备1", targetCycle: 100);
            var now = DateTime.Now;

            history.StatusTransitions.Add(new StatusTransitionRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                PreviousState = (int)DeviceStatus.Unknown,
                CurrentState = (int)DeviceStatus.Running,
                EventTime = now.AddMinutes(-90),
            });
            history.StatusTransitions.Add(new StatusTransitionRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                PreviousState = (int)DeviceStatus.Running,
                CurrentState = (int)DeviceStatus.Alarm,
                EventTime = now.AddMinutes(-50),
            });
            history.StatusTransitions.Add(new StatusTransitionRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                PreviousState = (int)DeviceStatus.Alarm,
                CurrentState = (int)DeviceStatus.Running,
                EventTime = now.AddMinutes(-40),
            });
            history.ProductionLogs.Add(new ProductionLog
            {
                DeviceId = "dev-1", DeviceName = "设备1", ShiftName = "白班",
                OkProduction = 10, NgProduction = 0, Timestamp = now.AddMinutes(-80),
            });
            history.ProductionLogs.Add(new ProductionLog
            {
                DeviceId = "dev-1", DeviceName = "设备1", ShiftName = "白班",
                OkProduction = 10, NgProduction = 0, Timestamp = now.AddMinutes(-5),
            });
            for (var index = 0; index < 3; index++)
            {
                var trigger = now.AddMinutes(-35 + index * 3);
                history.AlarmEvents.Add(new AlarmEventRecord
                {
                    DeviceId = "dev-1", DeviceName = "设备1", AlarmId = "a1",
                    AlarmName = "高温", PlcAddress = "M10",
                    EventType = AlarmEventType.Triggered, EventTime = trigger,
                });
                history.AlarmEvents.Add(new AlarmEventRecord
                {
                    DeviceId = "dev-1", DeviceName = "设备1", AlarmId = "a1",
                    AlarmName = "高温", PlcAddress = "M10",
                    EventType = AlarmEventType.Recovered, EventTime = trigger.AddMinutes(1),
                });
            }

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                await vm.RefreshCommand.ExecuteAsync(null);

                Assert.True(vm.StatusTimeline.Count >= 3);
                Assert.Contains(vm.StatusTimeline, segment => segment.StatusText == "报警");
                Assert.Equal(3, vm.TopAlarms[0].TriggerCount);
                Assert.Equal(2, vm.TopAlarms[0].RepeatCount);
                Assert.True(vm.TopAlarms[0].AverageIntervalMinutes > 0);
                Assert.Contains(vm.HealthIssues, issue => issue.StartsWith("节拍异常", StringComparison.Ordinal));
                Assert.True(vm.HealthScore < 100);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void RefreshAsync_CalculatesPeakValleyHour()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "设备1", targetCycle: 100);

            var now = DateTime.Now;
            // 在 2 小时前对齐到整点放一条生产快照 OkProduction=100
            // 经 DiffCumulative 后该小时桶 OK=100，其余桶=0 → Peak 落在该小时，Valley 落在首个桶
            var peakTime = now.AddHours(-2);
            peakTime = new DateTime(peakTime.Year, peakTime.Month, peakTime.Day, peakTime.Hour, 0, 0);
            history.ProductionLogs.Add(new ProductionLog
            {
                DeviceId = "dev-1",
                DeviceName = "设备1",
                OkProduction = 100,
                NgProduction = 0,
                Timestamp = peakTime,
            });
            AddRunningTransition(history, "dev-1", "设备1", now.AddHours(-23));

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                await vm.RefreshCommand.ExecuteAsync(null);

                Assert.Equal(peakTime.ToString("HH:mm"), vm.PeakHour);
                Assert.Equal(100, vm.PeakHourOk);
                // Valley 为首个桶（OK=0）
                Assert.Equal(0, vm.ValleyHourOk);
                Assert.False(string.IsNullOrEmpty(vm.ValleyHour));
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void RefreshAsync_CalculatesLongestDowntime()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "短停机设备", targetCycle: 100);
            AddDevice(repo, "dev-2", "长停机设备", targetCycle: 100);

            var now = DateTime.Now;
            // dev1：报警持续 60 秒
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "短停机设备",
                AlarmId = "short-1", AlarmName = "短报警", PlcAddress = "D1",
                EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-30),
            });
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "短停机设备",
                AlarmId = "short-1", AlarmName = "短报警", PlcAddress = "D1",
                EventType = AlarmEventType.Recovered, EventTime = now.AddMinutes(-29),
            });
            // dev2：报警持续 600 秒（10 分钟）
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-2", DeviceName = "长停机设备",
                AlarmId = "long-1", AlarmName = "长报警", PlcAddress = "D2",
                EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-20),
            });
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-2", DeviceName = "长停机设备",
                AlarmId = "long-1", AlarmName = "长报警", PlcAddress = "D2",
                EventType = AlarmEventType.Recovered, EventTime = now.AddMinutes(-10),
            });

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                vm.SelectedDeviceId = "dev-2";
                await WaitUntilNotLoadingAsync(vm);
                await vm.RefreshCommand.ExecuteAsync(null);

                Assert.Equal("长停机设备", vm.LongestDowntimeDevice);
                Assert.Equal("长报警", vm.LongestDowntimeAlarm);
            }
            finally { StopTimer(vm); }
        });
    }

    // ═══════════════════════════════════════════════════════════
    // 6. PendingAlarmCount
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void RefreshAsync_WithPendingAlarms_IncrementsPendingCount()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "设备1", targetCycle: 100);

            var now = DateTime.Now;
            // a1：Triggered → Recovered（已恢复，非 pending）
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a1", AlarmName = "报警1", PlcAddress = "D1",
                EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-50),
            });
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a1", AlarmName = "报警1", PlcAddress = "D1",
                EventType = AlarmEventType.Recovered, EventTime = now.AddMinutes(-40),
            });
            // a2：Triggered → Recovered → Triggered（末条为 Triggered，pending）
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a2", AlarmName = "报警2", PlcAddress = "D2",
                EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-30),
            });
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a2", AlarmName = "报警2", PlcAddress = "D2",
                EventType = AlarmEventType.Recovered, EventTime = now.AddMinutes(-20),
            });
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a2", AlarmName = "报警2", PlcAddress = "D2",
                EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-10),
            });
            // a3：仅 Triggered（pending）
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a3", AlarmName = "报警3", PlcAddress = "D3",
                EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-5),
            });

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                await vm.RefreshCommand.ExecuteAsync(null);

                // 总 Triggered 数 = 1(a1) + 2(a2) + 1(a3) = 4
                Assert.Equal(4, vm.AlarmCount);
                // pending：a2 末条 Triggered、a3 末条 Triggered
                Assert.Equal(2, vm.PendingAlarmCount);
            }
            finally { StopTimer(vm); }
        });
    }

    [Fact]
    public void RecoveredAlarmCount_EqualsAlarmCountMinusPending()
    {
        RunOnStaAsync(async () =>
        {
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(NewAppSettings());
            AddDevice(repo, "dev-1", "设备1", targetCycle: 100);

            var now = DateTime.Now;
            // a1：Triggered → Recovered（已恢复）
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a1", AlarmName = "报警1", PlcAddress = "D1",
                EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-40),
            });
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a1", AlarmName = "报警1", PlcAddress = "D1",
                EventType = AlarmEventType.Recovered, EventTime = now.AddMinutes(-35),
            });
            // a2：仅 Triggered（pending）
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "设备1",
                AlarmId = "a2", AlarmName = "报警2", PlcAddress = "D2",
                EventType = AlarmEventType.Triggered, EventTime = now.AddMinutes(-5),
            });

            var vm = CreateVm(history: history, repo: repo);
            try
            {
                await vm.RefreshCommand.ExecuteAsync(null);

                // AlarmCount = 2（a1 + a2 各 1 次 Triggered）
                Assert.Equal(2, vm.AlarmCount);
                Assert.Equal(1, vm.PendingAlarmCount);
                // RecoveredAlarmCount = AlarmCount - PendingAlarmCount = 1
                Assert.Equal(1, vm.RecoveredAlarmCount);
                Assert.Equal(vm.AlarmCount - vm.PendingAlarmCount, vm.RecoveredAlarmCount);
            }
            finally { StopTimer(vm); }
        });
    }
}
