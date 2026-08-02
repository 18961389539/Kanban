using System.IO;
using System.Linq;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// DeviceRepository 集成测试：SaveAll/LoadAll 往返（JSON 持久化）
/// 使用临时目录隔离，每个测试类共享一个临时目录
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","Database")]
public class DeviceRepositoryRoundTripTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;

    public DeviceRepositoryRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void SaveAll_LoadAll_RoundTrip_PreservesDeviceConfig()
    {
        var repo = new DeviceRepository(_appSettings);
        repo.Devices.Add(new Device
        {
            Name = "测试设备A",
            OkCountAddress = "D100",
            NgCountAddress = "D101",
            StatusCountAddress = "D102",
            RecipeName = "配方1",
            RecipeValue = 500,
            RecipeAddress = "D200",
            TargetCycle = 120
        });

        repo.SaveAll();

        // 重新加载
        var repo2 = new DeviceRepository(_appSettings);
        repo2.LoadAll();

        Assert.Single(repo2.Devices);
        var loaded = repo2.Devices[0];
        Assert.Equal("测试设备A", loaded.Name);
        Assert.Equal("D100", loaded.OkCountAddress);
        Assert.Equal("D101", loaded.NgCountAddress);
        Assert.Equal("D102", loaded.StatusCountAddress);
        Assert.Equal("配方1", loaded.RecipeName);
        Assert.Equal(500, loaded.RecipeValue);
        Assert.Equal("D200", loaded.RecipeAddress);
        Assert.Equal(120, loaded.TargetCycle);
    }

    [Fact]
    public void SaveAll_BackfillsDeviceIdToAlarmsAndDefects()
    {
        var repo = new DeviceRepository(_appSettings);
        var device = new Device { Name = "设备1", OkCountAddress = "D100" };
        device.Alarms.Add(new Alarm { Name = "报警1", PlcAddress = "M100" });
        device.Defects.Add(new Defect { Name = "缺陷1", PlcAddress = "D200" });
        repo.Devices.Add(device);

        // 保存前 Alarm.DeviceId / Defect.DeviceId 是默认值
        Assert.Equal(string.Empty, device.Alarms[0].DeviceId);

        repo.SaveAll();

        // 保存后回填了 DeviceId
        Assert.Equal(device.Id, device.Alarms[0].DeviceId);
        Assert.Equal(device.Id, device.Defects[0].DeviceId);
    }

    [Fact]
    public void SaveAll_LoadAll_RoundTrip_PreservesAlarmsAndDefects()
    {
        var repo = new DeviceRepository(_appSettings);
        var device = new Device { Name = "设备1", OkCountAddress = "D100" };
        device.Alarms.Add(new Alarm
        {
            Name = "高温报警",
            PlcAddress = "M100",
            Level = AlarmLevel.High,
            Description = "温度超过阈值"
        });
        device.Defects.Add(new Defect { Name = "划痕", PlcAddress = "D200" });
        repo.Devices.Add(device);

        repo.SaveAll();

        // 诊断：确认 JSON 中确实包含了 Alarms/Defects
        var json = File.ReadAllText(_appSettings.GetFilePath("devices.json"));
        Assert.Contains("Alarms", json);
        Assert.Contains("高温报警", json);
        Assert.Contains("Defects", json);
        Assert.Contains("划痕", json);

        var repo2 = new DeviceRepository(_appSettings);
        repo2.LoadAll();

        Assert.Single(repo2.Devices);
        var loaded = repo2.Devices[0];
        Assert.Single(loaded.Alarms);
        Assert.Equal("高温报警", loaded.Alarms[0].Name);
        Assert.Equal("M100", loaded.Alarms[0].PlcAddress);
        Assert.Equal(AlarmLevel.High, loaded.Alarms[0].Level);
        Assert.Equal("温度超过阈值", loaded.Alarms[0].Description);
        Assert.Equal(device.Id, loaded.Alarms[0].DeviceId);

        Assert.Single(loaded.Defects);
        Assert.Equal("划痕", loaded.Defects[0].Name);
        Assert.Equal("D200", loaded.Defects[0].PlcAddress);
        Assert.Equal(device.Id, loaded.Defects[0].DeviceId);
    }

    [Fact]
    public void LoadAll_FileNotExists_LeavesEmptyCollection()
    {
        var repo = new DeviceRepository(_appSettings);
        // 文件不存在时不抛异常
        repo.LoadAll();
        Assert.Empty(repo.Devices);
    }

    [Fact]
    public void LoadAll_CorruptedJson_LeavesEmptyCollection()
    {
        _appSettings.EnsureDirectory();
        File.WriteAllText(_appSettings.GetFilePath("devices.json"), "{ invalid json !!!");
        var repo = new DeviceRepository(_appSettings);
        // 损坏文件不抛异常，保持空集合
        repo.LoadAll();
        Assert.Empty(repo.Devices);
    }

    [Fact]
    public void SaveAll_CreatesDirectoryIfMissing()
    {
        // 删除目录后保存，应自动重建
        Directory.Delete(_tempDir, true);
        Assert.False(Directory.Exists(_tempDir));

        var repo = new DeviceRepository(_appSettings);
        repo.Devices.Add(new Device { Name = "X", OkCountAddress = "D1" });
        repo.SaveAll();

        Assert.True(Directory.Exists(_tempDir));
        Assert.True(File.Exists(_appSettings.GetFilePath("devices.json")));
    }

    [Fact]
    public void LoadAll_CreatesRuntimesForEachDevice()
    {
        var repo = new DeviceRepository(_appSettings);
        var device = new Device { Name = "设备1", OkCountAddress = "D100", TargetCycle = 80 };
        repo.Devices.Add(device);
        repo.SaveAll();

        var repo2 = new DeviceRepository(_appSettings);
        repo2.LoadAll();

        Assert.Single(repo2.Runtimes);
        Assert.Equal(repo2.Devices[0].Id, repo2.Runtimes[0].DeviceId);
        Assert.Equal(80, repo2.Devices[0].TargetCycle);
    }

    [Fact]
    public void SaveAll_DoesNotPersistRuntimeState()
    {
        // 运行时状态（OkProduction/RunTime 等）应位于 DeviceRuntime 而非 Device，
        // devices.json 不应包含这些字段
        var repo = new DeviceRepository(_appSettings);
        var device = new Device { Name = "设备1", OkCountAddress = "D100" };
        repo.Devices.Add(device);
        repo.AddRuntime(device);
        repo.Runtimes[0].OkProduction = 999;
        repo.Runtimes[0].RunTime = 3600;
        repo.SaveAll();

        var json = File.ReadAllText(_appSettings.GetFilePath("devices.json"));
        // Device 类已无 OkProduction/RunTime 属性，JSON 不应包含
        Assert.DoesNotContain("OkProduction", json);
        Assert.DoesNotContain("RunTime", json);
        Assert.DoesNotContain("AlarmTime", json);
    }
}
