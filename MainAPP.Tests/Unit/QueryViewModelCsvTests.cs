using System;
using System.IO;
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
/// 4 个查询 ViewModel 的 BuildCsv 方法单元测试。
/// 通过 <see cref="InMemoryHistoryService"/> 桩注入内存数据，验证空数据返回 null、
/// 有数据返回非空 CSV、CSV 包含表头/数据行/KPI 摘要注释等核心契约。
/// 不触发任何 SQLite 文件 IO，与 <see cref="QueryViewModelUnitTests"/> 同属纯单元测试。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class QueryViewModelCsvTests
{
    private static readonly DateTime From = new DateTime(2026, 1, 1, 10, 0, 0);
    private static readonly DateTime To = new DateTime(2026, 1, 1, 14, 0, 0);

    private static (AppSettings AppSettings, DeviceRepository Repo) NewRepo(string deviceId = "dev-1")
    {
        var appSettings = new AppSettings { ConfigDirectory = Path.GetTempPath() };
        var repo = new DeviceRepository(appSettings);
        repo.Devices.Add(new Device { Id = deviceId, Name = "设备" + deviceId, TargetCycle = 600 });
        return (appSettings, repo);
    }

    // ════════════════ ProductionQueryViewModel ════════════════

    [Fact]
    public void Production_BuildCsv_NoQuery_ReturnsNull()
    {
        var vm = new ProductionQueryViewModel(new InMemoryHistoryService());
        Assert.Null(vm.BuildCsv(From, To));
    }

    [Fact]
    public void Production_BuildCsv_AfterQuery_ReturnsNonNull()
    {
        var vm = BuildProductionVmWithData();
        var csv = vm.BuildCsv(From, To);
        Assert.NotNull(csv);
    }

    [Fact]
    public void Production_BuildCsv_ContainsHeader()
    {
        var vm = BuildProductionVmWithData();
        var csv = vm.BuildCsv(From, To);
        Assert.NotNull(csv);
        Assert.Contains("时间,设备ID,设备名称,班次,OK产量,NG产量,状态字", csv!);
    }

    [Fact]
    public void Production_BuildCsv_ContainsDataRow()
    {
        var vm = BuildProductionVmWithData();
        var csv = vm.BuildCsv(From, To);
        Assert.NotNull(csv);
        Assert.Contains("dev-1", csv!);
    }

    [Fact]
    public void Production_BuildCsv_ContainsKpiSummary()
    {
        var vm = BuildProductionVmWithData();
        var csv = vm.BuildCsv(From, To);
        Assert.NotNull(csv);
        Assert.Contains("# 总 OK", csv!);
    }

    private static ProductionQueryViewModel BuildProductionVmWithData()
    {
        var hs = new InMemoryHistoryService();
        hs.ProductionLogs.AddRange(new[]
        {
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 0, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 8, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 200, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 10, 0, 0) },
            new ProductionLog { DeviceId = "dev-1", DeviceName = "A", ShiftName = "白班", OkProduction = 400, NgProduction = 0, StatusWord = 1, Timestamp = new DateTime(2026, 1, 1, 14, 0, 0) },
        });
        var vm = new ProductionQueryViewModel(hs);
        vm.Query("dev-1", From, To, null, 1, 50);
        return vm;
    }

    // ════════════════ StatusQueryViewModel ════════════════

    [Fact]
    public void Status_BuildCsv_NoQuery_ReturnsNull()
    {
        var vm = new StatusQueryViewModel(new InMemoryHistoryService());
        Assert.Null(vm.BuildCsv());
    }

    [Fact]
    public void Status_BuildCsv_AfterQuery_ReturnsNonNull()
    {
        var vm = BuildStatusVmWithData();
        var csv = vm.BuildCsv();
        Assert.NotNull(csv);
    }

    [Fact]
    public void Status_BuildCsv_ContainsHeader()
    {
        var vm = BuildStatusVmWithData();
        var csv = vm.BuildCsv();
        Assert.NotNull(csv);
        Assert.Contains("事件时间,设备ID,设备名称,前一状态,当前状态,前一状态文本,当前状态文本", csv!);
    }

    [Fact]
    public void Status_BuildCsv_ContainsDataRow()
    {
        var vm = BuildStatusVmWithData();
        var csv = vm.BuildCsv();
        Assert.NotNull(csv);
        Assert.Contains("dev-1", csv!);
    }

    [Fact]
    public void Status_BuildCsv_ContainsKpiSummary()
    {
        var vm = BuildStatusVmWithData();
        var csv = vm.BuildCsv();
        Assert.NotNull(csv);
        Assert.Contains("# 运行时长", csv!);
    }

    private static StatusQueryViewModel BuildStatusVmWithData()
    {
        var hs = new InMemoryHistoryService();
        hs.StatusTransitions.AddRange(new[]
        {
            new StatusTransitionRecord { DeviceId = "dev-1", DeviceName = "A", PreviousState = 0, CurrentState = 1, EventTime = new DateTime(2026, 1, 1, 8, 0, 0), ShiftName = "白班" },
            new StatusTransitionRecord { DeviceId = "dev-1", DeviceName = "A", PreviousState = 1, CurrentState = 2, EventTime = new DateTime(2026, 1, 1, 10, 0, 0), ShiftName = "白班" },
            new StatusTransitionRecord { DeviceId = "dev-1", DeviceName = "A", PreviousState = 2, CurrentState = 1, EventTime = new DateTime(2026, 1, 1, 12, 0, 0), ShiftName = "白班" },
        });
        var vm = new StatusQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 12, 0, 0), null, 1, 50);
        return vm;
    }

    // ════════════════ OeeQueryViewModel ════════════════

    [Fact]
    public void Oee_BuildCsv_NoQuery_ReturnsNull()
    {
        var (appSettings, repo) = NewRepo();
        var vm = new OeeQueryViewModel(new InMemoryHistoryService(), repo, appSettings);
        Assert.Null(vm.BuildCsv("dev-1"));
    }

    [Fact]
    public void Oee_BuildCsv_AfterQuery_ReturnsNonNull()
    {
        var vm = BuildOeeVmWithData(out var deviceId);
        var csv = vm.BuildCsv(deviceId);
        Assert.NotNull(csv);
    }

    [Fact]
    public void Oee_BuildCsv_ContainsHeader()
    {
        var vm = BuildOeeVmWithData(out var deviceId);
        var csv = vm.BuildCsv(deviceId);
        Assert.NotNull(csv);
        Assert.Contains("指标,值", csv!);
    }

    [Fact]
    public void Oee_BuildCsv_ContainsMetricRows()
    {
        var vm = BuildOeeVmWithData(out var deviceId);
        var csv = vm.BuildCsv(deviceId);
        Assert.NotNull(csv);
        Assert.Contains("C良品率", csv!);
        Assert.Contains("B性能达标率", csv!);
        Assert.Contains("A时间稼动率", csv!);
        Assert.Contains("OEE综合", csv!);
    }

    [Fact]
    public void Oee_BuildCsv_ContainsKpiSummary()
    {
        var vm = BuildOeeVmWithData(out var deviceId);
        var csv = vm.BuildCsv(deviceId);
        Assert.NotNull(csv);
        Assert.Contains("# 设备：", csv!);
    }

    private static OeeQueryViewModel BuildOeeVmWithData(out string deviceId)
    {
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
        vm.Query("dev-1", From, To, null);
        deviceId = "dev-1";
        return vm;
    }

    // ════════════════ AlarmQueryViewModel ════════════════

    [Fact]
    public void Alarm_BuildCsv_NoQuery_ReturnsNull()
    {
        var vm = new AlarmQueryViewModel(new InMemoryHistoryService());
        Assert.Null(vm.BuildCsv());
    }

    [Fact]
    public void Alarm_BuildCsv_AfterQuery_ReturnsNonNull()
    {
        var vm = BuildAlarmVmWithData();
        var csv = vm.BuildCsv();
        Assert.NotNull(csv);
    }

    [Fact]
    public void Alarm_BuildCsv_ContainsHeader()
    {
        var vm = BuildAlarmVmWithData();
        var csv = vm.BuildCsv();
        Assert.NotNull(csv);
        Assert.Contains("事件时间,设备ID,设备名称,报警ID,报警名称,PLC地址,事件类型,事件类型文本", csv!);
    }

    [Fact]
    public void Alarm_BuildCsv_ContainsDataRow()
    {
        var vm = BuildAlarmVmWithData();
        var csv = vm.BuildCsv();
        Assert.NotNull(csv);
        Assert.Contains("dev-1", csv!);
    }

    [Fact]
    public void Alarm_BuildCsv_ContainsKpiSummary()
    {
        var vm = BuildAlarmVmWithData();
        var csv = vm.BuildCsv();
        Assert.NotNull(csv);
        Assert.Contains("# 触发", csv!);
    }

    private static AlarmQueryViewModel BuildAlarmVmWithData()
    {
        var hs = new InMemoryHistoryService();
        hs.AlarmEvents.AddRange(new[]
        {
            new AlarmEventRecord { DeviceId = "dev-1", DeviceName = "A", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 9, 0, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", DeviceName = "A", AlarmId = "a1", AlarmName = "高温", PlcAddress = "D1", EventType = AlarmEventType.Recovered, EventTime = new DateTime(2026, 1, 1, 9, 30, 0) },
            new AlarmEventRecord { DeviceId = "dev-1", DeviceName = "A", AlarmId = "a2", AlarmName = "低压", PlcAddress = "D2", EventType = AlarmEventType.Triggered, EventTime = new DateTime(2026, 1, 1, 10, 0, 0) },
        });
        var vm = new AlarmQueryViewModel(hs);
        vm.Query("dev-1", new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 23, 0, 0), null, null, 1, 50);
        return vm;
    }
}
