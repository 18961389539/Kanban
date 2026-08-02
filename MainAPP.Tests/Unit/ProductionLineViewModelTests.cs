using System.Collections.Generic;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 产线页 ViewModel 纯单元测试（不依赖真实 PLC / 数据库 / UI）。
/// 覆盖：布局自适应阈值、状态计数、产量汇总与合格率、加权 OEE、
/// 聚焦设备命令、选中态镜像、运行时属性变更刷新、设备动态增减同步、
/// 以及 vs 上班次产量差（含/不含 PLC 服务两种情形）。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ProductionLineViewModelTests
{
    private static (DeviceRepository repo, IDeviceSelectionService sel) BuildRepo(int n)
    {
        var repo = new DeviceRepository(new AppSettings());
        var sel = new DeviceSelectionService();
        for (var i = 0; i < n; i++)
            AddDevice(repo, "D" + i, DeviceStatus.Running, 100, 0, 3600, 0, 100);
        return (repo, sel);
    }

    private static Device AddDevice(DeviceRepository repo, string name, DeviceStatus status,
        int ok, int ng, double run, double alarm, int target)
    {
        var dev = new Device { Name = name };
        dev.TargetCycle = target;
        repo.Devices.Add(dev);
        var rt = new DeviceRuntime(dev)
        {
            StatusWord = (int)status,
            TotalOkProduction = ok,
            TotalNgProduction = ng,
            RunTime = run,
            AlarmTime = alarm,
        };
        repo.Runtimes.Add(rt);
        return dev;
    }

    /// <summary>
    /// 强制同步 LineDevices（Dispatcher 在单元测试环境中不可用时，通过反射调用 private SyncLineDevices）。
    /// 生产环境中 OnDevicesCollectionChanged 通过 Dispatcher.BeginInvoke 异步同步，测试环境无 Dispatcher 故需手动触发。
    /// </summary>
    private static void ForceSyncLineDevices(ProductionLineViewModel vm)
    {
        var method = typeof(ProductionLineViewModel).GetMethod(
            "SyncLineDevices",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method?.Invoke(vm, null);
    }

    // ───────────── 布局自适应阈值 ─────────────

    [Theory]
    [InlineData(1, LineLayoutMode.LargeCards)]
    [InlineData(8, LineLayoutMode.LargeCards)]
    [InlineData(9, LineLayoutMode.MediumCards)]
    [InlineData(15, LineLayoutMode.MediumCards)]
    [InlineData(16, LineLayoutMode.Table)]
    public void LayoutMode_MatchesDeviceCount(int n, LineLayoutMode expected)
    {
        var (repo, sel) = BuildRepo(n);
        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(expected, vm.LayoutMode);
    }

    // ───────────── 状态计数 ─────────────

    [Fact]
    public void StatusCounts_AggregateByState()
    {
        var (repo, sel) = BuildRepo(0);
        AddDevice(repo, "r", DeviceStatus.Running, 0, 0, 0, 0, 0);
        AddDevice(repo, "a", DeviceStatus.Alarm, 0, 0, 0, 0, 0);
        AddDevice(repo, "p", DeviceStatus.Paused, 0, 0, 0, 0, 0);
        AddDevice(repo, "i", DeviceStatus.Unknown, 0, 0, 0, 0, 0);

        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(1, vm.RunningCount);
        Assert.Equal(1, vm.AlarmCount);
        Assert.Equal(1, vm.PausedCount);
        Assert.Equal(1, vm.IdleCount);
    }

    // ───────────── 产量汇总与合格率 ─────────────

    [Fact]
    public void TotalsAndQualityRate_AggregateAcrossDevices()
    {
        var (repo, sel) = BuildRepo(0);
        AddDevice(repo, "A", DeviceStatus.Running, 80, 20, 0, 0, 0); // 合格率 0.8
        AddDevice(repo, "B", DeviceStatus.Running, 100, 0, 0, 0, 0); // 合格率 1.0

        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(180, vm.TotalOkProduction);
        Assert.Equal(20, vm.TotalNgProduction);
        Assert.Equal(200, vm.TotalOutput);
        Assert.Equal(0.9, vm.OverallQualityRate, 6); // 180 / 200
    }

    [Fact]
    public void WeightedOee_WeightsByOutput()
    {
        // A: OEE=1.0 (ok=100,ng=0,run=3600,alarm=0,tc=100) 权重=100
        // B: OEE=0.5 (ok=50, ng=0,run=3600,alarm=0,tc=100) 权重=50
        // 加权 = (1.0*100 + 0.5*50) / 150 = 125/150 ≈ 0.8333
        var (repo, sel) = BuildRepo(0);
        AddDevice(repo, "A", DeviceStatus.Running, 100, 0, 3600, 0, 100);
        AddDevice(repo, "B", DeviceStatus.Running, 50, 0, 3600, 0, 100);

        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(0.8333, vm.WeightedOee, 4);
    }

    // ───────────── 聚焦设备命令 ─────────────

    [Fact]
    public void FocusDevice_RaisesEventAndUpdatesSelection()
    {
        var (repo, sel) = BuildRepo(1);
        var dev = repo.Devices[0];
        string? raised = null;

        var vm = new ProductionLineViewModel(repo, sel);
        vm.FocusDeviceRequested += id => raised = id;

        vm.FocusDeviceCommand.Execute(dev.Id);

        Assert.Equal(dev.Id, raised);
        Assert.Equal(dev.Id, sel.SelectedDeviceId);
        Assert.Equal(dev.Id, vm.SelectedDeviceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FocusDevice_InvalidParameter_DoesNotRaiseEvent(object? param)
    {
        var (repo, sel) = BuildRepo(1);
        var vm = new ProductionLineViewModel(repo, sel);
        var raised = false;
        vm.FocusDeviceRequested += _ => raised = true;

        vm.FocusDeviceCommand.Execute(param);

        Assert.False(raised);
    }

    // ───────────── 选中态镜像 ─────────────

    [Fact]
    public void SelectedDeviceId_MirrorsSharedSelectionService()
    {
        var (repo, sel) = BuildRepo(2);
        var vm = new ProductionLineViewModel(repo, sel);

        sel.SelectedDeviceId = repo.Devices[1].Id;

        Assert.Equal(repo.Devices[1].Id, vm.SelectedDeviceId);
    }

    // ───────────── 运行时属性变更 → 汇总刷新 ─────────────

    [Fact]
    public void RuntimePropertyChanged_RefreshesSummaryKpis()
    {
        var (repo, sel) = BuildRepo(1);
        var rt = repo.Runtimes[0];
        var vm = new ProductionLineViewModel(repo, sel);

        rt.TotalOkProduction = 999;

        Assert.Equal(999, vm.TotalOkProduction);
    }

    // ───────────── 设备动态增减同步 ─────────────

    [Fact]
    public void AddDevice_DynamicallyGrowsLineDevicesAndSwitchesLayout()
    {
        var (repo, sel) = BuildRepo(8); // 1-8 → 大卡片
        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(8, vm.LineDevices.Count);
        Assert.True(vm.IsLargeCardsLayout);

        AddDevice(repo, "new", DeviceStatus.Running, 0, 0, 0, 0, 0); // 第 9 台 → 中卡片
        ForceSyncLineDevices(vm);

        Assert.Equal(9, vm.LineDevices.Count);
        Assert.True(vm.IsMediumCardsLayout);
    }

    // ───────────── 0 / 1 / 100 台布局切换 ─────────────
    // 阈值（按实际代码）：≤8 → LargeCards；9-15 → MediumCards；16+ → Table
    // 重点验证：
    // - 0 台设备走 LargeCards + HasNoDevices=true（EmptyState 覆盖）
    // - 1 台设备走 LargeCards + HasNoDevices=false
    // - 15 → 16 切到 Table（与 8 → 9 同样重要）
    // - 反向切换：16 → 15 切回 Medium；9 → 8 切回 Large
    // - 100 台设备走 Table

    [Fact]
    public void LayoutMode_ZeroDevices_SelectsLargeCardsWithEmptyState()
    {
        var (repo, sel) = BuildRepo(0);
        var vm = new ProductionLineViewModel(repo, sel);

        Assert.Equal(LineLayoutMode.LargeCards, vm.LayoutMode);
        Assert.True(vm.HasNoDevices);
        Assert.True(vm.IsLargeCardsLayout);
        Assert.False(vm.IsMediumCardsLayout);
        Assert.False(vm.IsTableLayout);
        Assert.Empty(vm.LineDevices);
    }

    [Fact]
    public void LayoutMode_OneDevice_SelectsLargeCardsWithoutEmptyState()
    {
        var (repo, sel) = BuildRepo(1);
        var vm = new ProductionLineViewModel(repo, sel);

        Assert.Equal(LineLayoutMode.LargeCards, vm.LayoutMode);
        Assert.False(vm.HasNoDevices);
        Assert.True(vm.IsLargeCardsLayout);
        Assert.Single(vm.LineDevices);
    }

    [Fact]
    public void AddDevice_15To16_SwitchesFromMediumToTable()
    {
        var (repo, sel) = BuildRepo(15);
        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(LineLayoutMode.MediumCards, vm.LayoutMode);
        Assert.True(vm.IsMediumCardsLayout);

        AddDevice(repo, "extra", DeviceStatus.Running, 0, 0, 0, 0, 0); // 第 16 台
        ForceSyncLineDevices(vm);

        Assert.Equal(16, vm.LineDevices.Count);
        Assert.Equal(LineLayoutMode.Table, vm.LayoutMode);
        Assert.True(vm.IsTableLayout);
        Assert.False(vm.IsMediumCardsLayout);
    }

    [Fact]
    public void RemoveDevice_16To15_SwitchesFromTableBackToMedium()
    {
        var (repo, sel) = BuildRepo(16);
        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(LineLayoutMode.Table, vm.LayoutMode);

        var dev = repo.Devices[0];
        repo.Devices.Remove(dev);
        ForceSyncLineDevices(vm);

        Assert.Equal(15, vm.LineDevices.Count);
        Assert.Equal(LineLayoutMode.MediumCards, vm.LayoutMode);
        Assert.True(vm.IsMediumCardsLayout);
        Assert.False(vm.IsTableLayout);
    }

    [Fact]
    public void RemoveDevice_9To8_SwitchesFromMediumBackToLarge()
    {
        var (repo, sel) = BuildRepo(9);
        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(LineLayoutMode.MediumCards, vm.LayoutMode);

        var dev = repo.Devices[0];
        repo.Devices.Remove(dev);
        ForceSyncLineDevices(vm);

        Assert.Equal(8, vm.LineDevices.Count);
        Assert.Equal(LineLayoutMode.LargeCards, vm.LayoutMode);
        Assert.True(vm.IsLargeCardsLayout);
        Assert.False(vm.IsMediumCardsLayout);
    }

    [Fact]
    public void LayoutMode_100Devices_SelectsTable()
    {
        var (repo, sel) = BuildRepo(100);
        var vm = new ProductionLineViewModel(repo, sel);

        Assert.Equal(LineLayoutMode.Table, vm.LayoutMode);
        Assert.True(vm.IsTableLayout);
        Assert.False(vm.IsLargeCardsLayout);
        Assert.False(vm.IsMediumCardsLayout);
        Assert.Equal(100, vm.LineDevices.Count);
        Assert.False(vm.HasNoDevices);
    }

    [Fact]
    public void LayoutMode_ThresholdBoundaries_ExactBoundaryValues()
    {
        // 阈值边界：8（含）→ Large；9 → Medium；15（含）→ Medium；16 → Table
        var (repo8, sel8) = BuildRepo(8);
        Assert.Equal(LineLayoutMode.LargeCards, new ProductionLineViewModel(repo8, sel8).LayoutMode);

        var (repo9, sel9) = BuildRepo(9);
        Assert.Equal(LineLayoutMode.MediumCards, new ProductionLineViewModel(repo9, sel9).LayoutMode);

        var (repo15, sel15) = BuildRepo(15);
        Assert.Equal(LineLayoutMode.MediumCards, new ProductionLineViewModel(repo15, sel15).LayoutMode);

        var (repo16, sel16) = BuildRepo(16);
        Assert.Equal(LineLayoutMode.Table, new ProductionLineViewModel(repo16, sel16).LayoutMode);
    }

    [Fact]
    public void RemoveDevice_DynamicallyShrinksLineDevices()
    {
        var (repo, sel) = BuildRepo(2);
        var vm = new ProductionLineViewModel(repo, sel);
        Assert.Equal(2, vm.LineDevices.Count);

        var dev = repo.Devices[0];
        repo.Devices.Remove(dev);
        ForceSyncLineDevices(vm);

        Assert.Single(vm.LineDevices);
        Assert.DoesNotContain(vm.LineDevices, x => x.Device.Id == dev.Id);
    }

    // ───────────── vs 上班次产量差 ─────────────

    [Fact]
    public void HasLastShift_False_WhenNoPlcService()
    {
        var (repo, sel) = BuildRepo(1);
        var vm = new ProductionLineViewModel(repo, sel); // plcService 默认 null

        Assert.False(vm.HasLastShift);
        Assert.Equal(0, vm.OutputDiff);
        Assert.Equal(0, vm.NgDiff);
    }

    [Fact]
    public void OutputDiff_ComparesAgainstLastShiftSummary()
    {
        var (repo, sel) = BuildRepo(0);
        var dev = AddDevice(repo, "A", DeviceStatus.Running, 120, 30, 0, 0, 100); // 本班总产 150

        var driver = new FakePlcDriver();
        var conn = new PlcConnectionManager(driver, new AppSettings());
        var plc = new PlcDataAcquisitionService(
            driver, conn, new AppSettings(), new InMemoryHistoryService(),
            repo, new ProductionBaselineStore(new AppSettings()),
            new NullLogger<PlcDataAcquisitionService>());

        // 注入上班次快照：(OK=80, NG=20, 白班)
        plc.SetLastShiftSummaryForTest(dev.Id, 80, 20, "白班");

        var vm = new ProductionLineViewModel(repo, sel, plc);

        Assert.True(vm.HasLastShift);
        Assert.Equal(50, vm.OutputDiff); // 150 - (80+20)
        Assert.Equal(10, vm.NgDiff);     // 30 - 20
    }

    /// <summary>ILogger&lt;T&gt; 空实现，避免对 Microsoft.Extensions.Logging.Abstractions 程序集的静态类型依赖。</summary>
    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
