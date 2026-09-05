using System.IO;
using System.Windows;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
using MainAPP.Resources;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class ProductionLineViewModelTests
{
    [Fact]
    public void HasNoDevices_WhenRepositoryEmpty_IsTrue_HasNoFilteredDevices_IsFalse()
    {
        var repo = new DeviceRepository(new AppSettings());
        using var vm = new ProductionLineViewModel(repo, new DeviceSelectionService());

        Assert.True(vm.HasNoDevices);
        Assert.False(vm.HasNoFilteredDevices);
        Assert.Empty(vm.FilteredLineDevices);
        Assert.False(vm.ApplyBatchTargetCycleCommand.CanExecute(null));
    }

    [Fact]
    public void HasNoFilteredDevices_WhenDevicesExistAndFilterIsAll_IsFalse()
    {
        using var vm = CreateViewModelWithDevices(
            (DeviceStatus.Running, "注塑机A"),
            (DeviceStatus.Alarm, "焊接机B"));

        Assert.False(vm.HasNoDevices);
        Assert.False(vm.HasNoFilteredDevices);
        Assert.Equal(2, vm.FilteredLineDevices.Cast<LineDeviceItem>().Count());
    }

    [Fact]
    public void SetStatusFilterCommand_WhenNoMatch_HasNoFilteredDevicesWithoutHasNoDevices()
    {
        using var vm = CreateViewModelWithDevices((DeviceStatus.Running, "注塑机A"));

        vm.SetStatusFilterCommand.Execute(LineStatusFilter.Alarm);

        Assert.False(vm.HasNoDevices);
        Assert.True(vm.HasNoFilteredDevices);
        Assert.Empty(vm.FilteredLineDevices);
    }

    [Fact]
    public void SetStatusFilterCommand_AfterReset_RestoresFilteredDevices()
    {
        using var vm = CreateViewModelWithDevices((DeviceStatus.Running, "注塑机A"));

        vm.SetStatusFilterCommand.Execute(LineStatusFilter.Alarm);
        Assert.True(vm.HasNoFilteredDevices);

        vm.SetStatusFilterCommand.Execute(LineStatusFilter.All);

        Assert.False(vm.HasNoFilteredDevices);
        Assert.Single(vm.FilteredLineDevices);
    }

    [Fact]
    public void LineSearchKeyword_WhenNoMatch_HasNoFilteredDevices()
    {
        using var vm = CreateViewModelWithDevices((DeviceStatus.Running, "注塑机A"));

        vm.LineSearchKeyword = "不存在";

        Assert.False(vm.HasNoDevices);
        Assert.True(vm.HasNoFilteredDevices);
        Assert.Empty(vm.FilteredLineDevices);
    }

    [Fact]
    public void LineSearchKeyword_CaseInsensitive_FiltersByDeviceName()
    {
        using var vm = CreateViewModelWithDevices((DeviceStatus.Running, "Injection-A"));

        vm.LineSearchKeyword = "injection";

        Assert.False(vm.HasNoFilteredDevices);
        Assert.Single(vm.FilteredLineDevices);
    }

    [Fact]
    public void SetStatusFilterCommand_Offline_OnlyShowsOfflineDevices()
    {
        using var vm = CreateViewModelWithDevices(
            (DeviceStatus.Running, "运行中"),
            (DeviceStatus.Offline, "离线机"));

        vm.SetStatusFilterCommand.Execute(LineStatusFilter.Offline);

        Assert.False(vm.HasNoFilteredDevices);
        var item = Assert.Single(vm.FilteredLineDevices.Cast<LineDeviceItem>());
        Assert.Equal("离线机", item.Device.Name);
    }

    [Fact]
    public void SummaryKpis_AggregateStatusCountsAndProduction()
    {
        var repo = new DeviceRepository(new AppSettings());
        var running = new Device { Id = Guid.NewGuid().ToString("N"), Name = "运行" };
        var alarm = new Device { Id = Guid.NewGuid().ToString("N"), Name = "报警" };
        repo.Devices.Add(running);
        repo.Devices.Add(alarm);
        var runningRt = new DeviceRuntime(running) { StatusWord = (int)DeviceStatus.Running, TotalOkProduction = 80, TotalNgProduction = 20 };
        var alarmRt = new DeviceRuntime(alarm) { StatusWord = (int)DeviceStatus.Alarm, TotalOkProduction = 30, TotalNgProduction = 10 };
        repo.Runtimes.Add(runningRt);
        repo.Runtimes.Add(alarmRt);

        using var vm = new ProductionLineViewModel(repo, new DeviceSelectionService());

        Assert.Equal(1, vm.RunningCount);
        Assert.Equal(1, vm.AlarmCount);
        Assert.Equal(0, vm.PausedCount);
        Assert.Equal(0, vm.OfflineCount);
        Assert.Equal(110, vm.TotalOkProduction);
        Assert.Equal(30, vm.TotalNgProduction);
        Assert.Equal(140, vm.TotalOutput);
    }

    [Fact]
    public void BatchTargetPcsPerHour_WhenAllDevicesShareCycle_SeedsThatValue()
    {
        using var vm = CreateViewModelWithTargetCycles(40, 40);

        Assert.Equal(40, vm.BatchTargetPcsPerHour);
        Assert.True(vm.ApplyBatchTargetCycleCommand.CanExecute(null));
    }

    [Fact]
    public void BatchTargetPcsPerHour_WhenDevicesDiffer_StaysZeroAndCannotApply()
    {
        using var vm = CreateViewModelWithTargetCycles(100, 200);

        Assert.Equal(0, vm.BatchTargetPcsPerHour);
        Assert.False(vm.ApplyBatchTargetCycleCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApplyBatchTargetCycle_WritesAllDevicesRuntimesAndPersists()
    {
        var (dir, settings, repo) = CreateIsolatedRepo();
        try
        {
            var a = new Device { Id = "a", Name = "A", TargetCycle = 100 };
            var b = new Device { Id = "b", Name = "B", TargetCycle = 200 };
            repo.Devices.Add(a);
            repo.Devices.Add(b);
            repo.AddRuntime(a);
            repo.AddRuntime(b);

            var dialog = new FakeDialogService { ShowResult = MessageBoxResult.Yes };
            using var vm = new ProductionLineViewModel(repo, new DeviceSelectionService(), appSettings: settings, dialog: dialog);
            vm.BatchTargetPcsPerHour = 40;

            await vm.ApplyBatchTargetCycleCommand.ExecuteAsync(null);

            Assert.Equal(40, a.TargetCycle);
            Assert.Equal(40, b.TargetCycle);
            Assert.Equal(40, repo.RuntimeMap["a"].TargetCycle);
            Assert.Equal(40, repo.RuntimeMap["b"].TargetCycle);
            Assert.Equal(40, vm.LineDevices[0].Runtime.TargetCycle);
            Assert.Equal(40, vm.LineDevices[1].Runtime.TargetCycle);
            Assert.Single(dialog.ShowCalls);
            Assert.Equal(string.Format(Strings.Ln_ApplyTargetCycleDone, 2, 40), Assert.Single(dialog.Success));

            var verify = new DeviceRepository(settings);
            verify.LoadAll();
            Assert.Equal(2, verify.Devices.Count);
            Assert.All(verify.Devices, device => Assert.Equal(40, device.TargetCycle));
        }
        finally
        {
            CleanupIsolated(dir);
        }
    }

    [Fact]
    public async Task ApplyBatchTargetCycle_WhenConfirmCancelled_LeavesValuesUnchanged()
    {
        var (dir, settings, repo) = CreateIsolatedRepo();
        try
        {
            var a = new Device { Id = "a", Name = "A", TargetCycle = 100 };
            var b = new Device { Id = "b", Name = "B", TargetCycle = 200 };
            repo.Devices.Add(a);
            repo.Devices.Add(b);
            repo.AddRuntime(a);
            repo.AddRuntime(b);

            var dialog = new FakeDialogService { ShowResult = MessageBoxResult.No };
            using var vm = new ProductionLineViewModel(repo, new DeviceSelectionService(), appSettings: settings, dialog: dialog);
            vm.BatchTargetPcsPerHour = 40;

            await vm.ApplyBatchTargetCycleCommand.ExecuteAsync(null);

            Assert.Equal(100, a.TargetCycle);
            Assert.Equal(200, b.TargetCycle);
            Assert.Equal(100, repo.RuntimeMap["a"].TargetCycle);
            Assert.Equal(200, repo.RuntimeMap["b"].TargetCycle);
            Assert.Single(dialog.ShowCalls);
            Assert.Empty(dialog.Success);
            Assert.False(File.Exists(repo.FilePath));
        }
        finally
        {
            CleanupIsolated(dir);
        }
    }

    private static ProductionLineViewModel CreateViewModelWithDevices(
        params (DeviceStatus Status, string Name)[] devices)
    {
        var repo = new DeviceRepository(new AppSettings());
        foreach (var (status, name) in devices)
        {
            var device = new Device { Id = Guid.NewGuid().ToString("N"), Name = name };
            repo.Devices.Add(device);
            var runtime = new DeviceRuntime(device) { StatusWord = (int)status };
            repo.Runtimes.Add(runtime);
        }

        return new ProductionLineViewModel(repo, new DeviceSelectionService());
    }

    private static ProductionLineViewModel CreateViewModelWithTargetCycles(params int[] cycles)
    {
        var repo = new DeviceRepository(new AppSettings());
        for (var i = 0; i < cycles.Length; i++)
        {
            var device = new Device { Id = Guid.NewGuid().ToString("N"), Name = $"D{i}", TargetCycle = cycles[i] };
            repo.Devices.Add(device);
            repo.AddRuntime(device);
        }

        return new ProductionLineViewModel(repo, new DeviceSelectionService());
    }

    private static (string Dir, AppSettings Settings, DeviceRepository Repo) CreateIsolatedRepo()
    {
        var name = "pl-batch-" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = name };
        return (Path.Combine(AppSettings.DataRoot, name), settings, new DeviceRepository(settings));
    }

    private static void CleanupIsolated(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 测试临时目录清理失败不影响断言
        }
    }
}
