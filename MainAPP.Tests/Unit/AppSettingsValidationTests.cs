using System.IO;
using System.Linq;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
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
        Assert.Equal("127.0.0.1", s.PlcConfig.IpAddress);
        Assert.Equal(4999, s.PlcConfig.Port);
        Assert.Equal(Kanban.Collector.Core.Models.PlcBrand.Mitsubishi, s.PlcConfig.Brand);
        Assert.Equal(5000, s.PlcConfig.TimeoutMs);
        Assert.Equal(200, s.PollingIntervalMs);
        Assert.Equal(25, s.HistoryWriteIntervalScans);
        Assert.Equal(3000, s.DashboardRefreshIntervalMs);
        Assert.False(s.IsDarkTheme);
        Assert.Null(s.LoadErrorMessage);
        Assert.Equal(2, s.Shifts.Count);
        Assert.Same(s.PlcConfig, s.DefaultConnectionProfile.Config);
    }

    [Fact]
    public void Load_LegacyPlcConfigCreatesDefaultConnectionProfile()
    {
        _settings.EnsureDirectory();
        File.WriteAllText(_settings.SettingsFilePath,
            "{\"SchemaVersion\":7,\"PlcConfig\":{\"ProtocolKey\":\"legacy\",\"IpAddress\":\"10.0.0.9\",\"Port\":6001}}");

        var loaded = new AppSettings { ConfigDirectory = _tempDir };
        loaded.Load();

        var profile = Assert.Single(loaded.ConnectionProfiles);
        Assert.Equal(ConnectionProfile.DefaultId, profile.Id);
        Assert.Equal("legacy", profile.Config.ProtocolKey);
        Assert.Equal("10.0.0.9", profile.Config.IpAddress);
        Assert.Equal(6001, profile.Config.Port);
        Assert.Same(loaded.PlcConfig, profile.Config);
    }

    [Fact]
    public void SaveAndLoad_PreservesNamedConnectionProfiles()
    {
        _settings.ConnectionProfiles.Add(new ConnectionProfile
        {
            Id = "line-2",
            Name = "Line 2",
            Config = new PlcConfig
            {
                ProtocolKey = "simulated",
                IpAddress = "10.0.0.20",
                Port = 1502,
            },
        });

        _settings.Save();

        var loaded = new AppSettings { ConfigDirectory = _tempDir };
        loaded.Load();

        var profile = Assert.Single(loaded.ConnectionProfiles, item => item.Id == "line-2");
        Assert.Equal("Line 2", profile.Name);
        Assert.Equal("simulated", profile.Config.ProtocolKey);
        Assert.Equal("10.0.0.20", profile.Config.IpAddress);
        Assert.Equal(1502, profile.Config.Port);
        Assert.Same(loaded.PlcConfig, loaded.DefaultConnectionProfile.Config);
    }

    [Fact]
    public void Validate_ReportsNamedProfileConfigurationErrors()
    {
        _settings.ConnectionProfiles.Add(new ConnectionProfile
        {
            Id = "line-2",
            Name = "",
            Config = new PlcConfig
            {
                IpAddress = "not-an-ip",
                Port = 0,
                TimeoutMs = 99,
                Brand = PlcBrand.ModbusTcp,
            },
        });
        _settings.ConnectionProfiles[1].Config.ModbusTcp.UnitId = 0;

        var errors = _settings.Validate();

        Assert.Contains(errors, error => error.Contains("line-2") && error.Contains("名称"));
        Assert.Contains(errors, error => error.Contains("line-2") && error.Contains("IP"));
        Assert.Contains(errors, error => error.Contains("line-2") && error.Contains("端口"));
        Assert.Contains(errors, error => error.Contains("line-2") && error.Contains("超时"));
        Assert.Contains(errors, error => error.Contains("line-2") && error.Contains("UnitId"));
    }

    [Fact]
    public void Validate_ReportsDuplicateConnectionProfileIds()
    {
        _settings.ConnectionProfiles.Add(new ConnectionProfile { Id = "line-2", Name = "Line 2" });
        _settings.ConnectionProfiles.Add(new ConnectionProfile { Id = "LINE-2", Name = "Line 2 duplicate" });

        var errors = _settings.Validate();

        Assert.Contains(errors, error => error.Contains("连接档案 Id 重复") && error.Contains("LINE-2"));
    }

    [Fact]
    public void KeyenceBrand_UsesMcDefaultPortAndPreservesCustomPort()
    {
        var config = new Kanban.Collector.Core.Models.PlcConfig();

        config.Brand = Kanban.Collector.Core.Models.PlcBrand.Keyence;
        Assert.Equal(5000, config.Port);

        config.Port = 8501;
        config.Brand = Kanban.Collector.Core.Models.PlcBrand.Mitsubishi;
        Assert.Equal(8501, config.Port);
    }

    [Fact]
    public void KeyenceSnapshot_PreservesConnectionSettings()
    {
        var source = new Kanban.Collector.Core.Models.PlcConfig
        {
            Brand = Kanban.Collector.Core.Models.PlcBrand.Keyence,
            IpAddress = "10.10.0.5",
            Port = 8501,
            TimeoutMs = 3500,
        };

        var snapshot = source.CreateSnapshot();

        Assert.Equal(Kanban.Collector.Core.Models.PlcBrand.Keyence, snapshot.Brand);
        Assert.Equal("10.10.0.5", snapshot.IpAddress);
        Assert.Equal(8501, snapshot.Port);
        Assert.Equal(3500, snapshot.TimeoutMs);
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
        _settings.PlcConfig.Brand = Kanban.Collector.Core.Models.PlcBrand.ModbusTcp;
        _settings.PlcConfig.TimeoutMs = 8000;
        _settings.PlcConfig.ModbusUnitId = 7;
        _settings.PlcConfig.ModbusAddressStartWithZero = false;
        _settings.PlcConfig.ModbusRegisterFunction = 4;
        _settings.PlcConfig.ModbusBitFunction = 2;
        _settings.PlcConfig.ModbusDataFormat = Kanban.Collector.Core.Models.PlcDataFormat.CDAB;
        _settings.PlcConfig.SiemensDataFormat = Kanban.Collector.Core.Models.PlcDataFormat.BADC;
        _settings.PollingIntervalMs = 500;
        _settings.HistoryWriteIntervalScans = 50;
        _settings.DashboardRefreshIntervalMs = 5000;
        _settings.IsDarkTheme = true;
        _settings.Shifts.Clear();
        _settings.Shifts.Add(new Kanban.Collector.Core.Models.ShiftConfig { Name = "早班", StartTime = new System.TimeSpan(0, 0, 0), EndTime = new System.TimeSpan(12, 0, 0) });

        _settings.Save();
        Assert.True(File.Exists(_settings.SettingsFilePath));

        var loaded = new AppSettings { ConfigDirectory = _tempDir };
        loaded.Load();

        Assert.Equal("10.0.0.99", loaded.PlcConfig.IpAddress);
        Assert.Equal(6000, loaded.PlcConfig.Port);
        Assert.Equal(Kanban.Collector.Core.Models.PlcBrand.ModbusTcp, loaded.PlcConfig.Brand);
        Assert.Equal(8000, loaded.PlcConfig.TimeoutMs);
        Assert.Equal((byte)7, loaded.PlcConfig.ModbusUnitId);
        Assert.False(loaded.PlcConfig.ModbusAddressStartWithZero);
        Assert.Equal(4, loaded.PlcConfig.ModbusRegisterFunction);
        Assert.Equal(2, loaded.PlcConfig.ModbusBitFunction);
        Assert.Equal(Kanban.Collector.Core.Models.PlcDataFormat.CDAB, loaded.PlcConfig.ModbusDataFormat);
        Assert.Equal(Kanban.Collector.Core.Models.PlcDataFormat.BADC, loaded.PlcConfig.SiemensDataFormat);
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
        Assert.Equal("127.0.0.1", s.PlcConfig.IpAddress);
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
        Assert.Equal("127.0.0.1", s.PlcConfig.IpAddress);
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
