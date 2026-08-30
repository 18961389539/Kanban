using System;
using System.IO;
using System.Linq;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 查询页四个子 ViewModel 的纯单元测试：不依赖任何 SQLite 数据库，
/// 通过 <see cref="InMemoryHistoryService"/> 桩注入内存数据，验证窗口差分、状态时长、报警计数、
/// 设备防空与班次过滤等核心逻辑。覆盖 P0 子集窗口产量回归。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class QueryViewModelUnitTests
{
    private static (AppSettings AppSettings, DeviceRepository Repo) NewRepo(string deviceId = "dev-1")
    {
        var appSettings = new AppSettings { ConfigDirectory = Path.GetTempPath() };
        var repo = new DeviceRepository(appSettings);
        repo.Devices.Add(new Device { Id = deviceId, Name = "设备" + deviceId, TargetCycle = 600 });
        return (appSettings, repo);
    }

    // ════════════════ Production ════════════════

    [Fact]
    public void Production_NoDevice_ReturnsZero()
    {
        var hs = new InMemoryHistoryService();
        var vm = new ProductionQueryViewModel(hs);
        var (total, _) = vm.Query(null, DateTime.Today, DateTime.Today.AddDays(1), null, 1, 50);
        Assert.Equal(0, total);
        Assert.Equal(0, vm.TotalOk);
        Assert.Equal(0, vm.TotalNg);
    }

    [Fact]
    public void Production_SubsetWindow_ScopesToWindowNotFullShift()
    {
        // P0 回归：白班 08:00→0, 10:00→200, 14:00→400；查 10:00–14:00 应得窗口增量 200，而非整班 400
        var hs = new InMemoryHistoryService();
        hs.ProductionLogs.AddRange(new[]
        {
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 0, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 8, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 200, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 10, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 400, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 14, 0, 0) },
        });
        var vm = new ProductionQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 10, 0, 0), new DateTime(2026, 1, 1, 14, 0, 0), null, 1, 50);

        Assert.Equal(200, vm.TotalOk);
        Assert.Equal(0, vm.TotalNg);
        Assert.Equal(1.0, vm.QualityRate, 6);
    }

    [Fact]
    public void Production_ShiftFilter_ReturnsOnlyMatchingShift()
    {
        var hs = new InMemoryHistoryService();
        hs.ProductionLogs.AddRange(new[]
        {
            // 窗口前基准（白班实例内，用于窗口差分）
            new ProductionLog { DeviceId = "dev-1", ShiftName = "白班", OkProduction = 0, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 7, 50, 0) },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "白班", OkProduction = 100, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 9, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "夜班", OkProduction = 50, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 21, 0, 0) },
        });
        var vm = new ProductionQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), "白班", 1, 50);

        Assert.Single(vm.ProductionLogs);
        Assert.Equal("白班", vm.ProductionLogs[0].ShiftName);
        Assert.Equal(100, vm.TotalOk); // 末条 100 − 窗口前基准 0
    }

    // ════════════════ OEE ════════════════

    [Fact]
    public void Oee_NoDevice_ReturnsZero()
    {
        var (appSettings, repo) = NewRepo();
        var hs = new InMemoryHistoryService();
        var vm = new OeeQueryViewModel(hs, repo, appSettings);
        var (total, _) = vm.Query(null, DateTime.Today, DateTime.Today.AddDays(1), null);
        Assert.Equal(0, total);
        Assert.Equal(0, vm.OeeOkProduction);
    }

    [Fact]
    public void Oee_SubsetWindow_ScopesToWindow()
    {
        // P0 回归：与 Production 同数据集，子集窗口下 OeeOkProduction 应为窗口增量 200
        var (appSettings, repo) = NewRepo();
        var hs = new InMemoryHistoryService();
        hs.ProductionLogs.AddRange(new[]
        {
            new ProductionLog { DeviceId = "dev-1", ShiftName = "白班", OkProduction = 0, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 8, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "白班", OkProduction = 200, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 10, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "白班", OkProduction = 400, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 14, 0, 0) },
        });
        hs.StatusTransitions.AddRange(new[]
        {
            new StatusTransitionRecord { DeviceId = "dev-1", PreviousState = 0, CurrentState = 1, EventTime = new DateTime(2026, 1, 1, 8, 0, 0), ShiftName = "白班" },
            new StatusTransitionRecord { DeviceId = "dev-1", PreviousState = 1, CurrentState = 1, EventTime = new DateTime(2026, 1, 1, 14, 0, 0), ShiftName = "白班" },
        });
        var vm = new OeeQueryViewModel(hs, repo, appSettings);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 10, 0, 0), new DateTime(2026, 1, 1, 14, 0, 0), null);

        Assert.Equal(200, vm.OeeOkProduction);
        Assert.Equal(0, vm.OeeNgProduction);
    }

    [Fact]
    public void Oee_UnknownDevice_ReturnsZero()
    {
        var (appSettings, repo) = NewRepo();
        var hs = new InMemoryHistoryService();
        var vm = new OeeQueryViewModel(hs, repo, appSettings);
        var (total, _) = vm.Query("no-such", DateTime.Today, DateTime.Today.AddDays(1), null);
        Assert.Equal(0, total);
    }

    // ════════════════ Status ════════════════

    [Fact]
    public void Status_NoDevice_ReturnsZero()
    {
        var hs = new InMemoryHistoryService();
        var vm = new StatusQueryViewModel(hs);
        var (total, _) = vm.Query(null, DateTime.Today, DateTime.Today.AddDays(1), null, 1, 50);
        Assert.Equal(0, total);
        Assert.Equal(0, vm.RunTimeSeconds);
        Assert.Equal(0, vm.AlarmTimeSeconds);
    }

    [Fact]
    public void Status_ComputesDurations()
    {
        // 08:00 运行 → 10:00 报警 → 12:00 运行；窗口 08:00–12:00
        var hs = new InMemoryHistoryService();
        hs.StatusTransitions.AddRange(new[]
        {
            new StatusTransitionRecord { DeviceId = "dev-1", PreviousState = 0, CurrentState = 1, EventTime = new DateTime(2026, 1, 1, 8, 0, 0), ShiftName = "白班" },
            new StatusTransitionRecord { DeviceId = "dev-1", PreviousState = 1, CurrentState = 2, EventTime = new DateTime(2026, 1, 1, 10, 0, 0), ShiftName = "白班" },
            new StatusTransitionRecord { DeviceId = "dev-1", PreviousState = 2, CurrentState = 1, EventTime = new DateTime(2026, 1, 1, 12, 0, 0), ShiftName = "白班" },
        });
        var vm = new StatusQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 12, 0, 0), null, 1, 50);

        // 运行 08:00–10:00 = 7200s；报警 10:00–12:00 = 7200s；12:00 起运行无结束不计
        Assert.Equal(7200, vm.RunTimeSeconds);
        Assert.Equal(7200, vm.AlarmTimeSeconds);
        Assert.Equal(0, vm.PausedTimeSeconds);
    }

    // ════════════════ Alarm ════════════════

    [Fact]
    public void Alarm_NoDevice_ReturnsZero()
    {
        var hs = new InMemoryHistoryService();
        var vm = new AlarmQueryViewModel(hs);
        var (total, _) = vm.Query(null, DateTime.Today, DateTime.Today.AddDays(1), null, null, 1, 50);
        Assert.Equal(0, total);
        Assert.Equal(0, vm.AlarmTriggerCount);
        Assert.Equal(0, vm.AlarmRecoverCount);
    }

    [Fact]
    public void Alarm_CountsTriggerRecoverPending()
    {
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 9, 30, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a2", AlarmName = "低压", PlcAddress = "D2", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 10, 0, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);

        Assert.Equal(2, vm.AlarmTriggerCount);   // 两条 EventType=1
        Assert.Equal(1, vm.AlarmRecoverCount);   // 一条 EventType=2
        Assert.Equal(1, vm.AlarmPendingCount);   // a2 未恢复
    }

    // ════════════════ 报警持续时长 / MTTR（合理化建议 2）════════════════

    [Fact]
    public void Alarm_PairedTrigger_BackfillsDurationAndMttr()
    {
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 9, 30, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);

        var trigger = vm.AlarmEvents.Single(e => e.EventType == AlarmEventType.Triggered);
        Assert.Equal("30min", trigger.DurationText);
        Assert.Equal("30min", vm.AlarmMttrText);
    }

    [Fact]
    public void Alarm_DurationAndMttr_FormatAsHours()
    {
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 10, 30, 0) }, // 90min = 1.5h
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);

        var trigger = vm.AlarmEvents.Single(e => e.EventType == AlarmEventType.Triggered);
        Assert.Equal("1.5h", trigger.DurationText);
        Assert.Equal("1.5h", vm.AlarmMttrText);
    }

    [Fact]
    public void Alarm_UnpairedTrigger_NoDurationAndNoMttr()
    {
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.Add(new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);

        var trigger = vm.AlarmEvents.Single();
        Assert.Null(trigger.DurationText);
        Assert.Null(vm.AlarmMttrText);
    }

    [Fact]
    public void Alarm_Mttr_AveragesAllPairedDurations()
    {
        var hs = new InMemoryHistoryService();
        // 两对：60min + 120min → 平均 90min = 1.5h
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 10, 0, 0) },   // 60min
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a2", AlarmName = "低压", PlcAddress = "D2", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 11, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a2", AlarmName = "低压", PlcAddress = "D2", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 13, 0, 0) }, // 120min
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);

        Assert.Equal("1.5h", vm.AlarmMttrText);
    }

    // ════════════════ 产量总览 / 不良率（合理化建议 3）════════════════

    [Fact]
    public void Production_TotalProductionAndDefectRate()
    {
        var hs = new InMemoryHistoryService();
        hs.ProductionLogs.AddRange(new[]
        {
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 0, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 8, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 800, NgProduction = 200, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 10, 0, 0) },
        });
        var vm = new ProductionQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 9, 0, 0), new DateTime(2026, 1, 1, 11, 0, 0), null, 1, 50);

        Assert.Equal(800, vm.TotalOk);          // 窗口末条 800 − 窗口前基准(8:00 的 0)
        Assert.Equal(200, vm.TotalNg);
        Assert.Equal(1000, vm.TotalProduction);
        Assert.Equal(0.2, vm.DefectRate, 6);
        Assert.Equal(0.8, vm.QualityRate, 6);
    }

    // ════════════════ 导出全量（合理化建议 4）════════════════

    [Fact]
    public void Alarm_BuildCsvAll_SpansAllPages()
    {
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 9, 30, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a2", AlarmName = "低压", PlcAddress = "D2", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 10, 0, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 2); // pageSize=2 → 2 页

        Assert.Equal(2, CountCsvLines(vm.BuildCsv()));      // 当前页 2 条数据行
        Assert.Equal(3, CountCsvLines(vm.BuildCsvAll()));   // 全量 3 条数据行
    }

    [Fact]
    public void Production_BuildCsvAll_SpansAllPages()
    {
        var hs = new InMemoryHistoryService();
        hs.ProductionLogs.AddRange(new[]
        {
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 0, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 8, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 100, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 9, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 200, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 9, 30, 0) },
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 300, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 10, 0, 0) },
        });
        var vm = new ProductionQueryViewModel(hs);
        var from = new DateTime(2026, 1, 1, 9, 0, 0);
        var to = new DateTime(2026, 1, 1, 11, 0, 0);
        vm.Query("dev-1", from, to, null, 1, 2); // 窗口内 3 条 → 2 页

        Assert.Equal(2, CountCsvLines(vm.BuildCsv(from, to)));      // 当前页 2 条数据行
        Assert.Equal(3, CountCsvLines(vm.BuildCsvAll(from, to)));   // 全量 3 条数据行
    }

    [Fact]
    public void Status_BuildCsvAll_SpansAllPages()
    {
        var hs = new InMemoryHistoryService();
        hs.StatusTransitions.AddRange(new[]
        {
            new StatusTransitionRecord { DeviceId = "dev-1", DeviceName = "A", PreviousState = 0, CurrentState = 1, EventTime = new DateTime(2026, 1, 1, 8, 0, 0), ShiftName = "白班" },
            new StatusTransitionRecord { DeviceId = "dev-1", DeviceName = "A", PreviousState = 1, CurrentState = 2, EventTime = new DateTime(2026, 1, 1, 10, 0, 0), ShiftName = "白班" },
            new StatusTransitionRecord { DeviceId = "dev-1", DeviceName = "A", PreviousState = 2, CurrentState = 1, EventTime = new DateTime(2026, 1, 1, 12, 0, 0), ShiftName = "白班" },
        });
        var vm = new StatusQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 12, 0, 0), null, 1, 2); // pageSize=2 → 2 页

        Assert.Equal(2, CountCsvLines(vm.BuildCsv()));      // 当前页 2 条数据行
        Assert.Equal(3, CountCsvLines(vm.BuildCsvAll()));   // 全量 3 条数据行
    }

    // ════════════════════ 报警贪心配对（补测 B1）════════════════════

    [Fact]
    public void Alarm_GreedyPairing_RecoverPairsWithMostRecentUnpairedTrigger()
    {
        // T1(9:00) → T2(9:05) → R(9:10)：同报警两次触发一次恢复。
        // 贪心配对下 R 与最近未配对触发 T2 配对（而非 T1/T2 双计），
        // 组合末事件为 R → 已恢复，无待恢复。
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 5, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 9, 10, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);

        Assert.Equal(2, vm.AlarmTriggerCount);
        Assert.Equal(1, vm.AlarmRecoverCount);
        Assert.Equal(0, vm.AlarmPendingCount); // 组合末事件为 R → 已恢复

        var t1 = vm.AlarmEvents.Single(e => e.EventType == AlarmEventType.Triggered && e.EventTime == new DateTime(2026, 1, 1, 9, 0, 0));
        var t2 = vm.AlarmEvents.Single(e => e.EventType == AlarmEventType.Triggered && e.EventTime == new DateTime(2026, 1, 1, 9, 5, 0));
        Assert.Null(t1.DurationText);          // 早期触发 T1 未配对 → 无时长
        Assert.Equal("5min", t2.DurationText); // 最近触发 T2 与 R 配对 → 5 分钟
        Assert.Equal("5min", vm.AlarmMttrText);
    }

    [Fact]
    public void Alarm_PairedTrigger_PairsAcrossSameAlarmAcrossDevices_Independent()
    {
        // 两台设备的同名报警恢复事件不应交叉配对。
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-2", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D2", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 10, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 9, 20, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);

        // 仅查 dev-1：dev-1 的 T→R 配对 20min，无待恢复
        Assert.Equal(1, vm.AlarmTriggerCount);
        Assert.Equal(1, vm.AlarmRecoverCount);
        Assert.Equal(0, vm.AlarmPendingCount);
        var trigger = vm.AlarmEvents.Single(e => e.EventType == AlarmEventType.Triggered);
        Assert.Equal("20min", trigger.DurationText);
        Assert.Equal("20min", vm.AlarmMttrText);
    }

    // ════════════════════ 班次切换不计待恢复（补测 B2）════════════════════

    [Fact]
    public void Alarm_ShiftChangeAsLastEvent_NotCountedAsPending()
    {
        // 最后事件为 ShiftChange 表示"报警在新班次重新开始计时"，不应算作待恢复。
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.ShiftChange, EventTime = new DateTime(2026, 1, 1, 9, 30, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);

        Assert.Equal(1, vm.AlarmTriggerCount);
        Assert.Equal(0, vm.AlarmRecoverCount);
        Assert.Equal(0, vm.AlarmPendingCount); // 末事件为 ShiftChange → 不算待恢复
    }

    // ════════════════════ 持续时长文本格式（补测 B3）════════════════════

    [Theory]
    [InlineData(0, "0min")]
    [InlineData(30, "30min")]
    [InlineData(59.4, "59min")]
    [InlineData(60, "1.0h")]
    [InlineData(90, "1.5h")]
    public void Alarm_FormatDurationText_Boundaries(double minutes, string expected)
    {
        Assert.Equal(expected, AlarmQueryViewModel.FormatDurationText(minutes));
    }

    // ════════════════════ 连锁触发洞察（补测 B4）════════════════════

    [Fact]
    public void Alarm_Insight_ReportsCorrelatedChain_WhenPatternRepeats()
    {
        // A/B 两报警在 5 分钟窗口内交替触发，使 "A→B" 组合出现 2 次 → 报告连锁触发。
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "A", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a2", AlarmName = "B", PlcAddress = "D2", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 1, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "A", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 2, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a2", AlarmName = "B", PlcAddress = "D2", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 3, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 10, 0, 0), null, null, 1, 50);

        Assert.NotNull(vm.AlarmInsight);
        Assert.Contains("A→B", vm.AlarmInsight);
    }

    [Fact]
    public void Alarm_Insight_NoCorrelation_WhenPatternAppearsOnce()
    {
        // A→B 仅出现 1 次，不满足 "≥2 次" 门槛 → 不报告连锁触发。
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "A", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a2", AlarmName = "B", PlcAddress = "D2", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 1, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a3", AlarmName = "C", PlcAddress = "D3", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 30, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 10, 0, 0), null, null, 1, 50);

        // 洞察仍应存在（Top 排行），但不含连锁触发段
        Assert.NotNull(vm.AlarmInsight);
        Assert.DoesNotContain("→", vm.AlarmInsight);
    }

    // ════════════════════ 待恢复时长排行洞察（补测 B5）════════════════════

    [Fact]
    public void Alarm_Insight_ReportsPendingDuration_WhenUnrecovered()
    {
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.Add(new AlarmEventRecord { DeviceId = "dev-1", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 9, 30, 0), null, null, 1, 50);

        Assert.NotNull(vm.AlarmInsight);
        Assert.Contains("高温", vm.AlarmInsight);
        Assert.Contains("30min", vm.AlarmInsight); // 9:00 → 区间终点 9:30
    }

    /// <summary>统计 CSV 中数据行数：数据行首列为时间戳（以数字开头），注释行以 # 或中文片段开头不计入。</summary>
    private static int CountCsvLines(string? csv)
    {
        if (string.IsNullOrEmpty(csv)) return 0;
        return csv.Split('\n')
            .Select(l => l.TrimStart())
            .Count(l => l.Length > 0 && char.IsDigit(l[0]));
    }
}
