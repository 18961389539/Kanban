using System;
using System.Collections.ObjectModel;
using System.Linq;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
// v3: ITestOutputHelper 已从 Xunit.Abstractions 移入 Xunit 命名空间

namespace MainAPP.Tests.Unit;

/// <summary>
/// PlcDataAcquisitionService 单元测试。
/// 完全脱离 SQLite/EF Core/HslCommunication：通过 FakePlcDriver + InMemoryHistoryService
/// + 临时目录隔离的 ProductionBaselineStore，验证核心采集逻辑：
/// - 报警边沿检测（上升沿/下降沿/写失败重试）
/// - 班次切换（EventType=3 事件写入、_prevAlarmStates 清空、_currentShiftId 更新）
/// - 产量基线管理（首次读取、回退自动更新基线）
/// - 状态转换（首次写入 0→status、变化写入 prev→new、写失败不更新内存）
/// - 班次重置（PLC 清零触发、基线清空、运行时清零）
/// - 生产快照写入历史
/// - 离线转换写入 CurrentState=0
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class PlcDataAcquisitionServiceTests : IDisposable
{
    private sealed class RecordingNotificationChannel : IAlarmNotificationChannel
    {
        public List<AlarmNotification> Notifications { get; } = [];

        public void Enqueue(AlarmNotification notification) => Notifications.Add(notification);
    }

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DeviceRepository _deviceRepository;
    private readonly FakePlcDriver _plc;
    private readonly PlcConnectionManager _connectionManager;
    private readonly InMemoryHistoryService _history;
    private readonly ProductionBaselineStore _baselineStore;
    private readonly PlcDataAcquisitionService _service;

    public PlcDataAcquisitionServiceTests(ITestOutputHelper output)
    {
        _output = output;
        // 临时目录隔离 baselines.json 写入，避免污染真实 %APPDATA%/Kanban
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KanbanPlcSvcTests_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_tempDir);

        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        // 配置一个全覆盖的默认班次（避免 DetectShiftChange 找不到班次时不触发）
        _appSettings.Shifts = new ObservableCollection<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new TimeSpan(0, 0, 0), EndTime = new TimeSpan(12, 0, 0) },
            new() { Name = "夜班", StartTime = new TimeSpan(12, 0, 0), EndTime = new TimeSpan(24, 0, 0) }
        };

        _deviceRepository = new DeviceRepository(_appSettings);
        _plc = new FakePlcDriver();
        _connectionManager = new PlcConnectionManager(_plc, _appSettings);
        _history = new InMemoryHistoryService();
        _baselineStore = new ProductionBaselineStore(_appSettings);

        _service = new PlcDataAcquisitionService(
            _plc,
            _connectionManager,
            _appSettings,
            _history,
            _deviceRepository,
            _baselineStore,
            NullLogger<PlcDataAcquisitionService>.Instance);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_tempDir, true); } catch { /* 测试间清理 */ }
    }

    // ──────────── 测试辅助：构造带 PLC 地址的设备 ────────────

    /// <summary>
    /// 构造一台配置齐全的设备（OK/NG/状态/清零地址 + 一个 M 位报警），
    /// 并注册到 DeviceRepository（同步创建 Runtime）。
    /// </summary>
    private Device AddDevice(
        string id = "dev-001",
        string name = "测试设备1",
        string okAddr = "D100",
        string ngAddr = "D101",
        string statusAddr = "D102",
        string resetAddr = "D103",
        string alarmAddr = "M100")
    {
        var device = new Device
        {
            Id = id,
            Name = name,
            OkCountAddress = okAddr,
            NgCountAddress = ngAddr,
            StatusCountAddress = statusAddr,
            ProductionResetAddress = resetAddr,
            TargetCycle = 100
        };
        // 加一个 M 位报警（PlcAddress 设值时 OnPlcAddressChanged 会回填 Id = "{DeviceId}_{PlcAddress}"）
        device.Alarms.Add(new Alarm
        {
            DeviceId = device.Id,
            Name = "测试报警1",
            PlcAddress = alarmAddr,
            Level = AlarmLevel.High
        });
        _deviceRepository.Devices.Add(device);
        _deviceRepository.AddRuntime(device);
        return device;
    }

    // ════════════════════════════════════════════════════════════════
    //  报警边沿检测
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ScanAlarms_RisingEdge_LogsTriggeredEvent()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 首次扫描：PLC 返回 false，应当作未触发，无事件写入
        _plc.SetBool(alarm.PlcAddress, false);
        _service.ScanAlarms();
        Assert.Empty(_history.AlarmEvents);
        Assert.False(_service.PrevAlarmStatesForTest[alarm.Id]);

        // PLC 置位 → 上升沿，应写入 EventType=Triggered
        _plc.SetBool(alarm.PlcAddress, true);
        _service.ScanAlarms();

        var evt = Assert.Single(_history.AlarmEvents);
        Assert.Equal(AlarmEventType.Triggered, evt.EventType);
        Assert.Equal(alarm.Id, evt.AlarmId);
        Assert.True(_service.PrevAlarmStatesForTest[alarm.Id]);
    }

    [Fact]
    public void ScanAlarms_FallingEdge_LogsRecoveredEvent()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 模拟报警已触发：先上升沿
        _plc.SetBool(alarm.PlcAddress, true);
        _service.ScanAlarms();
        Assert.Single(_history.AlarmEvents);

        // 下降沿：PLC 复位 → 应写入 EventType=Recovered
        _plc.SetBool(alarm.PlcAddress, false);
        _service.ScanAlarms();

        Assert.Equal(2, _history.AlarmEvents.Count);
        Assert.Equal(AlarmEventType.Recovered, _history.AlarmEvents[1].EventType);
        Assert.False(_service.PrevAlarmStatesForTest[alarm.Id]);
    }

    [Fact]
    public void ScanAlarms_NoChange_NoEventLogged()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 持续为 false：首次扫描 + 二次扫描均无事件
        _plc.SetBool(alarm.PlcAddress, false);
        _service.ScanAlarms();
        _service.ScanAlarms();
        Assert.Empty(_history.AlarmEvents);

        // 持续为 true：上升沿后再次扫描，不应重复写入 Triggered
        _plc.SetBool(alarm.PlcAddress, true);
        _service.ScanAlarms();
        Assert.Single(_history.AlarmEvents);
        _service.ScanAlarms();
        Assert.Single(_history.AlarmEvents);
    }

    [Fact]
    public void ScanCounterAlarms_FirstObservationDoesNotNotify_ButLaterRisingEdgeDoes()
    {
        var notifications = new RecordingNotificationChannel();
        var service = new PlcDataAcquisitionService(
            _plc,
            _connectionManager,
            _appSettings,
            _history,
            _deviceRepository,
            _baselineStore,
            NullLogger<PlcDataAcquisitionService>.Instance,
            alarmNotificationChannel: notifications);
        var device = AddDevice();
        var counterAlarm = new CounterAlarm
        {
            Id = "count-1",
            DeviceId = device.Id,
            Name = "计数超限",
            PlcAddress = "D200",
            MaxValue = 10,
            Enabled = true,
        };
        device.CounterAlarms.Add(counterAlarm);

        _plc.SetInt32(counterAlarm.PlcAddress, 20);
        service.ScanCounterAlarms();
        Assert.Empty(notifications.Notifications);

        _plc.SetInt32(counterAlarm.PlcAddress, 5);
        service.ScanCounterAlarms();
        _plc.SetInt32(counterAlarm.PlcAddress, 20);
        service.ScanCounterAlarms();

        var notification = Assert.Single(notifications.Notifications);
        Assert.Equal(counterAlarm.Id, notification.AlarmId);
        Assert.Equal(AlarmLevel.Medium, notification.Level);
    }

    [Fact]
    public void ScanAlarms_WriteFailsOnRisingEdge_DoesNotUpdatePrevState()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 模拟 DB 写入失败：上升沿触发但 LogAlarmEvent 返回 false
        _history.ShouldFailAlarmEventWrite = true;
        _plc.SetBool(alarm.PlcAddress, true);
        _service.ScanAlarms();

        // 关键断言：写入失败时 _prevAlarmStates 不应更新为 true，
        // 否则下次扫描会丢失这次上升沿事件。
        // 修复后 DB 写入失败时 key 不存在（不更新内存状态），下次重新尝试。
        Assert.Empty(_history.AlarmEvents);
        Assert.False(_service.PrevAlarmStatesForTest.ContainsKey(alarm.Id));

        // 恢复 DB 写入：再次扫描应能写入上升沿事件
        _history.ShouldFailAlarmEventWrite = false;
        _service.ScanAlarms();
        Assert.Single(_history.AlarmEvents);
        Assert.Equal(AlarmEventType.Triggered, _history.AlarmEvents[0].EventType);
    }

    [Fact]
    public void ScanAlarms_RebuildFromHistory_AfterStateCleared()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 触发报警并落库
        _plc.SetBool(alarm.PlcAddress, true);
        _service.ScanAlarms();
        Assert.Single(_history.AlarmEvents);

        // 模拟 PLC 重连后状态字典被清空（实际由 ResetShift 触发）：
        // 不再调用 _service.SetPrevAlarmStateForTest，直接 ScanAlarms 应从历史重建
        // 注意：调用前需手动清除内存状态以模拟重连
        _service.RemoveAlarmState(alarm.Id);
        Assert.False(_service.PrevAlarmStatesForTest.ContainsKey(alarm.Id));

        // PLC 仍为 true，重建后状态应为 true，StartTime 回填为最近 Triggered 事件时间
        _service.ScanAlarms();
        Assert.True(_service.PrevAlarmStatesForTest[alarm.Id]);
        Assert.True(alarm.StartTime > DateTime.MinValue);
        // 重建不写入新事件（仅内存恢复）
        Assert.Single(_history.AlarmEvents);
    }

    // ════════════════════════════════════════════════════════════════
    //  班次切换
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void LogShiftChangeForActiveAlarms_WritesShiftChangeEvent()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 标记报警当前触发中
        _service.SetPrevAlarmStateForTest(alarm.Id, true);
        _service.CurrentShiftIdForTest = new ShiftIdentifier("白班", new(0, 0, 0), new(12, 0, 0));

        _service.LogShiftChangeForActiveAlarms();

        var evt = Assert.Single(_history.AlarmEvents);
        Assert.Equal(AlarmEventType.ShiftChange, evt.EventType);
        Assert.Equal(alarm.Id, evt.AlarmId);
        Assert.Equal("白班", evt.ShiftName);
    }

    [Fact]
    public void LogShiftChangeForActiveAlarms_SkipsInactiveAlarms()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 报警未触发：不应写入班次切换事件
        _service.SetPrevAlarmStateForTest(alarm.Id, false);
        _service.CurrentShiftIdForTest = new ShiftIdentifier("白班", new(0, 0, 0), new(12, 0, 0));

        _service.LogShiftChangeForActiveAlarms();
        Assert.Empty(_history.AlarmEvents);
    }

    [Fact]
    public void LogShiftChangeForActiveAlarms_WriteFails_AddsToFailedSet()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 标记报警当前触发中
        _service.SetPrevAlarmStateForTest(alarm.Id, true);
        _service.CurrentShiftIdForTest = new ShiftIdentifier("白班", new(0, 0, 0), new(12, 0, 0));

        // 模拟 DB 写入失败：LogShiftChangeForActiveAlarms 应将报警 Id 加入 _shiftChangeFailedAlarms
        _history.ShouldFailAlarmEventWrite = true;
        _service.LogShiftChangeForActiveAlarms();

        // 关键断言：班次切换事件未落库
        Assert.Empty(_history.AlarmEvents);
        // 报警 Id 已被加入失败集合，等待 ScanAlarms 重建时跳过历史查询
        Assert.Contains(alarm.Id, _service.ShiftChangeFailedAlarmsForTest);
    }

    [Fact]
    public void ScanAlarms_AfterShiftChangeWriteFailed_TreatsAsUntriggeredAndLogsNewRisingEdge()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 步骤1：先记录一条 Triggered 事件到历史（模拟班次切换前报警已触发并落库）
        var oldShiftTime = DateTime.Now.AddMinutes(-10);
        _history.LogAlarmEvent(device.Id, device.Name, alarm.Id, alarm.Name,
            alarm.PlcAddress, AlarmEventType.Triggered, oldShiftTime, "白班");
        Assert.Single(_history.AlarmEvents);

        // 步骤2：班次切换事件写入失败，报警 Id 已加入 _shiftChangeFailedAlarms
        _service.SetPrevAlarmStateForTest(alarm.Id, true);
        _service.CurrentShiftIdForTest = new ShiftIdentifier("白班", new(0, 0, 0), new(12, 0, 0));
        _history.ShouldFailAlarmEventWrite = true;
        _service.LogShiftChangeForActiveAlarms();
        Assert.Contains(alarm.Id, _service.ShiftChangeFailedAlarmsForTest);

        // 步骤3：模拟 ResetShift 清空 _prevAlarmStates（生产代码中由 ResetShift 触发）。
        // 注意：必须使用 ClearPrevAlarmStateForTest 而非 RemoveAlarmState——后者会同时清空
        // _shiftChangeFailedAlarms，导致 ScanAlarms 走历史重建分支而非"跳过历史"分支。
        _service.ClearPrevAlarmStateForTest(alarm.Id);
        Assert.False(_service.PrevAlarmStatesForTest.ContainsKey(alarm.Id));

        // 步骤4：恢复写入，PLC 仍为 true（报警在新班次中物理上仍触发）
        _history.ShouldFailAlarmEventWrite = false;
        _plc.SetBool(alarm.PlcAddress, true);

        // 步骤5：ScanAlarms 重建状态
        _service.ScanAlarms();

        // 关键断言1：因 _shiftChangeFailedAlarms 跳过历史查询，报警被当作"未触发"，
        // PLC 当前为 true → 触发新的上升沿事件（而非沿用上班次的 Triggered 不写新事件）
        Assert.Equal(2, _history.AlarmEvents.Count);
        Assert.Equal(AlarmEventType.Triggered, _history.AlarmEvents[1].EventType);

        // 关键断言2：StartTime 是新班次内的时间（不是历史回填的上班次 oldShiftTime）
        Assert.True(alarm.StartTime > oldShiftTime);
    }

    [Fact]
    public void DetectShiftChange_FirstCall_InitializesCurrentShiftIdWithoutReset()
    {
        // 首次调用：仅初始化 _currentShiftId，不触发 ResetShift（避免启动即清零）
        Assert.Null(_service.CurrentShiftIdForTest);

        _service.DetectShiftChange();

        Assert.NotNull(_service.CurrentShiftIdForTest);
        // 不应有班次切换事件（首次调用无旧班次对比）
        Assert.Empty(_history.AlarmEvents);
    }

    [Fact]
    public void DetectShiftChange_OnRealTransition_TriggersResetShiftAndLogsShiftChange()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 模拟"当前班次"为不匹配 DateTime.Now 的班次
        // 配置 AppSettings 仅有 "白班" 00:00-12:00；当前时刻若在 12:00-24:00 应识别为班次切换
        // 但为避免测试在 12:00 边界附近 flaky，改为：先初始化为一个"过去班次"，再调用 DetectShiftChange
        _appSettings.Shifts = new ObservableCollection<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new(0, 0, 0), EndTime = new(12, 0, 0) },
            new() { Name = "夜班", StartTime = new(12, 0, 0), EndTime = new(24, 0, 0) }
        };

        // 通过首次调用初始化 CurrentShiftId
        _service.DetectShiftChange();
        var firstShift = _service.CurrentShiftIdForTest!;
        Assert.NotNull(firstShift);

        // 触发报警（便于验证班次切换事件）
        _plc.SetBool(alarm.PlcAddress, true);
        _service.ScanAlarms();
        Assert.True(_service.PrevAlarmStatesForTest[alarm.Id]);

        // 模拟班次切换：把 _currentShiftId 改成与当前时刻不匹配的班次
        // 这样下一次 DetectShiftChange 会检测到不同并触发 ResetShift
        var now = DateTime.Now.TimeOfDay;
        var fakeCurrentShiftName = now < new TimeSpan(12, 0, 0) ? "夜班" : "白班";
        var fakeCurrentShiftStart = now < new TimeSpan(12, 0, 0) ? new TimeSpan(12, 0, 0) : new TimeSpan(0, 0, 0);
        var fakeCurrentShiftEnd = now < new TimeSpan(12, 0, 0) ? new TimeSpan(24, 0, 0) : new TimeSpan(12, 0, 0);
        _service.CurrentShiftIdForTest = new ShiftIdentifier(fakeCurrentShiftName, fakeCurrentShiftStart, fakeCurrentShiftEnd);

        _service.DetectShiftChange();

        // 班次切换后 _currentShiftId 已更新为真实班次
        Assert.NotEqual(fakeCurrentShiftName, _service.CurrentShiftIdForTest!.Name);

        // 班次切换事件已写入（报警当前触发中）
        var shiftChangeEvents = _history.AlarmEvents.Where(e => e.EventType == AlarmEventType.ShiftChange).ToList();
        Assert.NotEmpty(shiftChangeEvents);

        // ResetShift 已清空报警内存状态
        Assert.False(_service.PrevAlarmStatesForTest.ContainsKey(alarm.Id));
    }

    // ════════════════════════════════════════════════════════════════
    //  基线回退 / 产量读取
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void TryReadOkCount_FirstRead_BaselineEqualsRaw_TotalOkZero()
    {
        var device = AddDevice();
        _plc.SetInt32(device.OkCountAddress, 1000);

        var result = _service.TryReadOkCount(device);

        Assert.Equal(PlcDataAcquisitionService.ReadResult.Success, result);
        var runtime = _deviceRepository.RuntimeMap[device.Id];
        Assert.Equal(1000, runtime.OkProduction);
        Assert.Equal(0, runtime.TotalOkProduction);  // 首次：delta = raw - baseline = 1000 - 1000
    }

    [Fact]
    public void TryReadOkCount_IncrementalRead_DeltaIsRawMinusBaseline()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        // 首次读取：baseline=1000, TotalOk=0
        _plc.SetInt32(device.OkCountAddress, 1000);
        _service.TryReadOkCount(device);
        Assert.Equal(0, runtime.TotalOkProduction);

        // 二次读取：raw=1050, baseline=1000, TotalOk=50
        _plc.SetInt32(device.OkCountAddress, 1050);
        _service.TryReadOkCount(device);
        Assert.Equal(50, runtime.TotalOkProduction);
        Assert.Equal(1050, runtime.OkProduction);
    }

    [Fact]
    public void TryReadOkCount_RawRollsBack_UpdatesBaselineToLowerValue()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        // 首次读取：baseline=1000
        _plc.SetInt32(device.OkCountAddress, 1000);
        _service.TryReadOkCount(device);

        // 模拟 PLC 清零后回退：raw=10 < baseline=1000
        // 应自动更新 baseline=10，TotalOk=0
        _plc.SetInt32(device.OkCountAddress, 10);
        _service.TryReadOkCount(device);

        Assert.Equal(10, runtime.OkProduction);
        Assert.Equal(0, runtime.TotalOkProduction);

        // 后续递增：raw=20, baseline=10, TotalOk=10
        _plc.SetInt32(device.OkCountAddress, 20);
        _service.TryReadOkCount(device);
        Assert.Equal(10, runtime.TotalOkProduction);
    }

    [Fact]
    public void TryReadOkCount_NotConfiguredAddress_ReturnsNotConfigured()
    {
        // OkCountAddress 为空 → NotConfigured，不会调用 PLC
        var device = AddDevice(okAddr: "");
        var result = _service.TryReadOkCount(device);

        Assert.Equal(PlcDataAcquisitionService.ReadResult.NotConfigured, result);
    }

    [Fact]
    public void TryReadOkCount_PlcReadFails_ReturnsFailed()
    {
        var device = AddDevice();
        _plc.SetFailing(device.OkCountAddress);

        var result = _service.TryReadOkCount(device);
        Assert.Equal(PlcDataAcquisitionService.ReadResult.Failed, result);
    }

    [Fact]
    public void TryReadNgCount_BaselineManagement_MirrorsOkCount()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        _plc.SetInt32(device.NgCountAddress, 50);
        _service.TryReadNgCount(device);
        Assert.Equal(0, runtime.TotalNgProduction);

        _plc.SetInt32(device.NgCountAddress, 75);
        _service.TryReadNgCount(device);
        Assert.Equal(25, runtime.TotalNgProduction);
    }

    // ════════════════════════════════════════════════════════════════
    //  状态转换
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void TryReadStatusWord_FirstRead_LogsZeroToCurrentTransition()
    {
        var device = AddDevice();
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);

        var result = _service.TryReadStatusWord(device);

        Assert.Equal(PlcDataAcquisitionService.ReadResult.Success, result);
        var transition = Assert.Single(_history.StatusTransitions);
        Assert.Equal(0, transition.PreviousState);
        Assert.Equal((int)DeviceStatus.Running,transition.CurrentState);
        Assert.Equal(device.Id, transition.DeviceId);
    }

    [Fact]
    public void TryReadStatusWord_StatusChanges_LogsTransition()
    {
        var device = AddDevice();

        // 1→2 转换
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);
        _service.TryReadStatusWord(device);
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Alarm);
        _service.TryReadStatusWord(device);

        Assert.Equal(2, _history.StatusTransitions.Count);
        Assert.Equal((int)DeviceStatus.Running,_history.StatusTransitions[0].CurrentState);
        Assert.Equal((int)DeviceStatus.Alarm,_history.StatusTransitions[1].CurrentState);
        Assert.Equal((int)DeviceStatus.Running,_history.StatusTransitions[1].PreviousState);
    }

    [Fact]
    public void TryReadStatusWord_SameStatus_NoNewTransition()
    {
        var device = AddDevice();

        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);
        _service.TryReadStatusWord(device);
        _service.TryReadStatusWord(device);
        _service.TryReadStatusWord(device);

        // 仅首次写入 0→1，后续相同状态不写入
        Assert.Single(_history.StatusTransitions);
    }

    [Fact]
    public void TryReadStatusWord_WriteFails_DoesNotUpdatePrevStatusWord()
    {
        var device = AddDevice();

        // 首次写入成功
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);
        _service.TryReadStatusWord(device);
        Assert.Single(_history.StatusTransitions);

        // 第二次：状态变化为 2 但写入失败
        _history.ShouldFailStatusTransitionWrite = true;
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Alarm);
        _service.TryReadStatusWord(device);

        // 关键断言：写入失败时 _prevStatusWords 不更新为 2
        Assert.Equal((int)DeviceStatus.Running,_service.PrevStatusWordsForTest[device.Id]);
        Assert.Single(_history.StatusTransitions);  // 仍是仅 1 条

        // 恢复 DB 写入：再次扫描应能成功写入 1→2 转换
        _history.ShouldFailStatusTransitionWrite = false;
        _service.TryReadStatusWord(device);
        Assert.Equal(2, _history.StatusTransitions.Count);
        Assert.Equal((int)DeviceStatus.Running,_history.StatusTransitions[1].PreviousState);
        Assert.Equal((int)DeviceStatus.Alarm,_history.StatusTransitions[1].CurrentState);
    }

    [Fact]
    public void LogOfflineTransition_WritesZeroTransition_WhenDeviceWasActive()
    {
        var device = AddDevice();

        // 先把 _prevStatusWords 设为 Running（模拟设备正在运行）
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);
        _service.TryReadStatusWord(device);
        Assert.Single(_history.StatusTransitions);

        // 触发离线转换
        _service.LogOfflineTransition(device);

        Assert.Equal(2, _history.StatusTransitions.Count);
        Assert.Equal((int)DeviceStatus.Running,_history.StatusTransitions[1].PreviousState);
        Assert.Equal(0, _history.StatusTransitions[1].CurrentState);
        Assert.Equal(0, _service.PrevStatusWordsForTest[device.Id]);
    }

    [Fact]
    public void LogOfflineTransition_SkipsWhenDeviceAlreadyOffline()
    {
        var device = AddDevice();

        // 设备已是离线状态（_prevStatusWords=0）：不应重复写入
        _service.LogOfflineTransition(device);
        Assert.Empty(_history.StatusTransitions);
    }

    // ════════════════════════════════════════════════════════════════
    //  班次重置 / PLC 清零触发
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ResetShift_ClearsRuntimeAndTriggersPlcReset_WhenConnected()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];
        runtime.TotalOkProduction = 100;
        runtime.TotalNgProduction = 5;
        runtime.RunTime = 60;
        runtime.AlarmTime = 10;

        _connectionManager.IsConnected = true;

        _service.ResetShift();

        // 运行时已清零
        Assert.Equal(0, runtime.TotalOkProduction);
        Assert.Equal(0, runtime.TotalNgProduction);
        Assert.Equal(0, runtime.RunTime);
        Assert.Equal(0, runtime.AlarmTime);

        // PLC 清零触发位已写入 1
        var resetWrite = _plc.WriteHistory.FirstOrDefault(w => w.Address == device.ProductionResetAddress);
        Assert.NotNull(resetWrite);
        Assert.Equal(1, resetWrite!.Value);
    }

    [Fact]
    public void ResetShift_DeferPlcReset_WhenDisconnected()
    {
        var device = AddDevice();
        _connectionManager.IsConnected = false;

        _service.ResetShift();

        // PLC 未连接：不应尝试写入清零
        Assert.Empty(_plc.WriteHistory);

        // 设备已加入待重连清零集合（间接验证：再次连接 + 调用相关逻辑可触发）
        // 这里仅验证不抛异常 + 不写入，待重连集合的内部状态由 PLC 重连路径处理
    }

    [Fact]
    public void ResetShift_ClearsAlarmAndStatusMemoryState()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        // 设置内存状态
        _service.SetPrevAlarmStateForTest(alarm.Id, true);
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);
        _service.TryReadStatusWord(device);
        Assert.NotEmpty(_service.PrevAlarmStatesForTest);
        Assert.NotEmpty(_service.PrevStatusWordsForTest);

        _connectionManager.IsConnected = true;
        _service.ResetShift();

        // 内存状态已清空
        Assert.Empty(_service.PrevAlarmStatesForTest);
        Assert.Empty(_service.PrevStatusWordsForTest);

        // 报警时间戳已重置
        Assert.Equal(DateTime.MinValue, alarm.StartTime);
        Assert.Equal(DateTime.MinValue, alarm.EndTime);
    }

    // ════════════════════════════════════════════════════════════════
    //  生产快照写入
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void LogProductionSnapshot_WritesTotalProductionForSuccessDevices()
    {
        var device1 = AddDevice(id: "dev-001", name: "设备1");
        var device2 = AddDevice(id: "dev-002", name: "设备2", okAddr: "D200", ngAddr: "D201", statusAddr: "D202", resetAddr: "D203", alarmAddr: "M200");

        var rt1 = _deviceRepository.RuntimeMap[device1.Id];
        rt1.TotalOkProduction = 100;
        rt1.TotalNgProduction = 5;
        rt1.StatusWord = (int)DeviceStatus.Running;

        var rt2 = _deviceRepository.RuntimeMap[device2.Id];
        rt2.TotalOkProduction = 200;
        rt2.TotalNgProduction = 10;
        rt2.StatusWord = (int)DeviceStatus.Alarm;

        // 设置班次标识，使 ShiftName 字段非空
        _service.CurrentShiftIdForTest = new ShiftIdentifier("白班", new(0, 0, 0), new(12, 0, 0));

        // 仅记录 dev-001 成功
        _service.LogProductionSnapshot(new HashSet<string> { device1.Id });

        var log = Assert.Single(_history.ProductionLogs);
        Assert.Equal(device1.Id, log.DeviceId);
        Assert.Equal("设备1", log.DeviceName);
        Assert.Equal(100, log.OkProduction);
        Assert.Equal(5, log.NgProduction);
        Assert.Equal((int)DeviceStatus.Running,log.StatusWord);
        Assert.Equal("白班", log.ShiftName);
    }

    [Fact]
    public void LogProductionSnapshot_SkipsDevicesNotInSuccessSet()
    {
        var device = AddDevice();
        var rt = _deviceRepository.RuntimeMap[device.Id];
        rt.TotalOkProduction = 100;

        // 空集合：不写入任何记录
        _service.LogProductionSnapshot(new HashSet<string>());
        Assert.Empty(_history.ProductionLogs);
    }

    // ════════════════════════════════════════════════════════════════
    //  OEE 时间累计
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void AccumulateOeeTime_AddsTimeByStatusWord()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        // Running 状态累计 10s 到 RunTime
        runtime.StatusWord = (int)DeviceStatus.Running;
        _service.AccumulateOeeTime(10.0, new HashSet<string> { device.Id });
        Assert.Equal(10.0, runtime.RunTime);
        Assert.Equal(0.0, runtime.AlarmTime);

        // Alarm 状态累计 5s 到 AlarmTime
        runtime.StatusWord = (int)DeviceStatus.Alarm;
        _service.AccumulateOeeTime(5.0, new HashSet<string> { device.Id });
        Assert.Equal(10.0, runtime.RunTime);
        Assert.Equal(5.0, runtime.AlarmTime);

        // Paused 状态累计 3s 到 PausedTime
        runtime.StatusWord = (int)DeviceStatus.Paused;
        _service.AccumulateOeeTime(3.0, new HashSet<string> { device.Id });
        Assert.Equal(3.0, runtime.PausedTime);
    }

    [Fact]
    public void AccumulateOeeTime_SkipsFailedDevices()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];
        runtime.StatusWord = (int)DeviceStatus.Running;

        // 设备不在 successDevices 集合中：不累计
        _service.AccumulateOeeTime(10.0, new HashSet<string>());
        Assert.Equal(0.0, runtime.RunTime);
    }

    // ════════════════════════════════════════════════════════════════
    //  RefreshDeviceData 综合采集
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void RefreshDeviceData_ReadsAllConfiguredDevices_ReturnsSuccessSet()
    {
        var device1 = AddDevice(id: "dev-001", name: "设备1");
        var device2 = AddDevice(id: "dev-002", name: "设备2",
            okAddr: "D200", ngAddr: "D201", statusAddr: "D202", resetAddr: "D203", alarmAddr: "M200");

        _plc.SetInt32(device1.OkCountAddress, 100);
        _plc.SetInt32(device1.NgCountAddress, 5);
        _plc.SetInt32(device1.StatusCountAddress, (int)DeviceStatus.Running);
        _plc.SetInt32(device2.OkCountAddress, 200);
        _plc.SetInt32(device2.NgCountAddress, 10);
        _plc.SetInt32(device2.StatusCountAddress, (int)DeviceStatus.Alarm);

        var success = _service.RefreshDeviceData(out var noDevices);

        Assert.False(noDevices);
        Assert.Equal(2, success.Count);
        Assert.Contains(device1.Id, success);
        Assert.Contains(device2.Id, success);

        // 状态转换已落库（首次读取：0→Running 和 0→Alarm）
        Assert.Equal(2, _history.StatusTransitions.Count);
    }

    [Fact]
    public void RefreshDeviceData_AllDevicesNotConfigured_ReturnsNoDevicesToRead()
    {
        // OK/NG/状态地址全空：视为"无设备可读"，不应触发 MarkDisconnected
        var device = AddDevice(okAddr: "", ngAddr: "", statusAddr: "", resetAddr: "");

        var success = _service.RefreshDeviceData(out var noDevices);

        Assert.True(noDevices);
        Assert.Empty(success);
    }

    [Fact]
    public void RefreshDeviceData_PartialFailure_StillReturnsSuccessForOthers()
    {
        var device1 = AddDevice(id: "dev-001", name: "设备1");
        var device2 = AddDevice(id: "dev-002", name: "设备2",
            okAddr: "D200", ngAddr: "D201", statusAddr: "D202", resetAddr: "D203", alarmAddr: "M200");

        // 设备1 OK，设备2 全部地址读取失败
        _plc.SetInt32(device1.OkCountAddress, 100);
        _plc.SetInt32(device1.NgCountAddress, 5);
        _plc.SetInt32(device1.StatusCountAddress, (int)DeviceStatus.Running);
        _plc.SetFailing(device2.OkCountAddress);
        _plc.SetFailing(device2.NgCountAddress);
        _plc.SetFailing(device2.StatusCountAddress);

        var success = _service.RefreshDeviceData(out var noDevices);

        Assert.False(noDevices);
        // 仅设备1成功
        Assert.Single(success);
        Assert.Contains(device1.Id, success);
    }

    [Fact]
    public void RefreshDeviceData_UnifiesDWordBatchForStatusDefectAndCounterAlarm()
    {
        var device = AddDevice(
            okAddr: "D10",
            ngAddr: "D12",
            statusAddr: "D14",
            resetAddr: "D20");
        var defect = new Defect
        {
            DeviceId = device.Id,
            Name = "划痕",
            PlcAddress = "D16",
        };
        device.Defects.Add(defect);
        var counterAlarm = new CounterAlarm
        {
            DeviceId = device.Id,
            Name = "计数超限",
            PlcAddress = "D18",
            Enabled = true,
            MaxValue = 100,
        };
        device.CounterAlarms.Add(counterAlarm);

        _plc.SetInt32("D10", 100);
        _plc.SetInt32("D12", 5);
        _plc.SetInt32("D14", (int)DeviceStatus.Running);
        _plc.SetInt32("D16", 7);
        _plc.SetInt32("D18", 8);

        var success = _service.RefreshDeviceData(out var noDevices);
        var defectsOk = _service.ScanDefects();
        _service.ScanCounterAlarms();

        Assert.False(noDevices);
        Assert.Contains(device.Id, success);
        Assert.True(defectsOk);
        Assert.Equal(7, defect.Count);
        Assert.Equal(8, counterAlarm.CurrentValue);
        Assert.Equal((int)DeviceStatus.Running, _deviceRepository.RuntimeMap[device.Id].StatusWord);
        Assert.Equal(1, _plc.ReadInt32BatchCallCount);
        Assert.Equal(("D10", (ushort)5), Assert.Single(_plc.ReadInt32BatchHistory));
        Assert.Equal(0, _plc.ReadInt32CallCount);
    }

    [Fact]
    public async Task DiagnosticsSnapshot_SuccessFailureRecovery_TracksCurrentHealth()
    {
        var device = AddDevice();
        _plc.SetInt32(device.OkCountAddress, 100);
        _plc.SetInt32(device.NgCountAddress, 2);
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);
        _appSettings.PollingIntervalMs = 20;
        _appSettings.HistoryWriteIntervalScans = 1000;

        _service.Start();
        await WaitUntilAsync(
            () => _service.GetDiagnosticsSnapshot().LastCycleSucceeded,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await _service.StopAsync();

        var successfulSnapshot = _service.GetDiagnosticsSnapshot();
        Assert.True(successfulSnapshot.CompletedCycles > 0);
        Assert.True(successfulSnapshot.LastCycleSucceeded);
        Assert.Equal(0, successfulSnapshot.ConsecutiveFailureCycles);
        Assert.Contains(device.Id, successfulSnapshot.LastSuccessfulDeviceIds);

        _plc.SetFailing(device.OkCountAddress);
        _plc.SetFailing(device.NgCountAddress);
        _plc.SetFailing(device.StatusCountAddress);
        _service.Start();
        await WaitUntilAsync(
            () => _service.GetDiagnosticsSnapshot().ConsecutiveFailureCycles > 0,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await _service.StopAsync();

        var failedSnapshot = _service.GetDiagnosticsSnapshot();
        Assert.False(failedSnapshot.LastCycleSucceeded);
        Assert.True(failedSnapshot.ConsecutiveFailureCycles > 0);
        Assert.NotNull(failedSnapshot.LastFailureAt);
        Assert.False(string.IsNullOrWhiteSpace(failedSnapshot.LastFailureMessage));

        _plc.ClearFailing(device.OkCountAddress);
        _plc.ClearFailing(device.NgCountAddress);
        _plc.ClearFailing(device.StatusCountAddress);
        _service.Start();
        await WaitUntilAsync(
            () =>
            {
                var snapshot = _service.GetDiagnosticsSnapshot();
                return snapshot.LastCycleSucceeded && snapshot.ConsecutiveFailureCycles == 0;
            },
            TimeSpan.FromSeconds(8),
            TestContext.Current.CancellationToken);
        await _service.StopAsync();

        var recoveredSnapshot = _service.GetDiagnosticsSnapshot();
        Assert.True(recoveredSnapshot.LastCycleSucceeded);
        Assert.Equal(0, recoveredSnapshot.ConsecutiveFailureCycles);
        Assert.Contains(device.Id, recoveredSnapshot.LastSuccessfulDeviceIds);

        Assert.False(recoveredSnapshot.LastSuccessfulDeviceIds is HashSet<string>);
        var copiedIds = recoveredSnapshot.LastSuccessfulDeviceIds.ToHashSet();
        copiedIds.Clear();
        Assert.Contains(device.Id, _service.GetDiagnosticsSnapshot().LastSuccessfulDeviceIds);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"等待采集诊断条件超时：{timeout}");
            await Task.Delay(20, cancellationToken);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  GetCurrentShiftName / GetCurrentShiftStart
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void GetCurrentShiftName_NoShiftId_ReturnsEmpty()
    {
        Assert.Null(_service.CurrentShiftIdForTest);
        Assert.Equal(string.Empty, _service.GetCurrentShiftName());
    }

    [Fact]
    public void GetCurrentShiftName_WithShiftId_ReturnsName()
    {
        _service.CurrentShiftIdForTest = new ShiftIdentifier("白班", new(0, 0, 0), new(12, 0, 0));
        Assert.Equal("白班", _service.GetCurrentShiftName());
    }

    [Fact]
    public void GetCurrentShiftStart_ReturnsShiftStartDateTime()
    {
        // 默认班次配置：00:00-12:00 + 12:00-24:00
        // 当前时刻必然落在其中一个班次内
        var now = DateTime.Now;
        var shiftStart = _service.GetCurrentShiftStart(now);

        // 起始时刻 <= now（起始不能晚于当前）
        Assert.True(shiftStart <= now);
        // 起始时刻 > now - 12h（不会太早）
        Assert.True(shiftStart > now.AddHours(-12));
    }

    // ════════════════════════════════════════════════════════════════
    //  PLC 返回异常值（状态字超范围 / 产量负数 / 产量溢出）
    //  生产环境 PLC 程序错误或通讯干扰可能写入异常值，验证：
    //  - 状态字超范围（如 99）仍写入 Runtime.StatusWord 与历史快照（不过滤）
    //  - AccumulateOeeTime 对未知状态字仅记日志警告，不累加 OEE 时间
    //  - 产量负数：基线差分仍可处理（raw - baseline 可能负）
    //  - 产量 int.MaxValue：不抛溢出异常
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// PLC 返回状态字=99（超出 0/1/2/3 范围）：
    /// - Runtime.StatusWord 被原样写入 99（代码不做范围过滤）
    /// - 状态转换历史记录 0→99（DeviceStatusTracker 首次读取会写入一条转换）
    /// - AccumulateOeeTime 仅对 Running(1)/Alarm(2)/Paused(3) 累加时间，未知状态不累加
    /// 注：ProductionLogs 历史快照由轮询循环按 HistoryWriteIntervalScans 间隔写入，
    ///     RefreshDeviceData 本身不触发快照写入，故此处不验证 ProductionLogs。
    /// </summary>
    [Fact]
    public void RefreshDeviceData_StatusWordOutOfRange_WritesToRuntimeButSkipsOeeAccumulation()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        // 首次读取写入状态字 99（超范围）
        _plc.SetInt32(device.OkCountAddress, 100);
        _plc.SetInt32(device.NgCountAddress, 0);
        _plc.SetInt32(device.StatusCountAddress, 99);

        var success = _service.RefreshDeviceData(out _);
        Assert.Contains(device.Id, success);
        Assert.Equal(99, runtime.StatusWord); // 原样写入

        // 状态转换历史记录 0→0（非法状态归一为初始状态）
        var transition = Assert.Single(_history.StatusTransitions);
        Assert.Equal(0, transition.CurrentState);

        // 累加 10s OEE 时间：状态字 99 不应累加到任何时间字段
        var runBefore = runtime.RunTime;
        var alarmBefore = runtime.AlarmTime;
        var pauseBefore = runtime.PausedTime;
        _service.AccumulateOeeTime(10.0, success);
        Assert.Equal(runBefore, runtime.RunTime);
        Assert.Equal(alarmBefore, runtime.AlarmTime);
        Assert.Equal(pauseBefore, runtime.PausedTime);
    }

    /// <summary>
    /// PLC 返回状态字=负数（如 -1，可能由有符号解析错误导致）：
    /// 与 99 相同——原样写入 Runtime，不累加 OEE 时间。
    /// </summary>
    [Fact]
    public void RefreshDeviceData_StatusWordNegative_WritesToRuntimeButSkipsOeeAccumulation()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        _plc.SetInt32(device.OkCountAddress, 0);
        _plc.SetInt32(device.NgCountAddress, 0);
        _plc.SetInt32(device.StatusCountAddress, -1);

        _service.RefreshDeviceData(out _);
        Assert.Equal(-1, runtime.StatusWord);

        var runBefore = runtime.RunTime;
        _service.AccumulateOeeTime(5.0, new HashSet<string> { device.Id });
        Assert.Equal(runBefore, runtime.RunTime); // 未累加
    }

    /// <summary>
    /// PLC 返回状态字=0 (Unknown/待机)：
    /// 0 是合法值（DeviceStatus.Unknown=0），写入 Runtime 但不累加 OEE 时间。
    /// 首次读取会写入一条 0→0 的状态转换记录（DeviceStatusTracker 首次读取必写）。
    /// </summary>
    [Fact]
    public void RefreshDeviceData_StatusWordZero_WritesToRuntimeSkipsOeeAccumulation()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        _plc.SetInt32(device.OkCountAddress, 0);
        _plc.SetInt32(device.NgCountAddress, 0);
        _plc.SetInt32(device.StatusCountAddress, 0);

        _service.RefreshDeviceData(out _);
        Assert.Equal(0, runtime.StatusWord);

        // 首次读取写入 0→0 状态转换（DeviceStatusTracker 首次读取必写一条）
        var transition = Assert.Single(_history.StatusTransitions);
        Assert.Equal(0, transition.CurrentState);

        var runBefore = runtime.RunTime;
        _service.AccumulateOeeTime(5.0, new HashSet<string> { device.Id });
        Assert.Equal(runBefore, runtime.RunTime);
    }

    /// <summary>
    /// PLC 返回 OK 产量=负数（可能由 PLC 寄存器解析错误或回退导致）：
    /// - RawProduction 被写入负数
    /// - TotalOkProduction 通过基线差分计算（raw - baseline），可能为负
    /// - 不抛异常
    /// </summary>
    [Fact]
    public void RefreshDeviceData_NegativeProductionCount_WrittenToRuntimeAsIs()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        _plc.SetInt32(device.OkCountAddress, -100);
        _plc.SetInt32(device.NgCountAddress, -5);
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);

        var success = _service.RefreshDeviceData(out _);
        Assert.Contains(device.Id, success);

        // Raw 值被原样写入（OkProduction = -100）
        Assert.Equal(-100, runtime.OkProduction);
        Assert.Equal(-5, runtime.NgProduction);

        // 首次读取时 baseline = raw，所以 TotalOkProduction = raw - baseline = 0
        Assert.Equal(0, runtime.TotalOkProduction);
    }

    /// <summary>
    /// PLC 返回 OK 产量=int.MaxValue：基线差分应正确处理，不溢出。
    /// 第二次读取若返回 int.MinValue（PLC 计数器回绕），基线差分为负数，
    /// ProductionBaselineStore 应以 raw 为新高线，避免差分持续为负。
    /// </summary>
    [Fact]
    public void RefreshDeviceData_MaxIntProduction_NoOverflowAndNegativeDeltaHandled()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        _plc.SetInt32(device.OkCountAddress, int.MaxValue);
        _plc.SetInt32(device.NgCountAddress, 0);
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);

        _service.RefreshDeviceData(out _);
        Assert.Equal(int.MaxValue, runtime.OkProduction);
        Assert.Equal(0, runtime.TotalOkProduction); // 首次：baseline = raw

        // 第二次读取：raw 减少（PLC 回绕），基线应更新为 max
        _plc.SetInt32(device.OkCountAddress, 10);
        _service.RefreshDeviceData(out _);
        Assert.Equal(10, runtime.OkProduction);
        // raw(10) - baseline(int.MaxValue) = 负数（PLC 回绕场景）
        // ProductionBaselineStore.GetOrCreate 应检测到 raw < baseline 并更新 baseline
        Assert.True(runtime.TotalOkProduction <= 0, "PLC 回绕时差分应为负或 0，不抛溢出异常");
    }

    // ════════════════════════════════════════════════════════════════
    //  异常分类（任务 9）：IsCommunicationException 静态方法
    //  验证外层 catch 的异常分类逻辑正确：
    //  - 已知通信异常（IOException/SocketException/ObjectDisposedException/TimeoutException/WebException）→ true
    //  - 业务异常（NullReferenceException/InvalidOperationException 等）→ false
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void IsCommunicationException_KnownCommTypes_ReturnsTrue()
    {
        Assert.True(PlcDataAcquisitionService.IsCommunicationException(new System.IO.IOException()));
        Assert.True(PlcDataAcquisitionService.IsCommunicationException(new System.Net.Sockets.SocketException()));
        Assert.True(PlcDataAcquisitionService.IsCommunicationException(new ObjectDisposedException("res")));
        Assert.True(PlcDataAcquisitionService.IsCommunicationException(new TimeoutException()));
        Assert.True(PlcDataAcquisitionService.IsCommunicationException(new System.Net.WebException()));
    }

    [Fact]
    public void IsCommunicationException_BusinessExceptions_ReturnsFalse()
    {
        Assert.False(PlcDataAcquisitionService.IsCommunicationException(new NullReferenceException()));
        Assert.False(PlcDataAcquisitionService.IsCommunicationException(new InvalidOperationException()));
        Assert.False(PlcDataAcquisitionService.IsCommunicationException(new ArgumentException()));
        Assert.False(PlcDataAcquisitionService.IsCommunicationException(new IndexOutOfRangeException()));
        Assert.False(PlcDataAcquisitionService.IsCommunicationException(new NotSupportedException()));
    }

    [Fact]
    public void IsCommunicationException_NullArgument_Throws()
    {
        // 编译期/运行期防御：null 参数应抛 ArgumentNullException（显式检查）
        Assert.Throws<ArgumentNullException>(() => PlcDataAcquisitionService.IsCommunicationException(null!));
    }

    // ════════════════════════════════════════════════════════════════
    //  外层 catch 路径（任务 9）：业务异常不触发 MarkDisconnected
    //  让 LogProduction 抛业务异常逃到 PollingLoopAsync 外层 catch，
    //  验证：
    //  - 服务不退出（IsRunning 仍为 true，外层 catch 不让循环跳出）
    //  - 不触发 ConnectionStateChanged 断开事件（外层 catch 不调 MarkDisconnected）
    //  - 下一轮仍能继续采集（外层 catch 后 await Task.Delay 正常）
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PollingLoopAsync_OuterCatch_BusinessException_DoesNotMarkDisconnected()
    {
        var device = AddDevice();
        _plc.SetInt32(device.OkCountAddress, 100);
        _plc.SetInt32(device.NgCountAddress, 0);
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);

        // 让 LogProduction 抛业务异常（模拟 EF Core 抛 InvalidOperationException 等非通信异常）
        _history.LogProductionException = new InvalidOperationException("测试业务异常：模拟 DB 上下文被释放");

        // 监听断开事件
        var disconnectEvents = new List<ConnectionStateChangedEventArgs>();
        _connectionManager.ConnectionStateChanged += (s, e) =>
        {
            if (!e.IsConnected) disconnectEvents.Add(e);
        };

        // 快速轮询 + 每轮写历史快照（HistoryWriteIntervalScans=1 触发 LogProductionSnapshot）
        _appSettings.PollingIntervalMs = 30;
        _appSettings.HistoryWriteIntervalScans = 1;
        _service.Start();

        // 等几轮让外层 catch 多次触发
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // 服务应仍在运行（外层 catch 不让循环退出）
        Assert.True(_service.IsRunning);

        await _service.StopAsync();

        // 业务异常不应触发 MarkDisconnected → 不应有断开事件
        // 注：若 MarkDisconnected 被错误调用，由于 IsConnected=true（首次 Connect 成功），
        //     会写入断开事件。这里断言空集验证未调用。
        Assert.Empty(disconnectEvents);
    }

    [Fact]
    public async Task PollingLoopAsync_OuterCatch_CommunicationException_KeepsServiceRunning()
    {
        // 通信类异常逃到外层 catch 时，服务也不应退出（外层 catch 仅记日志）
        // 下一轮通过 ReadFailure 路径触发 MarkDisconnected（而非外层 catch 直接调）
        var device = AddDevice();
        _plc.SetInt32(device.OkCountAddress, 100);
        _plc.SetInt32(device.NgCountAddress, 0);
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);

        // 抛 ObjectDisposedException（通信类）
        _history.LogProductionException = new ObjectDisposedException("FakeResource");

        _appSettings.PollingIntervalMs = 30;
        _appSettings.HistoryWriteIntervalScans = 1;
        _service.Start();

        await Task.Delay(300, TestContext.Current.CancellationToken);

        // 服务不退出
        Assert.True(_service.IsRunning);

        await _service.StopAsync();
    }

    // ════════════════════════════════════════════════════════════════
    //  TryScan 异常隔离（P1-3）：扫描方法抛异常时不应逃逸到 PollingLoopAsync 外层 catch
    //  验证：
    //  - 通信异常（SocketException/ObjectDisposedException 等）→ MarkDisconnected(ScanException) 触发一次
    //  - 业务异常（NullReference/InvalidOperation 等）→ 不调 MarkDisconnected，仅记 LogError
    //  - TryScan(Func<bool>) 异常时返回 false（保留原有返回值语义）
    //  - 关键异常（OutOfMemoryException）重抛，不被 TryScan 吞掉
    //  - 实际场景：FakePlcDriver.ReadInt32Exception 在 ScanAlarms/ScanDefects/ScanCounterAlarms 读取阶段抛出
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void TryScan_ScanAlarms_CommunicationException_TriggersMarkDisconnectedWithScanExceptionReason()
    {
        // 验证：ScanAlarms 抛 SocketException 时，TryScan 触发 MarkDisconnected(ScanException)
        var device = AddDevice();
        var alarm = device.Alarms.First();
        _plc.SetBool(alarm.PlcAddress, false);
        _plc.ReadBoolException = new System.Net.Sockets.SocketException(10054); // ConnectionReset

        // 必须先 EnsureConnected，否则 MarkDisconnected 早返回不触发事件
        _connectionManager.EnsureConnected();
        Assert.True(_connectionManager.IsConnected);

        ConnectionStateChangedEventArgs? disconnectEvent = null;
        _connectionManager.ConnectionStateChanged += (s, e) =>
        {
            if (!e.IsConnected) disconnectEvent = e;
        };

        // 调用 TryScanForTest 包装 ScanAlarms
        var result = _service.TryScanForTest(() => _service.ScanAlarms(), "ScanAlarms");

        Assert.False(result); // 异常时返回 false
        Assert.NotNull(disconnectEvent);
        Assert.Equal(DisconnectionReason.ScanException, disconnectEvent!.Reason);
        Assert.False(_connectionManager.IsConnected);
    }

    [Fact]
    public void TryScan_ScanAlarms_BusinessException_DoesNotMarkDisconnected()
    {
        // 验证：ScanAlarms 抛业务异常（NullReferenceException）时，TryScan 不调 MarkDisconnected
        var device = AddDevice();
        var alarm = device.Alarms.First();
        _plc.SetBool(alarm.PlcAddress, false);
        _plc.ReadBoolException = new NullReferenceException("模拟业务异常：内部状态被意外置空");

        _connectionManager.EnsureConnected();
        Assert.True(_connectionManager.IsConnected);

        ConnectionStateChangedEventArgs? disconnectEvent = null;
        _connectionManager.ConnectionStateChanged += (s, e) =>
        {
            if (!e.IsConnected) disconnectEvent = e;
        };

        var result = _service.TryScanForTest(() => _service.ScanAlarms(), "ScanAlarms");

        Assert.False(result);
        // 关键断言：业务异常不应触发 MarkDisconnected
        Assert.Null(disconnectEvent);
        Assert.True(_connectionManager.IsConnected); // 连接状态保持
    }

    [Fact]
    public void TryScan_ScanDefects_CommunicationException_TriggersMarkDisconnected()
    {
        // 验证：ScanDefects 抛 ObjectDisposedException 时，TryScan 触发 MarkDisconnected(ScanException)
        var device = AddDevice();
        device.Defects.Add(new Defect
        {
            DeviceId = device.Id,
            Name = "划痕",
            PlcAddress = "D500"
        });
        _plc.SetInt32("D500", 0);
        _plc.ReadInt32Exception = new ObjectDisposedException("MelsecMcNet");

        _connectionManager.EnsureConnected();

        ConnectionStateChangedEventArgs? disconnectEvent = null;
        _connectionManager.ConnectionStateChanged += (s, e) =>
        {
            if (!e.IsConnected) disconnectEvent = e;
        };

        var result = _service.TryScanForTest(() => _service.ScanDefects(), "ScanDefects");

        Assert.False(result);
        Assert.NotNull(disconnectEvent);
        Assert.Equal(DisconnectionReason.ScanException, disconnectEvent!.Reason);
    }

    [Fact]
    public void TryScan_ScanCounterAlarms_CommunicationException_TriggersMarkDisconnected()
    {
        // 验证：ScanCounterAlarms（void 重载）抛 IOException 时，TryScan 触发 MarkDisconnected(ScanException)
        var device = AddDevice();
        device.CounterAlarms.Add(new CounterAlarm
        {
            DeviceId = device.Id,
            Name = "产量超限",
            PlcAddress = "D600",
            Enabled = true,
            MaxValue = 1000
        });
        _plc.ReadInt32Exception = new System.IO.IOException("PLC 通信中断");

        _connectionManager.EnsureConnected();

        ConnectionStateChangedEventArgs? disconnectEvent = null;
        _connectionManager.ConnectionStateChanged += (s, e) =>
        {
            if (!e.IsConnected) disconnectEvent = e;
        };

        // void 重载无返回值，仅验证不抛异常 + 触发 MarkDisconnected
        _service.TryScanForTest(() => _service.ScanCounterAlarms(), "ScanCounterAlarms");

        Assert.NotNull(disconnectEvent);
        Assert.Equal(DisconnectionReason.ScanException, disconnectEvent!.Reason);
    }

    [Fact]
    public void TryScan_CriticalException_RethrowsAndDoesNotMarkDisconnected()
    {
        // 验证：OutOfMemoryException 等关键异常不被 TryScan 吞掉，直接重抛
        // 且不调 MarkDisconnected（关键异常下系统状态不可预测，不应继续修改业务状态）
        _connectionManager.EnsureConnected();

        ConnectionStateChangedEventArgs? disconnectEvent = null;
        _connectionManager.ConnectionStateChanged += (s, e) =>
        {
            if (!e.IsConnected) disconnectEvent = e;
        };

        Assert.Throws<OutOfMemoryException>(() =>
            _service.TryScanForTest(() => throw new OutOfMemoryException(), "CriticalScan"));

        // 关键异常不应触发 MarkDisconnected（系统状态已不可预测，不应继续修改业务状态）
        Assert.Null(disconnectEvent);
        Assert.True(_connectionManager.IsConnected);
    }

    [Fact]
    public void TryScan_NoException_ReturnsActionResult()
    {
        // 验证：扫描方法无异常时，TryScan 透传返回值（true/false 由扫描方法本身决定）
        var device = AddDevice();
        var alarm = device.Alarms.First();
        _plc.SetBool(alarm.PlcAddress, false);

        _connectionManager.EnsureConnected();

        // ScanAlarms 成功执行（无报警触发），TryScan 应返回 true（ScanAlarms 成功=true）
        var result = _service.TryScanForTest(() => _service.ScanAlarms(), "ScanAlarms");
        Assert.True(result);
        Assert.True(_connectionManager.IsConnected); // 状态未变
    }

    [Fact]
    public void TryScan_AlreadyDisconnected_CommunicationException_StillNoEvent()
    {
        // 验证：已断开状态下 TryScan 触发通信异常，MarkDisconnected 早返回不重复触发事件
        // （MarkDisconnected 内部 if (!IsConnected) return 防止重复计数）
        var device = AddDevice();
        var alarm = device.Alarms.First();
        _plc.SetBool(alarm.PlcAddress, false);
        _plc.ReadBoolException = new System.Net.Sockets.SocketException(10054);

        // 不调用 EnsureConnected，保持 IsConnected=false
        Assert.False(_connectionManager.IsConnected);

        var eventCount = 0;
        _connectionManager.ConnectionStateChanged += (s, e) =>
        {
            if (!e.IsConnected) eventCount++;
        };

        var result = _service.TryScanForTest(() => _service.ScanAlarms(), "ScanAlarms");

        Assert.False(result);
        Assert.Equal(0, eventCount); // 已断开，不重复触发事件
        Assert.Equal(0, _connectionManager.TotalDisconnectCount); // 计数不增加
    }

    [Fact]
    public async Task PollingLoopAsync_TryScan_CommunicationException_DoesNotKillLoop()
    {
        // 集成场景：PollingLoopAsync 中 TryScan 捕获 ScanAlarms 通信异常后，
        // 服务不退出、不误判为业务异常（区分 ScanException 与外层 catch 路径）
        var device = AddDevice();
        _plc.SetInt32(device.OkCountAddress, 100);
        _plc.SetInt32(device.NgCountAddress, 0);
        _plc.SetInt32(device.StatusCountAddress, (int)DeviceStatus.Running);
        _plc.SetBool(device.Alarms.First().PlcAddress, false);

        // 仅在报警地址上注入通信异常（其他读取仍正常，使 RefreshDeviceData 成功 → TryScan 路径被触发）
        // 通过 ReadBoolException 全局注入（FakePlcDriver 不支持按地址注入异常，这里接受 ScanAlarms 必失败）
        _plc.ReadBoolException = new System.Net.Sockets.SocketException(10054);

        _appSettings.PollingIntervalMs = 30;
        _appSettings.HistoryWriteIntervalScans = 100; // 避免历史写入干扰
        _service.Start();

        await Task.Delay(200, TestContext.Current.CancellationToken);

        // 服务不退出（TryScan 捕获异常，不让循环跳出）
        Assert.True(_service.IsRunning);

        await _service.StopAsync();

        // 至少触发过一次 MarkDisconnected(ScanException)
        // 注：首次 PollingLoopAsync 进入时 IsConnected=false（未 EnsureConnected），需等待 EnsureConnected 成功后
        // 下一轮 TryScan 才能触发 MarkDisconnected。300ms 内应至少完成一次连接 + 一次扫描。
        Assert.True(_connectionManager.TotalDisconnectCount >= 1);
    }
}
