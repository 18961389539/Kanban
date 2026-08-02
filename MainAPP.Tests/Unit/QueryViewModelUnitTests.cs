using System;
using System.IO;
using System.Linq;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
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
}
