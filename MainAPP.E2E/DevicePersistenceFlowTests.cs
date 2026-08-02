using System.Windows.Threading;
using MainAPP.Data;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// 设备持久化端到端流程：
/// 1. 添加设备到内存 → SaveAll 落盘 → 重新 LoadAll 验证恢复
/// 2. 损坏 devices.json → LoadAll 触发备份错误消息
/// 3. 添加设备后切到主页/产线页验证 UI 不异常
/// 所有修改 ObservableCollection 的操作通过 Run 封送到 STA 线程。
/// </summary>
[Collection("E2E")]
public class DevicePersistenceFlowTests
{
    private readonly TestHost _host;

    public DevicePersistenceFlowTests(TestHost host) => _host = host;

    [Fact]
    public void AddDevice_Save_Reload_Persists()
    {
        _host.ResetState();

        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            repo.Devices.Add(new Device { Name = "注塑机A1", TargetCycle = 600, RecipeName = "R1" });
            repo.Devices.Add(new Device { Name = "焊接机B2", TargetCycle = 400, RecipeName = "R2" });
            repo.SaveAll();
        });

        Assert.True(System.IO.File.Exists(_host.Resolve<DeviceRepository>().FilePath));

        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            repo.Devices.Clear();
            repo.Runtimes.Clear();
            repo.RuntimeMap.Clear();
            repo.LoadAll();
        });

        var r = _host.Resolve<DeviceRepository>();
        Assert.Equal(2, r.Devices.Count);
        Assert.Equal(2, r.Runtimes.Count);
        Assert.Contains(r.Devices, d => d.Name == "注塑机A1");
        Assert.Contains(r.Devices, d => d.Name == "焊接机B2");
    }

    [Fact]
    public void CorruptDevicesFile_LoadAll_SetsErrorMessageAndBacksUp()
    {
        _host.ResetState();

        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            repo.Devices.Add(new Device { Name = "测试设备" });
            repo.SaveAll();
        });

        System.IO.File.WriteAllText(_host.Resolve<DeviceRepository>().FilePath, "{ this is not valid json }");

        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            repo.Devices.Clear();
            repo.LoadAll();
        });

        var repo = _host.Resolve<DeviceRepository>();
        Assert.NotNull(repo.LoadErrorMessage);
        Assert.Contains("损坏", repo.LoadErrorMessage);
        Assert.True(System.IO.File.Exists(repo.FilePath + ".corrupt"));
        Assert.Empty(repo.Devices);
    }

    [Fact]
    public void AddDevice_NavigateToHomeAndProduction_NoException()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            repo.Devices.Add(new Device { Name = "产线设备1", TargetCycle = 500 });
            repo.Devices.Add(new Device { Name = "产线设备2", TargetCycle = 450 });
            repo.Devices.Add(new Device { Name = "产线设备3", TargetCycle = 550 });
            foreach (var d in repo.Devices)
                repo.Runtimes.Add(new DeviceRuntime(d));
            _host.Resolve<DeviceManagerViewModel>().RefreshDeviceList();
        });

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();

            vm.SelectedIndex = 0;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();
            Assert.Equal(0, vm.SelectedIndex);

            vm.SelectedIndex = 1;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();
            Assert.Equal(1, vm.SelectedIndex);

            Assert.Equal(3, vm.ProductionLineViewModel.LineDevices.Count);

            window.Hide();
        });
    }

    [Fact]
    public void DeviceRuntime_SyncsWithDevices_OnLoadAll()
    {
        _host.ResetState();

        _host.Run(() =>
        {
            var repo = _host.Resolve<DeviceRepository>();
            repo.Devices.Add(new Device { Name = "测试A", TargetCycle = 500 });
            repo.SaveAll();
            repo.Devices.Clear();
            repo.Runtimes.Clear();
            repo.RuntimeMap.Clear();
            repo.LoadAll();
        });

        var repo = _host.Resolve<DeviceRepository>();
        Assert.Equal(repo.Devices.Count, repo.Runtimes.Count);
        Assert.Equal(repo.Devices.Count, repo.RuntimeMap.Count);
        foreach (var d in repo.Devices)
            Assert.True(repo.RuntimeMap.ContainsKey(d.Id));
    }
}
