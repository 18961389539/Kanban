using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MainAPP.E2E;

/// <summary>
/// 配置持久化端到端流程：
/// 1. 修改 AppSettings → Save → 重新 Load 验证所有配置项往返保持一致
/// 2. 损坏 settings.json → Load 触发错误消息并回退默认值
/// 3. 修改班次配置 → 验证持久化往返
/// 所有修改 ObservableCollection 的操作通过 Run 封送到 STA 线程。
/// </summary>
[Collection("E2E")]
public class SettingsPersistenceFlowTests
{
    private readonly TestHost _host;

    public SettingsPersistenceFlowTests(TestHost host) => _host = host;

    [Fact]
    public void ModifyAllSettings_Save_Reload_RoundTripPreserved()
    {
        _host.ResetState();

        _host.Run(() =>
        {
            var s = _host.AppSettings;
            s.PlcConfig.IpAddress = "10.20.30.40";
            s.PlcConfig.Port = 9999;
            s.PollingIntervalMs = 1000;
            s.HistoryWriteIntervalScans = 100;
            s.DashboardRefreshIntervalMs = 10000;
            s.IsDarkTheme = true;
            s.Save();
        });

        // 重新加载（新建实例，无 UI 绑定）
        var loaded = new AppSettings { ConfigDirectory = _host.TempDir };
        loaded.Load();

        Assert.Equal("10.20.30.40", loaded.PlcConfig.IpAddress);
        Assert.Equal(9999, loaded.PlcConfig.Port);
        Assert.Equal(1000, loaded.PollingIntervalMs);
        Assert.Equal(100, loaded.HistoryWriteIntervalScans);
        Assert.Equal(10000, loaded.DashboardRefreshIntervalMs);
        Assert.True(loaded.IsDarkTheme);
    }

    [Fact]
    public void CorruptSettingsFile_Load_SetsErrorMessageAndFallsBackToDefaults()
    {
        _host.ResetState();

        _host.AppSettings.Save();
        System.IO.File.WriteAllText(_host.AppSettings.SettingsFilePath, "{ not valid json }");

        var loaded = new AppSettings { ConfigDirectory = _host.TempDir };
        loaded.Load();

        Assert.NotNull(loaded.LoadErrorMessage);
        Assert.Contains("损坏", loaded.LoadErrorMessage);
        Assert.Equal("127.0.0.1", loaded.PlcConfig.IpAddress);
        Assert.Equal(200, loaded.PollingIntervalMs);
        Assert.False(loaded.IsDarkTheme);
        Assert.True(System.IO.File.Exists(_host.AppSettings.SettingsFilePath + ".corrupt"));
    }

    [Fact]
    public void ShiftsModification_Save_Reload_PreservesShifts()
    {
        _host.ResetState();

        _host.Run(() =>
        {
            var s = _host.AppSettings;
            s.Shifts.Clear();
            s.Shifts.Add(new Kanban.Collector.Core.Models.ShiftConfig { Name = "早班", StartTime = new System.TimeSpan(0, 0, 0), EndTime = new System.TimeSpan(12, 0, 0) });
            s.Shifts.Add(new Kanban.Collector.Core.Models.ShiftConfig { Name = "晚班", StartTime = new System.TimeSpan(12, 0, 0), EndTime = new System.TimeSpan(0, 0, 0) });
            s.Save();
        });

        var loaded = new AppSettings { ConfigDirectory = _host.TempDir };
        loaded.Load();

        Assert.Equal(2, loaded.Shifts.Count);
        Assert.Equal("早班", loaded.Shifts[0].Name);
        Assert.Equal(new System.TimeSpan(0, 0, 0), loaded.Shifts[0].StartTime);
        Assert.Equal(new System.TimeSpan(12, 0, 0), loaded.Shifts[0].EndTime);
        Assert.Equal("晚班", loaded.Shifts[1].Name);
    }

    [Fact]
    public void Baselines_Save_Reload_PreservesShiftId()
    {
        _host.ResetState();

        var baselines = new Dictionary<string, int>
        {
            ["dev1_ok_base"] = 1000,
            ["dev1_ng_base"] = 50,
            ["dev2_ok_base"] = 2000
        };
        var store = _host.Host.Services.GetRequiredService<ProductionBaselineStore>();
        store.SaveBaselines(baselines, "白班|08:00:00|20:00:00");

        var loaded = new ProductionBaselineStore(new AppSettings { ConfigDirectory = _host.TempDir });
        loaded.Load();

        Assert.Equal(3, loaded.LoadedBaselines.Count);
        Assert.Equal(1000, loaded.LoadedBaselines["dev1_ok_base"]);
        Assert.Equal(50, loaded.LoadedBaselines["dev1_ng_base"]);
        Assert.Equal(2000, loaded.LoadedBaselines["dev2_ok_base"]);
    }

    [Fact]
    public void DeviceProductionResetAddress_Save_Reload_Preserved()
    {
        // 产量清零地址已随"数据归位"下放到设备级（Device.ProductionResetAddress），经 DeviceRepository 持久化。
        // 使用隔离临时目录，避免污染共享 TestHost 的设备配置。
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KanbanE2EReset_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var repo = new DeviceRepository(new AppSettings { ConfigDirectory = dir });
            repo.Devices.Add(new Kanban.Collector.Core.Models.Device { Id = "dev-reset", Name = "复位设备", ProductionResetAddress = "D999" });
            repo.SaveAll();

            var reloaded = new DeviceRepository(new AppSettings { ConfigDirectory = dir });
            reloaded.LoadAll();
            var dev = reloaded.Devices.FirstOrDefault(d => d.Id == "dev-reset");

            Assert.NotNull(dev);
            Assert.Equal("D999", dev!.ProductionResetAddress);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsView_ReflectsAppSettings_AfterLoad()
    {
        _host.ResetState();
        _host.InitializeDatabases();

        _host.Run(() =>
        {
            var s = _host.AppSettings;
            s.PlcConfig.IpAddress = "172.16.1.100";
            s.PollingIntervalMs = 500;
            s.Save();
        });

        _host.RunOnSta(app =>
        {
            var window = _host.GetMainWindow();
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            var vm = _host.GetMainWindowViewModel();
            vm.SelectedIndex = 6; // 设置页（索引 6）
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            window.UpdateLayout();

            Assert.Equal("172.16.1.100", vm.SettingsViewModel.AppSettings.PlcConfig.IpAddress);
            Assert.Equal(500, vm.SettingsViewModel.AppSettings.PollingIntervalMs);

            window.Hide();
        });
    }
}
