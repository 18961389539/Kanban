using System.Collections.ObjectModel;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// AlarmStateTracker 单元测试：覆盖报警边沿检测、内存状态重建、
/// 班次切换失败恢复、设备/报警删除清理等核心路径。
///
/// AlarmStateTracker 是 PlcDataAcquisitionService 拆分出的协作组件，
/// 拥有 _prevAlarmStates 与 _shiftChangeFailedAlarms 两个状态字典及专用锁。
/// 此测试文件独立验证其行为，不依赖 PlcDataAcquisitionService 整体。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class AlarmStateTrackerTests
{
    private sealed class RecordingNotificationChannel : IAlarmNotificationChannel
    {
        public List<AlarmNotification> Notifications { get; } = [];

        public void Enqueue(AlarmNotification notification) => Notifications.Add(notification);
    }

    private static readonly ILogger<AlarmStateTracker> Logger =
        NullLogger<AlarmStateTracker>.Instance;

    /// <summary>构造一台带报警的设备，报警 PlcAddress 设值时自动回填 Id。</summary>
    private static (Device device, Alarm alarm) BuildDeviceWithAlarm(
        string deviceId = "dev-001",
        string alarmAddr = "M100")
    {
        var device = new Device
        {
            Id = deviceId,
            Name = "测试设备1",
        };
        device.Alarms.Add(new Alarm
        {
            DeviceId = device.Id,
            Name = "测试报警1",
            PlcAddress = alarmAddr,
            Level = AlarmLevel.High
        });
        return (device, device.Alarms.First());
    }

    // ──────────── ScanAlarms：边沿检测 ────────────

    [Fact]
    public void ScanAlarms_FirstScanFalse_InitializesStateNoEvent()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        plc.SetBool(alarm.PlcAddress, false);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        var ok = tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.True(ok);
        Assert.Empty(history.AlarmEvents);
        var snapshot = tracker.GetPrevAlarmStatesSnapshot();
        Assert.False(snapshot[alarm.Id]);
    }

    [Fact]
    public void ScanAlarms_RisingEdge_LogsTriggeredEvent()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 首次扫描：初始化为 false
        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 上升沿：PLC 置位 → Triggered 事件
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        var evt = Assert.Single(history.AlarmEvents);
        Assert.Equal(AlarmEventType.Triggered, evt.EventType);
        Assert.Equal(alarm.Id, evt.AlarmId);
        Assert.Equal("白班", evt.ShiftName);
        Assert.True(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);
    }

    [Fact]
    public void ScanAlarms_RisingEdge_NotifiesOnceWithAlarmDetails()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var notifications = new RecordingNotificationChannel();
        var devices = new ObservableCollection<Device> { device };

        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger, notifications);

        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger, notifications);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger, notifications);

        var notification = Assert.Single(notifications.Notifications);
        Assert.Equal(device.Id, notification.DeviceId);
        Assert.Equal(alarm.Id, notification.AlarmId);
        Assert.Equal(AlarmLevel.High, notification.Level);
    }

    [Fact]
    public void ScanAlarms_RisingEdge_InvokesOnAlarmEdgeWithContractDto()
    {
        // 锁住 Remote 事件流修复：检测层成功落库后必须以 Contracts.Dtos.AlarmEventDto
        // 形式回调（喂给 Collector 的 EventBroadcaster.PublishAlarmEvent）。
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };
        var edges = new List<AlarmEventDto>();

        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger, onAlarmEdge: edges.Add);

        var evt = Assert.Single(edges);
        Assert.Equal(device.Id, evt.DeviceId);
        Assert.Equal(device.Name, evt.DeviceName);
        Assert.Equal(alarm.Id, evt.AlarmId);
        Assert.Equal(alarm.Name, evt.AlarmName);
        Assert.Equal(alarm.PlcAddress, evt.PlcAddress);
        Assert.Equal(Kanban.Contracts.Enums.AlarmEventType.Triggered, evt.EventType);
        Assert.Equal(Kanban.Contracts.Enums.AlarmLevel.High, evt.Level);
        Assert.Equal("白班", evt.ShiftName);
    }

    [Fact]
    public void ScanAlarms_FallingEdge_InvokesOnAlarmEdgeWithRecoveredDto()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };
        var edges = new List<AlarmEventDto>();

        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);
        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger, onAlarmEdge: edges.Add);

        var evt = Assert.Single(edges);
        Assert.Equal(Kanban.Contracts.Enums.AlarmEventType.Recovered, evt.EventType);
    }

    [Fact]
    public void ScanAlarms_NullOnAlarmEdge_DoesNotThrow()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        plc.SetBool(alarm.PlcAddress, true);
        // 未传 onAlarmEdge（旧调用方）必须继续工作
        tracker.ScanAlarms(devices, plc, history, "白班", Logger, onAlarmEdge: null);
        Assert.Single(history.AlarmEvents);
    }

    [Fact]
    public void ScanAlarms_FallingEdge_LogsRecoveredEvent()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发上升沿
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);
        Assert.Single(history.AlarmEvents);

        // 下降沿：PLC 复位 → Recovered 事件
        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.Equal(2, history.AlarmEvents.Count);
        Assert.Equal(AlarmEventType.Recovered, history.AlarmEvents[1].EventType);
        Assert.False(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);
    }

    [Fact]
    public void ScanAlarms_StateUnchanged_NoEventWritten()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 连续两次相同状态：不应写入事件
        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.Empty(history.AlarmEvents);
    }

    [Fact]
    public void ScanAlarms_ReadFails_ReturnsFalseAndPreservesState()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        plc.SetFailing(alarm.PlcAddress);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        var ok = tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.False(ok);
        Assert.Empty(history.AlarmEvents);
        // 读取失败时不应建立/修改状态
        Assert.False(tracker.GetPrevAlarmStatesSnapshot().ContainsKey(alarm.Id));
    }

    [Fact]
    public void ScanAlarms_ContiguousAddresses_UsesOneBoolBatch()
    {
        var tracker = new AlarmStateTracker();
        var device = new Device { Id = "dev-1", Name = "设备1" };
        device.Alarms.Add(new Alarm { DeviceId = device.Id, PlcAddress = "M10", Name = "A1" });
        device.Alarms.Add(new Alarm { DeviceId = device.Id, PlcAddress = "M11", Name = "A2" });
        device.Alarms.Add(new Alarm { DeviceId = device.Id, PlcAddress = "M12", Name = "A3" });

        var plc = new FakePlcDriver();
        plc.SetBool("M10", false);
        plc.SetBool("M11", true);
        plc.SetBool("M12", false);
        var history = new InMemoryHistoryService();

        var ok = tracker.ScanAlarms([device], plc, history, "白班", Logger);

        Assert.True(ok);
        Assert.Equal(1, plc.ReadBoolBatchCallCount);
        Assert.Equal(("M10", (ushort)3), Assert.Single(plc.ReadBoolBatchHistory));
        Assert.Equal(0, plc.ReadBoolCallCount);
        Assert.Single(history.AlarmEvents);
        Assert.Equal("A2", history.AlarmEvents[0].AlarmName);
    }

    [Fact]
    public void ScanAlarms_BatchFailure_FallsBackToSingleReads()
    {
        var tracker = new AlarmStateTracker();
        var device = new Device { Id = "dev-1", Name = "设备1" };
        device.Alarms.Add(new Alarm { DeviceId = device.Id, PlcAddress = "M10", Name = "A1" });
        device.Alarms.Add(new Alarm { DeviceId = device.Id, PlcAddress = "M11", Name = "A2" });

        var plc = new FakePlcDriver();
        plc.SetBool("M10", false);
        plc.SetBool("M11", true);
        plc.SetFailing("M11");
        var history = new InMemoryHistoryService();

        var ok = tracker.ScanAlarms([device], plc, history, "白班", Logger);

        Assert.False(ok);
        Assert.Equal(1, plc.ReadBoolBatchCallCount);
        Assert.Equal(2, plc.ReadBoolCallCount);
        Assert.Empty(history.AlarmEvents);
        Assert.False(tracker.GetPrevAlarmStatesSnapshot().ContainsKey(device.Alarms[1].Id));
    }

    // ──────────── ScanAlarms：写失败重试 ────────────

    [Fact]
    public void ScanAlarms_TriggeredWriteFails_DoesNotUpdateState()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 首次初始化为 false
        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 配置 DB 写入失败 + 上升沿
        history.ShouldFailAlarmEventWrite = true;
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 写失败时 _prevAlarmStates 不应更新为 true，下次会重新尝试
        Assert.False(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);

        // 解除失败后下一轮应成功写入
        history.ShouldFailAlarmEventWrite = false;
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);
        Assert.Single(history.AlarmEvents);
        Assert.True(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);
    }

    [Fact]
    public void ScanAlarms_RecoveredWriteFails_DoesNotUpdateState()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发上升沿
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 配置 DB 写入失败 + 下降沿
        history.ShouldFailAlarmEventWrite = true;
        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 写失败时 _prevAlarmStates 仍为 true，下次会重新尝试 Recovered
        Assert.True(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);

        history.ShouldFailAlarmEventWrite = false;
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);
        Assert.Equal(2, history.AlarmEvents.Count);
        Assert.False(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);
    }

    // ──────────── ScanAlarms：状态重建 ────────────

    [Fact]
    public void ScanAlarms_StateMissingAndActiveStateRow_RebuildsAsActive()
    {
        var activeStates = new InMemoryActiveAlarmStateService();
        var tracker = new AlarmStateTracker(activeStates);
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        plc.SetBool(alarm.PlcAddress, true);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 预置状态表：活跃行（触发时间 2 分钟前，模拟跨重启仍触发）
        var triggeredAt = DateTime.Now.AddMinutes(-2);
        activeStates.UpsertActive(device.Id, device.Name, alarm.Id, alarm.Name, alarm.PlcAddress, true, triggeredAt);

        // 首次扫描：从状态表重建为触发态，回填 StartTime，且不重复写入 Triggered 事件
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.True(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);
        Assert.Empty(history.AlarmEvents);
        Assert.Equal(triggeredAt, device.Alarms[0].StartTime);
    }

    [Fact]
    public void ScanAlarms_StateMissingAndNoActiveStateRow_RebuildsAsInactive()
    {
        // 状态表无行（等价旧模型"无历史/陈旧 Triggered"）→ 重建未触发；
        // PLC 为 ON 走正常触发沿（StartTime = now），并同步落状态表活跃行。
        var activeStates = new InMemoryActiveAlarmStateService();
        var tracker = new AlarmStateTracker(activeStates);
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        plc.SetBool(alarm.PlcAddress, true);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.Single(history.AlarmEvents);
        Assert.Equal(AlarmEventType.Triggered, history.AlarmEvents[0].EventType);
        Assert.True(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);
        Assert.True(activeStates.GetRow(device.Id, alarm.Id)?.IsActive);
    }

    // ──────────── LogShiftChangeForActiveAlarms ────────────

    [Fact]
    public void LogShiftChangeForActiveAlarms_WritesShiftChangeEventForActiveAlarms()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 先触发报警（处于 active 状态）
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 班次切换：写 EventType=ShiftChange
        tracker.LogShiftChangeForActiveAlarms(devices, history, "白班", Logger);

        Assert.Equal(2, history.AlarmEvents.Count);
        Assert.Equal(AlarmEventType.ShiftChange, history.AlarmEvents[1].EventType);
    }

    [Fact]
    public void LogShiftChangeForActiveAlarms_InactiveAlarm_NoEventWritten()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 报警未触发（false）
        plc.SetBool(alarm.PlcAddress, false);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        tracker.LogShiftChangeForActiveAlarms(devices, history, "白班", Logger);

        Assert.Empty(history.AlarmEvents);
    }

    [Fact]
    public void LogShiftChangeForActiveAlarms_WriteFails_DoesNotThrowAndEventNotPersisted()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发报警
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 配置写入失败：写失败不抛异常、不落库（旧"失败补偿集合"机制已移除，
        // 状态表 Upsert 幂等，新班次若仍触发会重新落表）
        history.ShouldFailAlarmEventWrite = true;
        tracker.LogShiftChangeForActiveAlarms(devices, history, "白班", Logger);

        Assert.Single(history.AlarmEvents); // 仅保留原 Triggered
    }

    [Fact]
    public void ScanAlarms_AfterShiftChangeWriteFailed_TreatsAsUntriggeredAndLogsNewRisingEdge()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发报警
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 班次切换写入失败
        history.ShouldFailAlarmEventWrite = true;
        tracker.LogShiftChangeForActiveAlarms(devices, history, "白班", Logger);

        // ResetShift 清空 _prevAlarmStates（生产中由 PlcDataAcquisitionService.ResetShift 调用；
        // 状态表行由 PlcScanPipeline.ResetAll 按设备清除）
        tracker.ResetAll();

        // 下一轮扫描：无状态表注入时重建走"未触发"，PLC 仍为 true → 触发新的上升沿
        history.ShouldFailAlarmEventWrite = false;
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 新写入一条 Triggered 事件
        var triggered = history.AlarmEvents.Count(e => e.EventType == AlarmEventType.Triggered);
        Assert.Equal(2, triggered);
    }

    // ──────────── RemoveAlarmState / RemoveDeviceAlarms ────────────

    [Fact]
    public void RemoveAlarmState_ClearsPrevState()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发报警
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 删除报警状态
        tracker.RemoveAlarmState(alarm.Id);

        Assert.False(tracker.GetPrevAlarmStatesSnapshot().ContainsKey(alarm.Id));
    }

    [Fact]
    public void RemoveDeviceAlarms_ClearsAllAlarmsOfDevice()
    {
        var tracker = new AlarmStateTracker();
        var device = new Device { Id = "dev-1", Name = "设备1" };
        device.Alarms.Add(new Alarm { DeviceId = "dev-1", PlcAddress = "M10", Name = "报警A" });
        device.Alarms.Add(new Alarm { DeviceId = "dev-1", PlcAddress = "M11", Name = "报警B" });
        var alarmIds = device.Alarms.Select(a => a.Id).ToList();

        // 预置状态
        foreach (var id in alarmIds)
            tracker.SetPrevAlarmStateForTest(id, true);

        tracker.RemoveDeviceAlarms(device);

        var snapshot = tracker.GetPrevAlarmStatesSnapshot();
        Assert.All(alarmIds, id => Assert.False(snapshot.ContainsKey(id)));
    }

    [Fact]
    public void RemoveDeviceAlarms_NullDevice_NoThrow()
    {
        var tracker = new AlarmStateTracker();
        tracker.RemoveDeviceAlarms(null!);
    }

    // ──────────── ResetAll ────────────

    [Fact]
    public void ResetAll_ClearsPrevAlarmStates()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发报警
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        tracker.ResetAll();

        // _prevAlarmStates 应清空（新班次后由状态表重建）
        Assert.Empty(tracker.GetPrevAlarmStatesSnapshot());
    }

    // ──────────── 无效地址 ────────────

    [Fact]
    public void ScanAlarms_InvalidAddress_SkipsAndContinuesOthers()
    {
        var tracker = new AlarmStateTracker();
        var device = new Device { Id = "dev-1", Name = "设备1" };
        // 报警A：无效地址（非 M 位）
        device.Alarms.Add(new Alarm { DeviceId = "dev-1", PlcAddress = "D100", Name = "报警A" });
        // 报警B：有效 M 位
        device.Alarms.Add(new Alarm { DeviceId = "dev-1", PlcAddress = "M200", Name = "报警B" });
        var validAlarm = device.Alarms.First(a => a.PlcAddress == "M200");

        var plc = new FakePlcDriver();
        plc.SetBool(validAlarm.PlcAddress, true);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        var ok = tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 无效地址的报警被跳过，有效报警正常处理
        Assert.True(ok);
        Assert.Single(history.AlarmEvents);
        Assert.Equal(validAlarm.Id, history.AlarmEvents[0].AlarmId);
    }

    [Fact]
    public void ScanAlarms_EmptyAddress_Skips()
    {
        var tracker = new AlarmStateTracker();
        var device = new Device { Id = "dev-1", Name = "设备1" };
        device.Alarms.Add(new Alarm { DeviceId = "dev-1", PlcAddress = "", Name = "空地址报警" });
        device.Alarms.Add(new Alarm { DeviceId = "dev-1", PlcAddress = "   ", Name = "空白报警" });

        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        var ok = tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.True(ok);
        Assert.Empty(history.AlarmEvents);
    }

    // ──────────── 多设备多报警 ────────────

    [Fact]
    public void ScanAlarms_MultipleDevicesAndAlarms_AllProcessed()
    {
        var tracker = new AlarmStateTracker();
        var device1 = new Device { Id = "dev-1", Name = "设备1" };
        device1.Alarms.Add(new Alarm { DeviceId = "dev-1", PlcAddress = "M10", Name = "A1" });
        device1.Alarms.Add(new Alarm { DeviceId = "dev-1", PlcAddress = "M11", Name = "A2" });
        var device2 = new Device { Id = "dev-2", Name = "设备2" };
        device2.Alarms.Add(new Alarm { DeviceId = "dev-2", PlcAddress = "M20", Name = "B1" });

        var plc = new FakePlcDriver();
        plc.SetBool("M10", true);
        plc.SetBool("M11", false);
        plc.SetBool("M20", true);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device1, device2 };

        var ok = tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.True(ok);
        // M10 与 M20 上升沿，M11 初始化为 false 不写事件
        Assert.Equal(2, history.AlarmEvents.Count);
        Assert.All(history.AlarmEvents, e => Assert.Equal(AlarmEventType.Triggered, e.EventType));
        Assert.Equal(3, tracker.GetPrevAlarmStatesSnapshot().Count);
    }

    // ──────────── 测试访问助手 ────────────

    [Fact]
    public void SetPrevAlarmStateForTest_SetsStateDirectly()
    {
        var tracker = new AlarmStateTracker();
        tracker.SetPrevAlarmStateForTest("test-alarm", true);
        Assert.True(tracker.GetPrevAlarmStatesSnapshot()["test-alarm"]);
    }

    [Fact]
    public void ClearPrevAlarmStateForTest_RemovesOnlySpecifiedAlarm()
    {
        var tracker = new AlarmStateTracker();
        tracker.SetPrevAlarmStateForTest("a1", true);
        tracker.SetPrevAlarmStateForTest("a2", true);

        tracker.ClearPrevAlarmStateForTest("a1");

        var snapshot = tracker.GetPrevAlarmStatesSnapshot();
        Assert.False(snapshot.ContainsKey("a1"));
        Assert.True(snapshot.ContainsKey("a2"));
    }
}
