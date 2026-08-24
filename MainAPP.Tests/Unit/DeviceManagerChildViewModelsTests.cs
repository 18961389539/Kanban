using System.ComponentModel;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using MainAPP.Tests.Integration;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 设备管理器三个子 Tab VM 测试（审查修复 2026-08-13 补 0 覆盖盲区）：
/// DeviceAlarmManager / DeviceDefectManager / DeviceCounterAlarmManager——
/// 覆盖选中设备同步、增删命令、CanExecute 依赖、Detach 解绑与 CSV 导出取消路径。
/// 加入 WpfUi 集合：子 VM 的宿主同步在 Application.Current 存在时经 Application.Dispatcher
/// 异步封送（DeviceAlarmManagerViewModel），与 WpfUi 集合串行可保证 STA 消息循环空闲即执行，
/// 消除全量并行下"回调排队被渲染测试占用"的时序脆弱（2026-08-14 实测修复）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
[Collection("WpfUi")]
public class DeviceManagerChildViewModelsTests
{
    private static IDeviceManagerHost CreateHost(Device? selected, bool isLoading = false)
    {
        var host = Substitute.For<IDeviceManagerHost>();
        host.SelectedDevice.Returns(selected);
        host.IsLoading.Returns(isLoading);
        host.IsPlcConnected.Returns(true);
        host.CanManageDevices.Returns(true);
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
        // 测试线程无消息循环，需显式 pump 队列（Invoke 同步执行会先处理已排队的 Normal 项），
        // 否则回调永不执行（审查修复 2026-08-13 的 5s 轮询不足以覆盖并行负载变化）。
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!ReferenceEquals(vm.SelectedDevice, device) && DateTime.UtcNow < deadline)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background,
                TestContext.Current.CancellationToken);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
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

    // ──────────── DeviceCounterAlarmManagerViewModel ────────────

    [Fact]
    public void CounterAlarmManager_AddAndRemoveCounterAlarm()
    {
        var device = new Device { Id = "dev-1", Name = "设备1" };
        var host = CreateHost(device);
        var plcCommands = new DevicePlcCommandHandler(
            new MainAPP.Tests.Unit.FakePlcDriver(),
            new PlcConnectionManager(new MainAPP.Tests.Unit.FakePlcDriver(), new AppSettings()),
            null!);
        var vm = new DeviceCounterAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            plcCommands,
            new CounterAlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            host);
        vm.SelectedDevice = device;

        vm.AddCounterAlarmCommand.Execute(null);
        Assert.Single(device.CounterAlarms);

        var target = device.CounterAlarms[0];
        vm.RemoveCounterAlarmCommand.Execute(target);
        Assert.Empty(device.CounterAlarms);
        host.Received(2).MarkDirty();
    }

    [Fact]
    public void CounterAlarmManager_NoDevice_CommandsDisabled()
    {
        var vm = new DeviceCounterAlarmManagerViewModel(
            new MainAPP.Tests.Unit.FakeDialogService(),
            new DevicePlcCommandHandler(
                new MainAPP.Tests.Unit.FakePlcDriver(),
                new PlcConnectionManager(new MainAPP.Tests.Unit.FakePlcDriver(), new AppSettings()),
                null!),
            new CounterAlarmCsvIOService(new MainAPP.Tests.Unit.FakeDialogService()),
            CreateHost(null));

        Assert.False(vm.AddCounterAlarmCommand.CanExecute(null));
        Assert.False(vm.RemoveCounterAlarmCommand.CanExecute(null));
    }

    [Fact]
    public async Task PlcCommandManager_Operator_CannotExecuteCommandsDirectly()
    {
        var device = new Device
        {
            Id = "dev-1",
            Name = "设备1",
            RecipeAddress = "D100",
            RecipeValue = 42,
            ProductionResetAddress = "D106",
        };
        var driver = new FakePlcDriver();
        var handler = new DevicePlcCommandHandler(
            driver,
            new PlcConnectionManager(driver, new AppSettings()),
            Substitute.For<IPlcDataAcquisitionService>(),
            null);
        var dialog = new FakeDialogService();
        var host = CreateHost(device);
        host.CanManageDevices.Returns(false);
        var vm = new DevicePlcCommandViewModel(dialog, host, handler)
        {
            SelectedDevice = device,
        };

        Assert.False(vm.WriteRecipeCommand.CanExecute(null));
        Assert.False(vm.ResetProductionCommand.CanExecute(null));
        Assert.False(vm.ReadPlcValueCommand.CanExecute("D100"));

        await vm.WriteRecipeCommand.ExecuteAsync(null);
        await vm.ResetProductionCommand.ExecuteAsync(null);
        await vm.ReadPlcValueCommand.ExecuteAsync("D100");

        Assert.Empty(driver.WriteHistory);
        Assert.Equal(0, driver.ReadInt32CallCount);
        Assert.Empty(dialog.ShowCalls);
    }

    [Fact]
    public async Task CounterAlarmManager_Operator_CannotResetCounterAlarmDirectly()
    {
        var device = new Device { Id = "dev-1", Name = "设备1" };
        var alarm = new CounterAlarm { DeviceId = device.Id, Name = "计数报警", PlcAddress = "D200" };
        device.CounterAlarms.Add(alarm);
        var driver = new FakePlcDriver();
        var handler = new DevicePlcCommandHandler(
            driver,
            new PlcConnectionManager(driver, new AppSettings()),
            Substitute.For<IPlcDataAcquisitionService>(),
            null);
        var dialog = new FakeDialogService();
        var host = CreateHost(device);
        host.CanManageDevices.Returns(false);
        var vm = new DeviceCounterAlarmManagerViewModel(
            dialog,
            handler,
            new CounterAlarmCsvIOService(dialog),
            host)
        {
            SelectedDevice = device,
        };

        Assert.False(vm.ResetCounterAlarmValueCommand.CanExecute(alarm));
        await vm.ResetCounterAlarmValueCommand.ExecuteAsync(alarm);

        Assert.Empty(driver.WriteHistory);
        Assert.Empty(dialog.ShowCalls);
    }
}
