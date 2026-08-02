using System.Collections.ObjectModel;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
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
    public void ScanAlarms_StateMissingAndLastEventTriggered_RebuildsAsActive()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        plc.SetBool(alarm.PlcAddress, true);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 预置历史：最近事件是 Triggered（无对应 Recovered），表示报警当前仍触发中
        history.AlarmEvents.Add(new AlarmEventRecord
        {
            AlarmId = alarm.Id,
            EventType = AlarmEventType.Triggered,
            EventTime = DateTime.Now.AddMinutes(-5)
        });

        // 首次扫描：从历史重建状态，应回填 StartTime 但不重复写入 Triggered
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.True(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);
        // 重建时不应产生新事件
        Assert.Single(history.AlarmEvents);
    }

    [Fact]
    public void ScanAlarms_StateMissingAndLastEventRecovered_RebuildsAsInactive()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        plc.SetBool(alarm.PlcAddress, true);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 预置历史：最近事件是 Recovered，表示报警已恢复
        history.AlarmEvents.Add(new AlarmEventRecord
        {
            AlarmId = alarm.Id,
            EventType = AlarmEventType.Recovered,
            EventTime = DateTime.Now.AddMinutes(-5)
        });

        // 首次扫描：从历史重建为 false，但 PLC 当前为 true → 触发新的上升沿
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        Assert.Equal(2, history.AlarmEvents.Count);
        Assert.Equal(AlarmEventType.Triggered, history.AlarmEvents[1].EventType);
        Assert.True(tracker.GetPrevAlarmStatesSnapshot()[alarm.Id]);
    }

    [Fact]
    public void ScanAlarms_StateMissingAndLastEventShiftChange_RebuildsAsInactive()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        plc.SetBool(alarm.PlcAddress, true);
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 预置历史：最近事件是 ShiftChange（班次切换），新班次按未触发处理
        history.AlarmEvents.Add(new AlarmEventRecord
        {
            AlarmId = alarm.Id,
            EventType = AlarmEventType.ShiftChange,
            EventTime = DateTime.Now.AddMinutes(-5)
        });

        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 重建为 false + PLC 当前 true → 触发新的上升沿
        Assert.Equal(2, history.AlarmEvents.Count);
        Assert.Equal(AlarmEventType.Triggered, history.AlarmEvents[1].EventType);
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
    public void LogShiftChangeForActiveAlarms_WriteFails_AddsToFailedSet()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发报警
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 配置写入失败
        history.ShouldFailAlarmEventWrite = true;
        tracker.LogShiftChangeForActiveAlarms(devices, history, "白班", Logger);

        // 应记入 _shiftChangeFailedAlarms
        Assert.Contains(alarm.Id, tracker.GetShiftChangeFailedAlarmsSnapshot());
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
        Assert.Contains(alarm.Id, tracker.GetShiftChangeFailedAlarmsSnapshot());

        // ResetShift 清空 _prevAlarmStates（生产中由 PlcDataAcquisitionService.ResetShift 调用）
        tracker.ResetAll();

        // 下一轮扫描：_shiftChangeFailedAlarms 应被消费并跳过历史查询，直接当作未触发
        // PLC 仍为 true → 触发新的上升沿
        history.ShouldFailAlarmEventWrite = false;
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);

        // 新写入一条 Triggered 事件
        var triggered = history.AlarmEvents.Count(e => e.EventType == AlarmEventType.Triggered);
        Assert.Equal(2, triggered);
        // _shiftChangeFailedAlarms 应已被消费移除
        Assert.DoesNotContain(alarm.Id, tracker.GetShiftChangeFailedAlarmsSnapshot());
    }

    // ──────────── RemoveAlarmState / RemoveDeviceAlarms ────────────

    [Fact]
    public void RemoveAlarmState_ClearsBothDictionaries()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发报警 + 模拟班次切换失败
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);
        history.ShouldFailAlarmEventWrite = true;
        tracker.LogShiftChangeForActiveAlarms(devices, history, "白班", Logger);

        // 删除报警状态
        tracker.RemoveAlarmState(alarm.Id);

        Assert.False(tracker.GetPrevAlarmStatesSnapshot().ContainsKey(alarm.Id));
        Assert.DoesNotContain(alarm.Id, tracker.GetShiftChangeFailedAlarmsSnapshot());
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
    public void ResetAll_ClearsPrevAlarmStatesButKeepsShiftChangeFailedSet()
    {
        var tracker = new AlarmStateTracker();
        var (device, alarm) = BuildDeviceWithAlarm();
        var plc = new FakePlcDriver();
        var history = new InMemoryHistoryService();
        var devices = new ObservableCollection<Device> { device };

        // 触发报警 + 班次切换写入失败
        plc.SetBool(alarm.PlcAddress, true);
        tracker.ScanAlarms(devices, plc, history, "白班", Logger);
        history.ShouldFailAlarmEventWrite = true;
        tracker.LogShiftChangeForActiveAlarms(devices, history, "白班", Logger);

        tracker.ResetAll();

        // _prevAlarmStates 应清空
        Assert.Empty(tracker.GetPrevAlarmStatesSnapshot());
        // _shiftChangeFailedAlarms 应保留（按设计，由 ScanAlarms 消费一次后自动移除）
        Assert.Contains(alarm.Id, tracker.GetShiftChangeFailedAlarmsSnapshot());
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
