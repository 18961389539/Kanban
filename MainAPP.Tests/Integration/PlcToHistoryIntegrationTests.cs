using System.Collections.ObjectModel;
using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
// v3: ITestOutputHelper 已从 Xunit.Abstractions 移入 Xunit 命名空间

namespace MainAPP.Tests.Integration;

/// <summary>
/// 端到端集成测试：验证 PLC → 采集服务 → 历史 SQLite → 重启后查询 的完整数据链路。
///
/// 与 <see cref="MainAPP.Tests.Unit.PlcDataAcquisitionServiceTests"/> 的区别：
/// - 单元测试用 <see cref="InMemoryHistoryService"/>（纯内存，无 IO）
/// - 此集成测试用生产实现 <see cref="HistoryService"/>（基于 SQLite/EF Core + Channel 异步 flush）
///
/// 关键验证点：
/// - 一次完整采集周期的所有事件（产量快照/报警边沿/状态转换）都能落库到 SQLite
/// - HistoryService DisposeAsync 后数据真正持久化（通道 flush 完成）
/// - 重启场景：新建 HistoryService 实例指向同一组 .db 文件，能查到上一会话写入的数据
/// - 多设备并发采集：3 台设备混合状态（成功/未配置/失败）正确写入
/// - 报警上升沿 + 下降沿循环：两条事件均落库，重启后可查
/// - 状态转换：Running → Idle → Alarm 三次转换均落库
///
/// 这些用例跨多个真实组件边界（PLC 抽象 / 采集服务 / Channel / EF Core / SQLite WAL），
/// 用于捕获单元测试无法发现的集成问题（序列化字段缺失、Channel 未 flush、表结构不匹配等）。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","None")]
public class PlcToHistoryIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _dbProvider;

    public PlcToHistoryIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanE2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _appSettings = new AppSettings
        {
            ConfigDirectory = _tempDir,
            // 每轮采集都写历史快照，避免等待 25 轮才落库
            HistoryWriteIntervalScans = 1,
        };
        _appSettings.Shifts = new ObservableCollection<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new(0, 0, 0), EndTime = new(24, 0, 0) }
        };

        _dbProvider = new DatabaseProvider(_appSettings);
        // 建表（EnsureCreated 不会因表已存在而报错）
        using (var pCtx = _dbProvider.CreateProductionLogContext()) pCtx.Database.EnsureCreated();
        using (var aCtx = _dbProvider.CreateAlarmEventContext()) aCtx.Database.EnsureCreated();
        using (var sCtx = _dbProvider.CreateStatusTransitionContext()) sCtx.Database.EnsureCreated();
        // 启用 WAL，与生产启动流程一致
        _dbProvider.EnsureWalModeEnabled();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* 测试间清理 */ }
    }

    /// <summary>
    /// 构造一台配置齐全的设备（OK/NG/状态/清零地址 + 1 个 M 位报警），
    /// 并注册到 DeviceRepository（同步创建 Runtime）。
    /// </summary>
    private static Device BuildDevice(
        string id, string name,
        string okAddr, string ngAddr, string statusAddr,
        string resetAddr, string alarmAddr)
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
            Name = $"报警_{id}",
            PlcAddress = alarmAddr,
            Level = AlarmLevel.High
        });
        return device;
    }

    /// <summary>
    /// 完整一轮采集周期：班次检测 → EnsureConnected → RefreshDeviceData →
    /// AccumulateOeeTime → ScanAlarms → LogProductionSnapshot。
    /// 模拟 PollingLoopAsync 一次循环的核心步骤（去掉 Delay 和班次切换 ResetShift）。
    /// </summary>
    private static void RunOneScanCycle(PlcDataAcquisitionService svc, PlcConnectionManager conn, double elapsedSeconds = 0.2)
    {
        svc.DetectShiftChange();
        conn.EnsureConnected();
        if (conn.IsConnected)
        {
            var successDevices = svc.RefreshDeviceData(out _);
            svc.AccumulateOeeTime(elapsedSeconds, successDevices);
            svc.ScanAlarms();
            // LogProductionSnapshot 内部按 _scanCount % HistoryWriteIntervalScans 判断是否落库
            // HistoryWriteIntervalScans=1 时每次都写
            svc.LogProductionSnapshot(successDevices);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  完整链路：PLC → 采集 → Channel flush → SQLite → 重启后查询
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullScanCycle_PersistsAllEventTypesToSqlite_AcrossRestart()
    {
        // ── 1. 准备：3 台设备，配置齐全 ──
        var deviceRepo = new DeviceRepository(_appSettings);
        var plc = new FakePlcDriver();
        var connMgr = new PlcConnectionManager(plc, _appSettings);
        var baselineStore = new ProductionBaselineStore(_appSettings);

        var dev1 = BuildDevice("dev-001", "设备1", "D100", "D101", "D102", "D103", "M100");
        var dev2 = BuildDevice("dev-002", "设备2", "D200", "D201", "D202", "D203", "M200");
        var dev3 = BuildDevice("dev-003", "设备3", "D300", "D301", "D302", "D303", "M300");
        foreach (var d in new[] { dev1, dev2, dev3 })
        {
            deviceRepo.Devices.Add(d);
            deviceRepo.AddRuntime(d);
        }

        // 预置 PLC 数据：OK 产量、NG 产量、状态字、报警位
        // dev1：运行中，OK=1000, NG=10，报警触发
        plc.SetInt32("D100", 1000);
        plc.SetInt32("D101", 10);
        plc.SetInt32("D102", (int)DeviceStatus.Running);
        plc.SetBool("M100", true);
        // dev2：暂停，OK=500, NG=5
        plc.SetInt32("D200", 500);
        plc.SetInt32("D201", 5);
        plc.SetInt32("D202", (int)DeviceStatus.Paused);
        plc.SetBool("M200", false);
        // dev3：待机（状态字=0），OK=0, NG=0
        plc.SetInt32("D300", 0);
        plc.SetInt32("D301", 0);
        plc.SetInt32("D302", (int)DeviceStatus.Unknown);
        plc.SetBool("M300", false);

        // ── 2. 第一会话：跑两轮采集（第二轮触发报警下降沿） ──
        HistoryService? history1 = null;
        try
        {
            history1 = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
            var svc1 = new PlcDataAcquisitionService(
                plc, connMgr, _appSettings, history1, deviceRepo, baselineStore,
                NullLogger<PlcDataAcquisitionService>.Instance);

            // 首轮：dev1 报警上升沿（PLC=true，初始 prevAlarm 未记录）
            RunOneScanCycle(svc1, connMgr);
            Assert.True(connMgr.IsConnected);

            // 第二轮：dev1 报警下降沿（PLC=false 触发 Recovered 事件）
            plc.SetBool("M100", false);
            RunOneScanCycle(svc1, connMgr);

            // 等待 Channel flush（生产代码 5s 一次，但 DisposeAsync 会强制 flush）
            await history1.DisposeAsync();
        }
        finally
        {
            if (history1 != null) history1.Dispose();
        }

        // ── 3. 重启：新建 HistoryService 指向同一组 .db 文件 ──
        using var history2 = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);

        // ── 4. 验证 ProductionLog ──
        // 每台设备每轮写一条快照，3 台 × 2 轮 = 6 条
        var today = DateTime.Today;
        var prodLogs = history2.QueryProductionLogs(
            today, today.AddDays(1).AddSeconds(-1));
        Assert.Equal(6, prodLogs.Count);

        // dev1 的两条快照：OK 应分别为 0（首次基线=1000）和 0（PLC 值未变）
        var dev1Logs = prodLogs.Where(p => p.DeviceId == "dev-001").OrderBy(p => p.Timestamp).ToList();
        Assert.Equal(2, dev1Logs.Count);
        Assert.Equal("设备1", dev1Logs[0].DeviceName);
        Assert.Equal("白班", dev1Logs[0].ShiftName);

        // ── 5. 验证 AlarmEventRecord：dev1 报警上升沿 + 下降沿 ──
        var alarmEvents = history2.QueryAlarmEvents(today, today.AddDays(1).AddSeconds(-1));
        Assert.Equal(2, alarmEvents.Count);
        Assert.Equal(AlarmEventType.Triggered, alarmEvents[0].EventType);
        Assert.Equal(AlarmEventType.Recovered, alarmEvents[1].EventType);
        Assert.Equal(dev1.Alarms[0].Id, alarmEvents[0].AlarmId);
        Assert.Equal("白班", alarmEvents[0].ShiftName);

        // ── 6. 验证 StatusTransitionRecord：每台设备首次读取都写 0→status ──
        var dev1Transitions = history2.QueryStatusTransitions("dev-001", today, today.AddDays(1).AddSeconds(-1));
        Assert.NotEmpty(dev1Transitions);
        // 首次写入 PreviousState=0, CurrentState=Running
        Assert.Equal(0, dev1Transitions[0].PreviousState);
        Assert.Equal((int)DeviceStatus.Running, dev1Transitions[0].CurrentState);

        // ── 7. 验证 GetLatestAlarmEvent：重启后可查最新报警 ──
        var latestAlarm = history2.GetLatestAlarmEvent(dev1.Alarms[0].Id);
        Assert.NotNull(latestAlarm);
        Assert.Equal(AlarmEventType.Recovered, latestAlarm!.EventType);

        // ── 8. 验证 GetLatestStatusBefore：可查询窗口前的最近状态 ──
        var latestStatus = history2.GetLatestStatusBefore("dev-001", DateTime.Now.AddSeconds(1));
        Assert.NotNull(latestStatus);
        Assert.Equal((int)DeviceStatus.Running, latestStatus!.CurrentState);
    }

    // ════════════════════════════════════════════════════════════════
    //  多设备混合场景：成功 / 未配置地址 / 读取失败
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MultiDevice_MixedConfigurations_OnlySuccessfulDevicesPersist()
    {
        var deviceRepo = new DeviceRepository(_appSettings);
        var plc = new FakePlcDriver();
        var connMgr = new PlcConnectionManager(plc, _appSettings);
        var baselineStore = new ProductionBaselineStore(_appSettings);

        // dev1：配置齐全，正常读取
        var dev1 = BuildDevice("dev-ok", "成功设备", "D100", "D101", "D102", "D103", "M100");
        // dev2：未配置 OK/NG/状态地址（模拟仅有设备名但无 PLC 地址）
        var dev2 = new Device { Id = "dev-noconfig", Name = "无地址设备" };
        // dev3：地址指向未初始化的 PLC 区域（FakePlcDriver.ReadXxx 返回 Fail）
        var dev3 = BuildDevice("dev-fail", "失败设备", "D999", "D998", "D997", "D996", "M995");

        foreach (var d in new[] { dev1, dev2, dev3 })
        {
            deviceRepo.Devices.Add(d);
            deviceRepo.AddRuntime(d);
        }

        // 仅给 dev1 预置 PLC 数据
        plc.SetInt32("D100", 500);
        plc.SetInt32("D101", 5);
        plc.SetInt32("D102", (int)DeviceStatus.Running);
        plc.SetBool("M100", false);

        HistoryService? history = null;
        try
        {
            history = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
            var svc = new PlcDataAcquisitionService(
                plc, connMgr, _appSettings, history, deviceRepo, baselineStore,
                NullLogger<PlcDataAcquisitionService>.Instance);

            RunOneScanCycle(svc, connMgr);

            // 等待 flush
            await history.DisposeAsync();
        }
        finally
        {
            if (history != null) history.Dispose();
        }

        // 验证：只有 dev1 的快照落库
        using var history2 = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
        var today = DateTime.Today;
        var logs = history2.QueryProductionLogs(today, today.AddDays(1).AddSeconds(-1));
        var log = Assert.Single(logs);
        Assert.Equal("dev-ok", log.DeviceId);
        Assert.Equal("成功设备", log.DeviceName);
    }

    // ════════════════════════════════════════════════════════════════
    //  连续多轮采集 + 产量增量
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MultipleScanCycles_ProductionIncremental_PersistsEachSnapshot()
    {
        var deviceRepo = new DeviceRepository(_appSettings);
        var plc = new FakePlcDriver();
        var connMgr = new PlcConnectionManager(plc, _appSettings);
        var baselineStore = new ProductionBaselineStore(_appSettings);

        var dev = BuildDevice("dev-inc", "增量设备", "D100", "D101", "D102", "D103", "M100");
        deviceRepo.Devices.Add(dev);
        deviceRepo.AddRuntime(dev);

        // 首次预置：OK=1000, NG=0, Running
        plc.SetInt32("D100", 1000);
        plc.SetInt32("D101", 0);
        plc.SetInt32("D102", (int)DeviceStatus.Running);
        plc.SetBool("M100", false);

        HistoryService? history = null;
        try
        {
            history = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
            var svc = new PlcDataAcquisitionService(
                plc, connMgr, _appSettings, history, deviceRepo, baselineStore,
                NullLogger<PlcDataAcquisitionService>.Instance);

            // 跑 5 轮，每轮 OK 产量递增 50
            for (int i = 0; i < 5; i++)
            {
                plc.SetInt32("D100", 1000 + (i + 1) * 50);
                RunOneScanCycle(svc, connMgr);
            }

            await history.DisposeAsync();
        }
        finally
        {
            if (history != null) history.Dispose();
        }

        using var history2 = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
        var today = DateTime.Today;
        var logs = history2.QueryProductionLogs(today, today.AddDays(1).AddSeconds(-1), "dev-inc");
        Assert.Equal(5, logs.Count);

        // 验证 OK 产量递增：首轮 baseline = 1050（首次读取的 PLC 值），TotalOk=0
        // 后续每轮 TotalOkProduction = raw - 1050 = i*50
        Assert.Equal(0, logs[0].OkProduction);
        for (int i = 1; i < 5; i++)
        {
            Assert.Equal(i * 50, logs[i].OkProduction);
        }

        // 状态转换应有 1 条（首次 0→Running）
        var transitions = history2.QueryStatusTransitions("dev-inc", today, today.AddDays(1).AddSeconds(-1));
        Assert.Single(transitions);
        Assert.Equal(0, transitions[0].PreviousState);
        Assert.Equal((int)DeviceStatus.Running, transitions[0].CurrentState);
    }

    // ════════════════════════════════════════════════════════════════
    //  报警上升沿 → 下降沿 → 上升沿 循环
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AlarmRisingFallingRisingCycle_AllEventsPersist()
    {
        var deviceRepo = new DeviceRepository(_appSettings);
        var plc = new FakePlcDriver();
        var connMgr = new PlcConnectionManager(plc, _appSettings);
        var baselineStore = new ProductionBaselineStore(_appSettings);

        var dev = BuildDevice("dev-alarm", "报警设备", "D100", "D101", "D102", "D103", "M100");
        deviceRepo.Devices.Add(dev);
        deviceRepo.AddRuntime(dev);

        plc.SetInt32("D100", 0);
        plc.SetInt32("D101", 0);
        plc.SetInt32("D102", (int)DeviceStatus.Running);
        plc.SetBool("M100", false); // 初始未触发

        HistoryService? history = null;
        try
        {
            history = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
            var svc = new PlcDataAcquisitionService(
                plc, connMgr, _appSettings, history, deviceRepo, baselineStore,
                NullLogger<PlcDataAcquisitionService>.Instance);

            // 第1轮：未触发
            RunOneScanCycle(svc, connMgr);
            // 第2轮：上升沿
            plc.SetBool("M100", true);
            RunOneScanCycle(svc, connMgr);
            // 第3轮：下降沿
            plc.SetBool("M100", false);
            RunOneScanCycle(svc, connMgr);
            // 第4轮：再次上升沿
            plc.SetBool("M100", true);
            RunOneScanCycle(svc, connMgr);

            await history.DisposeAsync();
        }
        finally
        {
            if (history != null) history.Dispose();
        }

        using var history2 = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
        var today = DateTime.Today;
        var alarms = history2.QueryAlarmEvents(today, today.AddDays(1).AddSeconds(-1), "dev-alarm");

        // 期望 3 条事件：Triggered → Recovered → Triggered
        Assert.Equal(3, alarms.Count);
        Assert.Equal(AlarmEventType.Triggered, alarms[0].EventType);
        Assert.Equal(AlarmEventType.Recovered, alarms[1].EventType);
        Assert.Equal(AlarmEventType.Triggered, alarms[2].EventType);

        // 时间戳单调递增
        Assert.True(alarms[0].EventTime <= alarms[1].EventTime);
        Assert.True(alarms[1].EventTime <= alarms[2].EventTime);
    }

    // ════════════════════════════════════════════════════════════════
    //  状态转换：Running → Paused → Alarm
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StatusTransitions_RunningPausedAlarm_AllPersistInOrder()
    {
        var deviceRepo = new DeviceRepository(_appSettings);
        var plc = new FakePlcDriver();
        var connMgr = new PlcConnectionManager(plc, _appSettings);
        var baselineStore = new ProductionBaselineStore(_appSettings);

        var dev = BuildDevice("dev-st", "状态设备", "D100", "D101", "D102", "D103", "M100");
        deviceRepo.Devices.Add(dev);
        deviceRepo.AddRuntime(dev);

        plc.SetInt32("D100", 0);
        plc.SetInt32("D101", 0);
        plc.SetBool("M100", false);

        HistoryService? history = null;
        try
        {
            history = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
            var svc = new PlcDataAcquisitionService(
                plc, connMgr, _appSettings, history, deviceRepo, baselineStore,
                NullLogger<PlcDataAcquisitionService>.Instance);

            // 第1轮：Running
            plc.SetInt32("D102", (int)DeviceStatus.Running);
            RunOneScanCycle(svc, connMgr);
            // 第2轮：Paused
            plc.SetInt32("D102", (int)DeviceStatus.Paused);
            RunOneScanCycle(svc, connMgr);
            // 第3轮：Alarm
            plc.SetInt32("D102", (int)DeviceStatus.Alarm);
            RunOneScanCycle(svc, connMgr);
            // 第4轮：再次 Running
            plc.SetInt32("D102", (int)DeviceStatus.Running);
            RunOneScanCycle(svc, connMgr);

            await history.DisposeAsync();
        }
        finally
        {
            if (history != null) history.Dispose();
        }

        using var history2 = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
        var today = DateTime.Today;
        var transitions = history2.QueryStatusTransitions("dev-st", today, today.AddDays(1).AddSeconds(-1));

        // 期望 4 条：0→Running, Running→Paused, Paused→Alarm, Alarm→Running
        Assert.Equal(4, transitions.Count);
        Assert.Equal(0, transitions[0].PreviousState);
        Assert.Equal((int)DeviceStatus.Running, transitions[0].CurrentState);
        Assert.Equal((int)DeviceStatus.Running, transitions[1].PreviousState);
        Assert.Equal((int)DeviceStatus.Paused, transitions[1].CurrentState);
        Assert.Equal((int)DeviceStatus.Paused, transitions[2].PreviousState);
        Assert.Equal((int)DeviceStatus.Alarm, transitions[2].CurrentState);
        Assert.Equal((int)DeviceStatus.Alarm, transitions[3].PreviousState);
        Assert.Equal((int)DeviceStatus.Running, transitions[3].CurrentState);
    }

    // ════════════════════════════════════════════════════════════════
    //  班次切换：报警 ShiftChange 事件落库
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShiftChange_AlarmStillActive_LogsShiftChangeEvent()
    {
        // 两个班次：白班 00:00-12:00, 夜班 12:00-24:00
        _appSettings.Shifts = new ObservableCollection<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new(0, 0, 0), EndTime = new(12, 0, 0) },
            new() { Name = "夜班", StartTime = new(12, 0, 0), EndTime = new(24, 0, 0) }
        };

        var deviceRepo = new DeviceRepository(_appSettings);
        var plc = new FakePlcDriver();
        var connMgr = new PlcConnectionManager(plc, _appSettings);
        var baselineStore = new ProductionBaselineStore(_appSettings);

        var dev = BuildDevice("dev-shift", "班次设备", "D100", "D101", "D102", "D103", "M100");
        deviceRepo.Devices.Add(dev);
        deviceRepo.AddRuntime(dev);

        plc.SetInt32("D100", 100);
        plc.SetInt32("D101", 0);
        plc.SetInt32("D102", (int)DeviceStatus.Running);
        plc.SetBool("M100", true); // 报警持续触发

        HistoryService? history = null;
        try
        {
            history = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
            var svc = new PlcDataAcquisitionService(
                plc, connMgr, _appSettings, history, deviceRepo, baselineStore,
                NullLogger<PlcDataAcquisitionService>.Instance);

            // 首轮：触发报警上升沿，初始化班次
            RunOneScanCycle(svc, connMgr);
            var firstShift = svc.CurrentShiftIdForTest!;
            Assert.NotNull(firstShift);

            // 模拟切换到另一个班次：把 CurrentShiftIdForTest 改成不匹配的班次
            var now = DateTime.Now.TimeOfDay;
            var fakeShiftName = now < new TimeSpan(12, 0, 0) ? "夜班" : "白班";
            var fakeStart = now < new TimeSpan(12, 0, 0) ? new TimeSpan(12, 0, 0) : new TimeSpan(0, 0, 0);
            var fakeEnd = now < new TimeSpan(12, 0, 0) ? new TimeSpan(24, 0, 0) : new TimeSpan(12, 0, 0);
            svc.CurrentShiftIdForTest = new ShiftIdentifier(fakeShiftName, fakeStart, fakeEnd);

            // 再跑一轮：DetectShiftChange 检测到不一致 → 触发 ResetShift → LogShiftChangeForActiveAlarms
            RunOneScanCycle(svc, connMgr);

            await history.DisposeAsync();
        }
        finally
        {
            if (history != null) history.Dispose();
        }

        using var history2 = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
        var today = DateTime.Today;
        var alarms = history2.QueryAlarmEvents(today, today.AddDays(1).AddSeconds(-1), "dev-shift");

        // 至少 2 条：Triggered + ShiftChange（报警仍触发，班次切换事件应写入）
        Assert.True(alarms.Count >= 2);
        Assert.Equal(AlarmEventType.Triggered, alarms[0].EventType);
        Assert.Contains(alarms, a => a.EventType == AlarmEventType.ShiftChange);
    }

    // ════════════════════════════════════════════════════════════════
    //  离线转换：PLC 断开时设备状态被标记为 Unknown(0)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PlcDisconnect_LogsOfflineTransitionToUnknown()
    {
        var deviceRepo = new DeviceRepository(_appSettings);
        var plc = new FakePlcDriver();
        var connMgr = new PlcConnectionManager(plc, _appSettings);
        var baselineStore = new ProductionBaselineStore(_appSettings);

        var dev = BuildDevice("dev-offline", "离线设备", "D100", "D101", "D102", "D103", "M100");
        deviceRepo.Devices.Add(dev);
        deviceRepo.AddRuntime(dev);

        plc.SetInt32("D100", 100);
        plc.SetInt32("D101", 0);
        plc.SetInt32("D102", (int)DeviceStatus.Running);
        plc.SetBool("M100", false);

        HistoryService? history = null;
        try
        {
            history = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
            var svc = new PlcDataAcquisitionService(
                plc, connMgr, _appSettings, history, deviceRepo, baselineStore,
                NullLogger<PlcDataAcquisitionService>.Instance);

            // 第1轮：正常采集，设备 Running
            RunOneScanCycle(svc, connMgr);
            Assert.True(connMgr.IsConnected);

            // 主动断开连接，模拟 PLC 断线
            connMgr.Disconnect();
            Assert.False(connMgr.IsConnected);

            // 第2轮：断开状态下扫描，应触发 LogOfflineTransition（Running → Unknown=0）
            // 注意：断开时不进入 RefreshDeviceData 分支，但 PollingLoopAsync 会调用 LogOfflineTransition
            // 这里手动调用内部方法模拟 PollingLoopAsync 的断开分支
            foreach (var d in deviceRepo.GetDevicesSnapshot())
            {
                svc.LogOfflineTransition(d);
            }

            await history.DisposeAsync();
        }
        finally
        {
            if (history != null) history.Dispose();
        }

        using var history2 = new HistoryService(_dbProvider, NullLogger<HistoryService>.Instance);
        var today = DateTime.Today;
        var transitions = history2.QueryStatusTransitions("dev-offline", today, today.AddDays(1).AddSeconds(-1));

        // 至少 2 条：0→Running + Running→Unknown(0)
        Assert.True(transitions.Count >= 2);
        var offlineTransition = transitions.First(t => t.CurrentState == 0);
        Assert.Equal((int)DeviceStatus.Running, offlineTransition.PreviousState);
    }
}
