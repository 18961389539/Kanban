using System.ComponentModel;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 设备管理器三个子 Tab VM 测试（审查修复 2026-08-13 补 0 覆盖盲区）：
/// DeviceAlarmManager / DeviceDefectManager / DeviceCountAlarmManager——
/// 覆盖选中设备同步、增删命令、CanExecute 依赖、Detach 解绑与 CSV 导出取消路径。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class DeviceManagerChildViewModelsTests
{
    private static IDeviceManagerHost CreateHost(Device? selected, bool isLoading = false)
    {
        var host = Substitute.For<IDeviceManagerHost>();
        host.SelectedDevice.Returns(selected);
        host.IsLoading.Returns(isLoading);
        host.IsPlcConnected.Returns(true);
        return host;
    }

    private static Device CreateDeviceWithAlarm()
    {
        var device = new Device { Id = "dev-1", Name = "设备1" };
        device.Alarms.Add(new Alarm { Name = "高温报警", DeviceId = "dev-1", PlcAddress = "M0" });
        return device;
    }

    // ──────────── DeviceAlarmManagerViewModel ────────────

    [Fact]
    public void AlarmManager_AddAlarm_AppendsToDevice_AndMarksDirty()
    {
        var device = CreateDeviceWithAlarm();
        var host = CreateHost(device);
        var vm = new DeviceAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new AlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            Substitute.For<IPlcDataAcquisitionService>(),
            host);
        vm.SelectedDevice = device;

        var before = device.Alarms.Count;
        vm.AddAlarmCommand.Execute(null);

        Assert.Equal(before + 1, device.Alarms.Count);
        host.Received(1).MarkDirty();
    }

    [Fact]
    public void AlarmManager_RemoveAlarm_RemovesAndClearsAcquisitionState()
    {
        var device = CreateDeviceWithAlarm();
        var target = device.Alarms[0];
        var host = CreateHost(device);
        var acq = Substitute.For<IPlcDataAcquisitionService>();
        var vm = new DeviceAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new AlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            acq, host);
        vm.SelectedDevice = device;
        vm.SelectedAlarm = target;

        vm.RemoveAlarmCommand.Execute(target);

        Assert.DoesNotContain(device.Alarms, a => a.Id == target.Id);
        acq.Received(1).RemoveAlarmState(target.Id);
        Assert.Null(vm.SelectedAlarm);
    }

    [Fact]
    public async Task AlarmManager_HostSelectedDeviceChange_SyncsSelectedDevice()
    {
        var device = CreateDeviceWithAlarm();
        var host = CreateHost(null);
        var vm = new DeviceAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new AlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            Substitute.For<IPlcDataAcquisitionService>(),
            host);
        Assert.Null(vm.SelectedDevice);

        host.SelectedDevice.Returns(device);
        host.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            host, new PropertyChangedEventArgs(nameof(IDeviceManagerHost.SelectedDevice)));

        // Application.Current 存在时（WpfUi collection 并行运行）回调经 Dispatcher.InvokeAsync 异步封送，
        // 轮询等待同步完成（审查修复 2026-08-13：全量并行跑时的时序修正）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ReferenceEquals(vm.SelectedDevice, device) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Same(device, vm.SelectedDevice);
    }

    [Fact]
    public void AlarmManager_Detach_StopsHostSync()
    {
        var host = CreateHost(null);
        var vm = new DeviceAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new AlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            Substitute.For<IPlcDataAcquisitionService>(),
            host);
        vm.Detach();

        host.SelectedDevice.Returns(CreateDeviceWithAlarm());
        host.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            host, new PropertyChangedEventArgs(nameof(IDeviceManagerHost.SelectedDevice)));

        Assert.Null(vm.SelectedDevice); // 已解绑，不再同步
    }

    [Fact]
    public void AlarmManager_NoSelectedDevice_CommandsDisabled()
    {
        var vm = new DeviceAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new AlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            Substitute.For<IPlcDataAcquisitionService>(),
            CreateHost(null));

        Assert.False(vm.AddAlarmCommand.CanExecute(null));
        Assert.False(vm.RemoveAlarmCommand.CanExecute(null));
    }

    // ──────────── DeviceDefectManagerViewModel ────────────

    [Fact]
    public void DefectManager_AddAndRemoveDefect()
    {
        var device = new Device { Id = "dev-1", Name = "设备1" };
        device.Defects.Add(new Defect { Name = "毛边", DeviceId = "dev-1", PlcAddress = "D0" });
        var host = CreateHost(device);
        var vm = new DeviceDefectManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new DefectCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            host);
        vm.SelectedDevice = device;

        vm.AddDefectCommand.Execute(null);
        Assert.Equal(2, device.Defects.Count);

        var target = device.Defects[0];
        vm.RemoveDefectCommand.Execute(target);
        Assert.DoesNotContain(device.Defects, d => d.Id == target.Id);
        host.Received(2).MarkDirty();
    }

    [Fact]
    public void DefectManager_Detach_StopsHostSync()
    {
        var host = CreateHost(null);
        var vm = new DeviceDefectManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new DefectCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            host);
        vm.Detach();

        host.SelectedDevice.Returns(new Device { Id = "dev-9", Name = "设备9" });
        host.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            host, new PropertyChangedEventArgs(nameof(IDeviceManagerHost.SelectedDevice)));

        Assert.Null(vm.SelectedDevice);
    }

    // ──────────── DeviceCountAlarmManagerViewModel ────────────

    [Fact]
    public void CountAlarmManager_AddAndRemoveCountAlarm()
    {
        var device = new Device { Id = "dev-1", Name = "设备1" };
        var host = CreateHost(device);
        var plcCommands = new DevicePlcCommandHandler(
            new MainAPP.Tests.Unit.FakePlcDriver(),
            new PlcConnectionManager(new MainAPP.Tests.Unit.FakePlcDriver(), new AppSettings()),
            null!);
        var vm = new DeviceCountAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            plcCommands,
            new CountAlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            host);
        vm.SelectedDevice = device;

        vm.AddCountAlarmCommand.Execute(null);
        Assert.Single(device.CountAlarms);

        var target = device.CountAlarms[0];
        vm.RemoveCountAlarmCommand.Execute(target);
        Assert.Empty(device.CountAlarms);
        host.Received(2).MarkDirty();
    }

    [Fact]
    public void CountAlarmManager_NoDevice_CommandsDisabled()
    {
        var vm = new DeviceCountAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new DevicePlcCommandHandler(
                new MainAPP.Tests.Unit.FakePlcDriver(),
                new PlcConnectionManager(new MainAPP.Tests.Unit.FakePlcDriver(), new AppSettings()),
                null!),
            new CountAlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            CreateHost(null));

        Assert.False(vm.AddCountAlarmCommand.CanExecute(null));
        Assert.False(vm.RemoveCountAlarmCommand.CanExecute(null));
    }
}
