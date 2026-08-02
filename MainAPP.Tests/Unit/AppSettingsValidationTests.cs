using System.IO;
using System.Linq;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// AppSettings 配置持久化单元测试（settings.json）。
/// 覆盖：默认值、Save/Load 往返、文件不存在、损坏文件回退、默认班次回退。
/// 使用临时目录隔离，不触碰真实 %APPDATA%/Kanban。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class AppSettingsValidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;

    public AppSettingsValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanSettingsTests_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings { ConfigDirectory = _tempDir };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Defaults_AreExpected()
    {
        var s = new AppSettings();
        Assert.Equal("192.168.1.2", s.PlcConfig.IpAddress);
        Assert.Equal(4999, s.PlcConfig.Port);
        Assert.Equal(Models.PlcBrand.Mitsubishi, s.PlcConfig.Brand);
        Assert.Equal(5000, s.PlcConfig.TimeoutMs);
        Assert.Equal(200, s.PollingIntervalMs);
        Assert.Equal(25, s.HistoryWriteIntervalScans);
        Assert.Equal(3000, s.DashboardRefreshIntervalMs);
        Assert.False(s.IsDarkTheme);
        Assert.Null(s.LoadErrorMessage);
        Assert.Equal(2, s.Shifts.Count);
    }

    [Fact]
    public void DefaultShifts_CoversDayAndNight()
    {
        var defaults = AppSettings.GetDefaultShifts();
        Assert.Equal(2, defaults.Count);
        Assert.Equal("白班", defaults[0].Name);
        Assert.Equal("夜班", defaults[1].Name);
        // 白班 08:00-20:00
        Assert.Equal(new System.TimeSpan(8, 0, 0), defaults[0].StartTime);
        Assert.Equal(new System.TimeSpan(20, 0, 0), defaults[0].EndTime);
        // 夜班跨天 20:00-次日 08:00
        Assert.Equal(new System.TimeSpan(20, 0, 0), defaults[1].StartTime);
        Assert.Equal(new System.TimeSpan(8, 0, 0), defaults[1].EndTime);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesAllSettings()
    {
        _settings.PlcConfig.IpAddress = "10.0.0.99";
        _settings.PlcConfig.Port = 6000;
        _settings.PlcConfig.Brand = Models.PlcBrand.ModbusTcp;
        _settings.PlcConfig.TimeoutMs = 8000;
        _settings.PlcConfig.ModbusUnitId = 7;
        _settings.PlcConfig.ModbusAddressStartWithZero = false;
        _settings.PlcConfig.ModbusRegisterFunction = 4;
        _settings.PlcConfig.ModbusBitFunction = 2;
        _settings.PlcConfig.ModbusDataFormat = Models.PlcDataFormat.CDAB;
        _settings.PlcConfig.SiemensDataFormat = Models.PlcDataFormat.BADC;
        _settings.PollingIntervalMs = 500;
        _settings.HistoryWriteIntervalScans = 50;
        _settings.DashboardRefreshIntervalMs = 5000;
        _settings.IsDarkTheme = true;
        _settings.Shifts.Clear();
        _settings.Shifts.Add(new Models.ShiftConfig { Name = "早班", StartTime = new System.TimeSpan(0, 0, 0), EndTime = new System.TimeSpan(12, 0, 0) });

        _settings.Save();
        Assert.True(File.Exists(_settings.SettingsFilePath));

        var loaded = new AppSettings { ConfigDirectory = _tempDir };
        loaded.Load();

        Assert.Equal("10.0.0.99", loaded.PlcConfig.IpAddress);
        Assert.Equal(6000, loaded.PlcConfig.Port);
        Assert.Equal(Models.PlcBrand.ModbusTcp, loaded.PlcConfig.Brand);
        Assert.Equal(8000, loaded.PlcConfig.TimeoutMs);
        Assert.Equal((byte)7, loaded.PlcConfig.ModbusUnitId);
        Assert.False(loaded.PlcConfig.ModbusAddressStartWithZero);
        Assert.Equal(4, loaded.PlcConfig.ModbusRegisterFunction);
        Assert.Equal(2, loaded.PlcConfig.ModbusBitFunction);
        Assert.Equal(Models.PlcDataFormat.CDAB, loaded.PlcConfig.ModbusDataFormat);
        Assert.Equal(Models.PlcDataFormat.BADC, loaded.PlcConfig.SiemensDataFormat);
        Assert.Equal(500, loaded.PollingIntervalMs);
        Assert.Equal(50, loaded.HistoryWriteIntervalScans);
        Assert.Equal(5000, loaded.DashboardRefreshIntervalMs);
        Assert.True(loaded.IsDarkTheme);
        Assert.Single(loaded.Shifts);
        Assert.Equal("早班", loaded.Shifts[0].Name);
        Assert.Null(loaded.LoadErrorMessage);
    }

    [Fact]
    public void Load_NonExistentFile_DoesNotThrow()
    {
        var s = new AppSettings { ConfigDirectory = _tempDir };
        s.Load();
        // 默认值保持不变
        Assert.Equal("192.168.1.2", s.PlcConfig.IpAddress);
        Assert.Null(s.LoadErrorMessage);
    }

    [Fact]
    public void Load_CorruptFile_SetsErrorMessageAndBacksUp()
    {
        _settings.EnsureDirectory();
        File.WriteAllText(_settings.SettingsFilePath, "{ this is not valid json }");

        var s = new AppSettings { ConfigDirectory = _tempDir };
        s.Load();

        Assert.NotNull(s.LoadErrorMessage);
        Assert.Contains("损坏", s.LoadErrorMessage);
        // 损坏文件备份为 .corrupt
        Assert.True(File.Exists(_settings.SettingsFilePath + ".corrupt"));
        // 回退默认值
        Assert.Equal("192.168.1.2", s.PlcConfig.IpAddress);
        Assert.Equal(2, s.Shifts.Count); // 默认两班次
    }

    [Fact]
    public void Load_EmptyShifts_FallsBackToDefault()
    {
        _settings.EnsureDirectory();
        // 序列化一个 Shifts 为空的配置
        File.WriteAllText(_settings.SettingsFilePath,
            "{\"PlcConfig\":{\"IpAddress\":\"1.2.3.4\",\"Port\":1234},\"Shifts\":[]}");

        var s = new AppSettings { ConfigDirectory = _tempDir };
        s.Load();

        Assert.Equal("1.2.3.4", s.PlcConfig.IpAddress);
        Assert.Equal(1234, s.PlcConfig.Port);
        // 空 Shifts 应回退默认两班次
        Assert.Equal(2, s.Shifts.Count);
    }

    [Fact]
    public void Save_CreatesBackup_OnSecondWrite()
    {
        _settings.PlcConfig.IpAddress = "1.1.1.1";
        _settings.Save();
        Assert.False(File.Exists(_settings.SettingsFilePath + ".bak"));

        _settings.PlcConfig.IpAddress = "2.2.2.2";
        _settings.Save();
        Assert.True(File.Exists(_settings.SettingsFilePath + ".bak"));
        // .bak 内容应为上一版本
        var bak = File.ReadAllText(_settings.SettingsFilePath + ".bak");
        Assert.Contains("1.1.1.1", bak);
    }

    [Fact]
    public void GetFilePath_CombinesDataRootAndConfigDirectory()
    {
        var s = new AppSettings { ConfigDirectory = _tempDir };
        var path = s.GetFilePath("test.json");
        Assert.EndsWith("test.json", path);
        Assert.Contains(_tempDir, path);
    }

    [Fact]
    public void EnsureDirectory_CreatesDirectoryIfMissing()
    {
        var subDir = Path.Combine(_tempDir, "SubConfig");
        var s = new AppSettings { ConfigDirectory = subDir };
        Assert.False(Directory.Exists(subDir));
        s.EnsureDirectory();
        Assert.True(Directory.Exists(subDir));
    }
}
