using System;
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


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class HomeViewModelTests : IDisposable
{
    private readonly AppSettings _appSettings;
    private readonly DeviceRepository _deviceRepository;
    private readonly IPlcDriver _plcDriver;
    private readonly PlcConnectionManager _connectionManager;
    private readonly IDeviceSelectionService _selection = new DeviceSelectionService();

    public HomeViewModelTests()
    {
        _appSettings = new AppSettings();
        _appSettings.Shifts.Clear();
        _appSettings.Shifts.Add(new ShiftConfig { Name = "白班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) });
        _appSettings.Shifts.Add(new ShiftConfig { Name = "夜班", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) });

        // 使用 FakePlcDriver 替代真实 HslPlcDriver，避免 HslCommunication 后台线程导致 testhost 无法退出
        _plcDriver = new FakePlcDriver();
        _connectionManager = new PlcConnectionManager(_plcDriver, _appSettings);
        _deviceRepository = new DeviceRepository(_appSettings);
    }

    public void Dispose()
    {
        // PlcConnectionManager 无外部资源，无需清理
    }

    [Fact]
    public void Constructor_WithoutDevices_EmptyFilterItems()
    {
        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);
        Assert.Empty(vm.DeviceFilterItems);
        Assert.Null(vm.SelectedDeviceId);
        Assert.Equal("未选择设备", vm.DataStatusText);
        Assert.Equal("—", vm.OeeDisplay);
        Assert.Equal("—", vm.RealtimeSpeedDisplay);
    }

    [Fact]
    public void Constructor_WithDevice_AutoSelectsFirst()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepository.Devices.Add(device);

        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);
        Assert.Equal("d1", vm.SelectedDeviceId);
        Assert.Single(vm.DeviceFilterItems);
    }

    [Fact]
    public void SelectDevice_UpdatesCurrentDeviceAndRuntime()
    {
        var device = AddDeviceWithRuntime("d1", "设备1");
        var rt = _deviceRepository.Runtimes.First(r => r.DeviceId == "d1");
        rt.TotalOkProduction = 42;

        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);
        Assert.NotNull(vm.CurrentDevice);
        Assert.NotNull(vm.CurrentRuntime);
        Assert.Equal(42, vm.TotalOkProduction);
    }

    [Fact]
    public void SelectDevice_WithoutRuntime_ClearsLiveData()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepository.Devices.Add(device);
        _deviceRepository.Runtimes.Clear(); // 模拟无 Runtime

        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);
        Assert.Equal(0, vm.OeeValue);
        Assert.Equal(0, vm.TotalOkProduction);
        Assert.Equal("", vm.RunTimeFormatted);
        Assert.Equal("PLC断开", vm.DataStatusText);
        Assert.Equal("—", vm.QualityRateDisplay);
        Assert.Equal("—", vm.TotalOutputDisplay);
    }

    [Fact]
    public void RefreshActiveAlarms_CollectsFromAllDevices()
    {
        var dev1 = CreateDevice("d1", "设备1");
        var dev2 = CreateDevice("d2", "设备2");
        _deviceRepository.Devices.Add(dev1);
        _deviceRepository.Devices.Add(dev2);

        // 两台设备各设一个活跃报警（StartTime 已设，EndTime 未设）
        dev1.Alarms.Add(new Alarm
        {
            Id = "a1", Name = "报警A", Level = AlarmLevel.High,
            StartTime = DateTime.Now, EndTime = default
        });
        dev2.Alarms.Add(new Alarm
        {
            Id = "a2", Name = "报警B", Level = AlarmLevel.Medium,
            StartTime = DateTime.Now.AddMinutes(-5), EndTime = default
        });

        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);

        Assert.Equal(2, vm.ActiveAlarms.Count);
        Assert.True(vm.HasHighLevelAlarm);
    }

    [Fact]
    public void RefreshActiveAlarms_IgnoresRecoveredAlarms()
    {
        var dev = CreateDevice("d1", "设备1");
        _deviceRepository.Devices.Add(dev);
        dev.Alarms.Add(new Alarm
        {
            Id = "a1", Name = "已结束报警", Level = AlarmLevel.Low,
            StartTime = DateTime.Now.AddMinutes(-5),
            EndTime = DateTime.Now.AddMinutes(-1)
        });

        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);
        Assert.Empty(vm.ActiveAlarms);
    }

    // 原 CollectionChanged_UpdatesDeviceFilterItems 因依赖 Application.Current.Dispatcher 永久 Skip 而删除
    // （审查修复 2026-08-13）：HomeViewModel.OnDevicesCollectionChanged 的回调经 Dispatcher.BeginInvoke，
    // CLI 测试环境无 Dispatcher 无法执行；同类逻辑已由
    // HistoryQueryViewModelTests.DevicesChangedFromBackgroundThread_RefreshesFilterWithoutCrossThreadException
    // 与 HomeViewRenderTests.PlcDisconnected_Banner_StateBinding_Works（STA fixture）覆盖。

    [Fact]
    public void Dispose_StopsTimer_WithoutException()
    {
        var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Runtime_AppliesOeeValues()
    {
        var dev = AddDeviceWithRuntime("d1", "设备1", targetCycle: 100);
        var rt = _deviceRepository.Runtimes.First(r => r.DeviceId == "d1");
        // Runtime 的比率是计算属性，通过设基础值间接推导
        rt.TotalOkProduction = 95;
        rt.TotalNgProduction = 5;
        rt.RunTime = 3600;
        rt.AlarmTime = 300;
        rt.PausedTime = 600;

        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);
        Assert.Equal(0.95, vm.QualityRate, 3); // 95/(95+5)
        Assert.Equal(1.0, vm.PerformanceRate, 3); // (95+5)/(100*3600/3600)=1.0
        Assert.Equal(3600.0 / 3900.0, vm.AvailabilityRate, 3); // 3600/(3600+300)
        Assert.Equal("1h 0m", vm.RunTimeFormatted);
        Assert.Equal("5m 0s", vm.AlarmTimeFormatted);
        Assert.Equal("10m 0s", vm.PausedTimeFormatted);
        Assert.Equal(100, vm.TargetSpeed);
        Assert.Equal(36, vm.TargetCycleSec, 1);
    }

    // ──────────── ViewDeviceDetailCommand ────────────

    [Fact]
    public void ViewDeviceDetailCommand_WithSelectedDevice_RaisesEventAndSyncsSelection()
    {
        AddDeviceWithRuntime("d1", "设备1");
        AddDeviceWithRuntime("d2", "设备2");
        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);

        // 默认选中 d1（构造函数自动选中首台）
        Assert.Equal("d1", vm.SelectedDeviceId);

        string? requestedDeviceId = null;
        vm.ViewDeviceDetailRequested += id => requestedDeviceId = id;

        // 切到 d2 后触发查看详情
        vm.SelectedDeviceId = "d2";
        vm.ViewDeviceDetailCommand.Execute(null);

        // 事件应携带 d2，并已写回共享服务
        Assert.Equal("d2", requestedDeviceId);
        Assert.Equal("d2", _selection.SelectedDeviceId);
    }

    [Fact]
    public void ViewDeviceDetailCommand_NoSelectedDevice_CannotExecute()
    {
        // 不添加任何设备，SelectedDeviceId 保持 null
        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);
        Assert.Null(vm.SelectedDeviceId);
        Assert.False(vm.ViewDeviceDetailCommand.CanExecute(null));
    }

    [Fact]
    public void ViewDeviceDetailCommand_AfterSelectingDevice_CanExecute()
    {
        AddDeviceWithRuntime("d1", "设备1");
        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);

        // 选中设备后命令应可执行
        Assert.True(vm.ViewDeviceDetailCommand.CanExecute(null));
    }

    [Fact]
    public void ViewDeviceDetailCommand_NoSubscribers_DoesNotThrow()
    {
        AddDeviceWithRuntime("d1", "设备1");
        using var vm = new HomeViewModel(_deviceRepository, _connectionManager, _appSettings, null!, _selection);

        // 无订阅者时调用不应抛异常
        var ex = Record.Exception(() => vm.ViewDeviceDetailCommand.Execute(null));
        Assert.Null(ex);
    }

    // ──────────── 辅助 ────────────

    private static Device CreateDevice(string id, string name, int targetCycle = 100)
    {
        return new Device
        {
            Id = id,
            Name = name,
            TargetCycle = targetCycle,
        };
    }

    private Device AddDeviceWithRuntime(string id, string name, int targetCycle = 100)
    {
        var device = CreateDevice(id, name, targetCycle);
        _deviceRepository.Devices.Add(device);
        _deviceRepository.AddRuntime(device); // 同时维护 Runtimes + RuntimeMap
        return device;
    }

    // ═══════════════ 上班次历史回填（FindLastOtherShiftLog） ═══════════════

    private static ProductionLog Log(string shift, DateTime ts, int ok, int ng = 0)
        => new()
        {
            DeviceId = "d1",
            DeviceName = "设备",
            ShiftName = shift,
            OkProduction = ok,
            NgProduction = ng,
            Timestamp = ts,
        };

    /// <summary>
    /// 回归测试（2026-08-11 上班次空白修复）：内存无缓存时应取"时间上最近的、
    /// 班次不同于当前班次"的最后一条快照——即使当前班次也有更新记录。
    /// </summary>
    [Fact]
    public void FindLastOtherShiftLog_PicksLatestDifferentShift()
    {
        var baseTime = new DateTime(2026, 8, 11, 16, 0, 0);
        var logs = new[]
        {
            Log("白班", baseTime.AddMinutes(-5), 999),          // 当前班次（应被排除）
            Log("夜班", baseTime.AddHours(-1), 88),            // 更近的夜班
            Log("白班", baseTime.AddHours(-2), 500),
            Log("夜班", baseTime.AddHours(-3), 50),
        };

        var last = LastShiftComparisonProvider.FindLastOtherShiftLog(logs, "白班");

        Assert.NotNull(last);
        Assert.Equal("夜班", last!.ShiftName);
        Assert.Equal(88, last.OkProduction);
    }

    [Fact]
    public void FindLastOtherShiftLog_AllSameShift_ReturnsNull()
    {
        var logs = new[]
        {
            Log("白班", new DateTime(2026, 8, 11, 10, 0, 0), 100),
            Log("白班", new DateTime(2026, 8, 11, 9, 0, 0), 50),
        };

        Assert.Null(LastShiftComparisonProvider.FindLastOtherShiftLog(logs, "白班"));
    }

    [Fact]
    public void FindLastOtherShiftLog_NoCurrentShiftName_FallsBackToLatest()
    {
        var logs = new[]
        {
            Log("白班", new DateTime(2026, 8, 11, 10, 0, 0), 100),
            Log("夜班", new DateTime(2026, 8, 11, 2, 0, 0), 77),
        };

        var last = LastShiftComparisonProvider.FindLastOtherShiftLog(logs, null);

        Assert.NotNull(last);
        Assert.Equal("白班", last!.ShiftName);
    }

    // ═══════════════ 距目标差距（FormatQualityGap，2026-08-11） ═══════════════

    [Theory]
    [InlineData(0.956, "+0.6%")]   // 超目标
    [InlineData(0.945, "-0.5%")]   // 还差
    [InlineData(0.95, "+0.0%")]    // 恰好达标
    [InlineData(1.0, "+5.0%")]
    public void FormatQualityGap_FormatsCorrectly(double qualityRate, string expected)
    {
        Assert.Equal(expected, HomeViewModel.FormatQualityGap(qualityRate));
    }
}
