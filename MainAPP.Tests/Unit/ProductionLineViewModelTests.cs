using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
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
        Assert.Contains("110", vm.TotalOutputDetailText);
        Assert.Contains("30", vm.TotalOutputDetailText);
        var expectedWeightedOee = (runningRt.Oee * 100 + alarmRt.Oee * 100) / 140;
        Assert.Equal(expectedWeightedOee, vm.WeightedOee, 6);
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
}
