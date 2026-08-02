using System;
using System.Collections.ObjectModel;
using System.Linq;
using MainAPP.Data;
using MainAPP.Entities;
using MainAPP.Models;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
// v3: ITestOutputHelper 已从 Xunit.Abstractions 移入 Xunit 命名空间

namespace MainAPP.Tests.Unit;

/// <summary>
/// 班次切换端到端链路单测：验证 <see cref="PlcDataAcquisitionService.DetectShiftChange"/> 触发的完整调用链：
/// CacheLastShiftSummaries → LogShiftChangeForActiveAlarms → SetCurrentShift → ResetShift → ProductionBaselineStore.ClearAll。
/// 由于 <c>PollingLoopAsync</c> 含 <c>Task.Delay</c> 难以在单测中直接运行，改用直接调用 internal
/// <c>DetectShiftChange</c>（参考 <c>PlcDataAcquisitionServiceTests</c> 的同款做法）。
///
/// 模拟时间方案（任务推荐的方案 A）：配置两个全天覆盖的班次，
/// 首次调用 DetectShiftChange 初始化 _currentShiftId；
/// 再通过 <c>CurrentShiftIdForTest</c> 注入一个与当前时刻不匹配的"伪班次"作为旧班次，
/// 下一次 DetectShiftChange 即检测到真实切换，完整走完端到端链路。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ShiftChangeEndToEndTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DeviceRepository _deviceRepository;
    private readonly FakePlcDriver _plc;
    private readonly PlcConnectionManager _connectionManager;
    private readonly InMemoryHistoryService _history;
    private readonly ProductionBaselineStore _baselineStore;
    private readonly PlcDataAcquisitionService _service;

    public ShiftChangeEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
        // 临时目录隔离 baselines.json 写入，避免污染真实 %APPDATA%/Kanban
        _tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "KanbanShiftE2E_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_tempDir);

        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        // 全天覆盖：白班 00:00-12:00 + 夜班 12:00-24:00，保证任何时刻都有当前班次（DetectChange 必能初始化）
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

    // ──────────── 测试辅助 ────────────

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

    /// <summary>
    /// 伪造一个与当前时刻不匹配的班次标识并注入 _currentShiftId，
    /// 使下一次 DetectShiftChange 检测到真实班次切换并触发完整链路。
    /// 调用前必须先通过首次 DetectShiftChange 完成初始化。
    /// </summary>
    private ShiftIdentifier SetFakeNonMatchingCurrentShift()
    {
        var now = DateTime.Now.TimeOfDay;
        // 当前在白班(00-12) → 伪造为夜班；当前在夜班(12-24) → 伪造为白班
        var fakeName = now < new TimeSpan(12, 0, 0) ? "夜班" : "白班";
        var fakeStart = now < new TimeSpan(12, 0, 0) ? new TimeSpan(12, 0, 0) : new TimeSpan(0, 0, 0);
        var fakeEnd = now < new TimeSpan(12, 0, 0) ? new TimeSpan(24, 0, 0) : new TimeSpan(12, 0, 0);
        var fake = new ShiftIdentifier(fakeName, fakeStart, fakeEnd);
        _service.CurrentShiftIdForTest = fake;
        return fake;
    }

    // ════════════════════════════════════════════════════════════════
    //  1. 班次切换触发 ResetShift：所有设备 TotalOkProduction 被清零
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ShiftChange_TriggersResetShift_ClearsAllDeviceTotals()
    {
        // 多设备场景：验证 ResetShift 遍历所有设备的 Runtime
        var device1 = AddDevice(id: "dev-001", name: "设备1");
        var device2 = AddDevice(id: "dev-002", name: "设备2",
            okAddr: "D200", ngAddr: "D201", statusAddr: "D202", resetAddr: "D203", alarmAddr: "M200");

        var rt1 = _deviceRepository.RuntimeMap[device1.Id];
        var rt2 = _deviceRepository.RuntimeMap[device2.Id];
        rt1.TotalOkProduction = 100; rt1.TotalNgProduction = 5; rt1.RunTime = 60; rt1.AlarmTime = 10; rt1.PausedTime = 5;
        rt2.TotalOkProduction = 200; rt2.TotalNgProduction = 15; rt2.RunTime = 120; rt2.AlarmTime = 20; rt2.PausedTime = 8;

        _connectionManager.IsConnected = true;

        // 首次调用：仅初始化 _currentShiftId，不触发 ResetShift
        _service.DetectShiftChange();
        Assert.NotNull(_service.CurrentShiftIdForTest);
        // 验证首次未清零（保护性断言，区分"首次初始化"与"班次切换"）
        Assert.Equal(100, rt1.TotalOkProduction);
        Assert.Equal(200, rt2.TotalOkProduction);

        // 伪造不匹配班次 → 下一次 DetectShiftChange 触发真实切换
        SetFakeNonMatchingCurrentShift();
        _service.DetectShiftChange();

        // 关键断言：所有设备的 TotalOkProduction / TotalNgProduction / RunTime / AlarmTime / PausedTime 被清零
        Assert.Equal(0, rt1.TotalOkProduction);
        Assert.Equal(0, rt1.TotalNgProduction);
        Assert.Equal(0, rt1.RunTime);
        Assert.Equal(0, rt1.AlarmTime);
        Assert.Equal(0, rt1.PausedTime);

        Assert.Equal(0, rt2.TotalOkProduction);
        Assert.Equal(0, rt2.TotalNgProduction);
        Assert.Equal(0, rt2.RunTime);
        Assert.Equal(0, rt2.AlarmTime);
        Assert.Equal(0, rt2.PausedTime);
    }

    // ════════════════════════════════════════════════════════════════
    //  2. 班次切换前缓存上班次汇总：GetLastShiftSummary 返回切换前的产量
    //     关键：CacheLastShiftSummaries 必须在 ResetShift 清零之前调用
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ShiftChange_CachesLastShiftSummary_BeforeReset()
    {
        var device1 = AddDevice(id: "dev-001", name: "设备1");
        var device2 = AddDevice(id: "dev-002", name: "设备2",
            okAddr: "D200", ngAddr: "D201", statusAddr: "D202", resetAddr: "D203", alarmAddr: "M200");

        var rt1 = _deviceRepository.RuntimeMap[device1.Id];
        var rt2 = _deviceRepository.RuntimeMap[device2.Id];
        rt1.TotalOkProduction = 150; rt1.TotalNgProduction = 8;
        rt2.TotalOkProduction = 280; rt2.TotalNgProduction = 12;

        _connectionManager.IsConnected = true;

        // 首次调用初始化 _currentShiftId
        _service.DetectShiftChange();

        // 伪造"旧班次"作为切换前的班次标识，下一次 DetectShiftChange 会将其作为 prev shift 缓存
        var fakePrevShift = SetFakeNonMatchingCurrentShift();
        _service.DetectShiftChange();

        // 关键断言：GetLastShiftSummary 返回清零之前的产量
        var s1 = _service.GetLastShiftSummary(device1.Id);
        Assert.Equal(150, s1.Ok);
        Assert.Equal(8, s1.Ng);
        Assert.Equal(fakePrevShift.Name, s1.ShiftName);

        var s2 = _service.GetLastShiftSummary(device2.Id);
        Assert.Equal(280, s2.Ok);
        Assert.Equal(12, s2.Ng);
        Assert.Equal(fakePrevShift.Name, s2.ShiftName);

        // 对照断言：设备 Runtime 已被 ResetShift 清零（与缓存值不同，证明缓存发生在清零之前）
        Assert.Equal(0, rt1.TotalOkProduction);
        Assert.Equal(0, rt2.TotalOkProduction);
    }

    // ════════════════════════════════════════════════════════════════
    //  3. 班次切换清空基线：ProductionBaselineStore 的活动基线被清空
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ShiftChange_ClearsProductionBaselines()
    {
        var device1 = AddDevice(id: "dev-001", name: "设备1");
        var device2 = AddDevice(id: "dev-002", name: "设备2",
            okAddr: "D200", ngAddr: "D201", statusAddr: "D202", resetAddr: "D203", alarmAddr: "M200");

        // 通过读取产量建立基线（2 设备 × OK/NG = 4 条）
        _plc.SetInt32(device1.OkCountAddress, 1000);
        _plc.SetInt32(device1.NgCountAddress, 50);
        _plc.SetInt32(device2.OkCountAddress, 2000);
        _plc.SetInt32(device2.NgCountAddress, 80);

        _service.TryReadOkCount(device1);
        _service.TryReadNgCount(device1);
        _service.TryReadOkCount(device2);
        _service.TryReadNgCount(device2);

        // 保护性断言：基线已建立
        Assert.Equal(4, _baselineStore.ActiveBaselineCount);

        _connectionManager.IsConnected = true;

        // 首次调用初始化班次
        _service.DetectShiftChange();
        // 初始化不应清空已有基线
        Assert.Equal(4, _baselineStore.ActiveBaselineCount);

        // 伪造不匹配班次 → 触发切换 → ResetShift 调用 ClearAll
        SetFakeNonMatchingCurrentShift();
        _service.DetectShiftChange();

        // 关键断言：基线被清空
        Assert.Equal(0, _baselineStore.ActiveBaselineCount);
    }

    // ════════════════════════════════════════════════════════════════
    //  4. 班次切换后新班次从 0 累计：切换后再跑几轮采集，产量从 0 开始
    //     注：IsConnected=false 避免 1 秒清零窗口期干扰基线重建，
    //     使测试可直接验证"新班次产量从 0 累计"的累计逻辑
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ShiftChange_NewShiftAccumulatesFromZero()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        // 首次调用初始化班次（IsConnected 默认 false，不影响 DetectShiftChange）
        _service.DetectShiftChange();

        // 旧班次：建立基线并累计产量
        // 第1轮：raw=1000 → baseline=1000 → TotalOk=0
        _plc.SetInt32(device.OkCountAddress, 1000);
        _service.TryReadOkCount(device);
        Assert.Equal(0, runtime.TotalOkProduction);

        // 第2轮：raw=1050 → baseline=1000 → TotalOk=50
        _plc.SetInt32(device.OkCountAddress, 1050);
        _service.TryReadOkCount(device);
        Assert.Equal(50, runtime.TotalOkProduction);

        // 触发班次切换（IsConnected=false：避免 ScheduleClear 设置清零窗口）
        SetFakeNonMatchingCurrentShift();
        _service.DetectShiftChange();

        // 关键断言1：切换后 TotalOk 已清零
        Assert.Equal(0, runtime.TotalOkProduction);

        // 关键断言2：切换后第一轮读取——基线已清空，以当前 raw=1050 重建 baseline，TotalOk=0
        _service.TryReadOkCount(device);
        Assert.Equal(0, runtime.TotalOkProduction);

        // 关键断言3：后续递增——raw=1075, baseline=1050, TotalOk=25
        _plc.SetInt32(device.OkCountAddress, 1075);
        _service.TryReadOkCount(device);
        Assert.Equal(25, runtime.TotalOkProduction);

        // 关键断言4：再递增——raw=1100, baseline=1050, TotalOk=50
        _plc.SetInt32(device.OkCountAddress, 1100);
        _service.TryReadOkCount(device);
        Assert.Equal(50, runtime.TotalOkProduction);
    }

    // ════════════════════════════════════════════════════════════════
    //  5. 未切换班次时不触发 ResetShift：同一班次内多轮采集，产量持续累计
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void NoShiftChange_DoesNotTriggerReset_ProductionAccumulates()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        // 首次调用初始化班次
        _service.DetectShiftChange();
        var initialShift = _service.CurrentShiftIdForTest!;
        Assert.NotNull(initialShift);

        // 模拟多轮采集：每轮先 TryReadOkCount 再 DetectShiftChange（与 PollingLoopAsync 顺序一致）
        // 第1轮：raw=1000, baseline=1000, TotalOk=0
        _plc.SetInt32(device.OkCountAddress, 1000);
        _service.TryReadOkCount(device);
        _service.DetectShiftChange();  // 同一班次，不应触发 ResetShift
        Assert.Equal(0, runtime.TotalOkProduction);
        Assert.Equal(initialShift, _service.CurrentShiftIdForTest);

        // 第2轮：raw=1050, TotalOk=50
        _plc.SetInt32(device.OkCountAddress, 1050);
        _service.TryReadOkCount(device);
        _service.DetectShiftChange();  // 同一班次，不应触发 ResetShift
        Assert.Equal(50, runtime.TotalOkProduction);
        Assert.Equal(initialShift, _service.CurrentShiftIdForTest);

        // 第3轮：raw=1080, TotalOk=80
        _plc.SetInt32(device.OkCountAddress, 1080);
        _service.TryReadOkCount(device);
        _service.DetectShiftChange();  // 同一班次，不应触发 ResetShift
        Assert.Equal(80, runtime.TotalOkProduction);
        Assert.Equal(initialShift, _service.CurrentShiftIdForTest);

        // 第4轮：raw=1100, TotalOk=100
        _plc.SetInt32(device.OkCountAddress, 1100);
        _service.TryReadOkCount(device);
        _service.DetectShiftChange();  // 同一班次，不应触发 ResetShift
        Assert.Equal(100, runtime.TotalOkProduction);
        Assert.Equal(initialShift, _service.CurrentShiftIdForTest);

        // 关键断言：4 轮采集后产量持续累计，未被清零
        Assert.Equal(100, runtime.TotalOkProduction);
    }

    // ════════════════════════════════════════════════════════════════
    //  6. 首次初始化不触发 ResetShift：首次进入循环只设置 _currentShiftId
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void FirstInit_OnlySetsCurrentShiftId_NoResetTriggered()
    {
        var device = AddDevice();
        var runtime = _deviceRepository.RuntimeMap[device.Id];

        // 预设运行时累计值（模拟应用启动前已有数据，或从历史重建后的状态）
        runtime.TotalOkProduction = 500;
        runtime.TotalNgProduction = 30;
        runtime.RunTime = 200;
        runtime.AlarmTime = 15;
        runtime.PausedTime = 10;

        _connectionManager.IsConnected = true;

        // 首次调用：仅初始化 _currentShiftId，不触发 ResetShift
        Assert.Null(_service.CurrentShiftIdForTest);
        _service.DetectShiftChange();

        // _currentShiftId 已设置为当前实际班次
        Assert.NotNull(_service.CurrentShiftIdForTest);
        var firstName = _service.CurrentShiftIdForTest!.Name;
        Assert.True(firstName == "白班" || firstName == "夜班");

        // 关键断言：运行时累计值未被清零（首次初始化不触发 ResetShift）
        Assert.Equal(500, runtime.TotalOkProduction);
        Assert.Equal(30, runtime.TotalNgProduction);
        Assert.Equal(200, runtime.RunTime);
        Assert.Equal(15, runtime.AlarmTime);
        Assert.Equal(10, runtime.PausedTime);

        // 班次切换事件未写入（无活跃报警 + 无切换发生）
        Assert.Empty(_history.AlarmEvents);

        // 二次调用（同一班次）：仍不应触发 ResetShift
        _service.DetectShiftChange();
        Assert.Equal(500, runtime.TotalOkProduction);
        Assert.Equal(firstName, _service.CurrentShiftIdForTest!.Name);
    }

    // ════════════════════════════════════════════════════════════════
    //  额外：班次切换对活跃报警写入 EventType=3 事件 + 清空报警内存状态
    //       验证完整链路：CacheLastShiftSummaries + LogShiftChangeForActiveAlarms + ResetShift
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ShiftChange_LogsShiftChangeEventForActiveAlarms_AndClearsAlarmState()
    {
        var device = AddDevice();
        var alarm = device.Alarms.First();

        _connectionManager.IsConnected = true;

        // 首次调用初始化班次
        _service.DetectShiftChange();

        // 触发报警上升沿（便于验证班次切换事件写入）
        _plc.SetBool(alarm.PlcAddress, true);
        _service.ScanAlarms();
        Assert.True(_service.PrevAlarmStatesForTest[alarm.Id]);
        Assert.Single(_history.AlarmEvents);  // Triggered 事件

        // 伪造不匹配班次作为"旧班次" → 下一次 DetectShiftChange 触发真实切换
        var fakePrevShift = SetFakeNonMatchingCurrentShift();
        _service.DetectShiftChange();

        // 关键断言1：班次切换事件已写入（报警当前触发中，应记录 EventType=ShiftChange）
        var shiftChangeEvents = _history.AlarmEvents
            .Where(e => e.EventType == AlarmEventType.ShiftChange).ToList();
        Assert.Single(shiftChangeEvents);
        Assert.Equal(alarm.Id, shiftChangeEvents[0].AlarmId);
        // ShiftName 应为旧班次名称（CacheLastShiftSummaries/LogShiftChangeForActiveAlarms 在 SetCurrentShift 之前调用）
        Assert.Equal(fakePrevShift.Name, shiftChangeEvents[0].ShiftName);

        // 关键断言2：ResetShift 已清空报警内存状态（_prevAlarmStates）
        Assert.False(_service.PrevAlarmStatesForTest.ContainsKey(alarm.Id));

        // 关键断言3：班次标识已更新为新班次（与旧班次不同）
        Assert.NotEqual(fakePrevShift.Name, _service.CurrentShiftIdForTest!.Name);
    }
}
