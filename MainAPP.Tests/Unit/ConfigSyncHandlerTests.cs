using System.IO;
using System.Text.Json;
using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// ConfigSyncHandler 测试：锁住**采集设置同步**（第五轮修复 Remote 配置分裂的核心逻辑）。
/// - SaveCollectorSettingsAsync：可空字段部分更新语义 + 班次整体替换 + 落 Collector 侧 settings.json
/// 此前 Collector 服务端零测试，此文件是补盲区第二块。
/// 串行集合：本类修改进程级 KANBAN_DATA_DIR 环境变量（与 SnapshotPublisherTests 同集合隔离）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Collection("EnvIsolation")]
public class ConfigSyncHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly ConfigSyncHandler _handler;

    public ConfigSyncHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanTests", "ConfigSync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _tempDir);
        _appSettings = new AppSettings();
        _handler = new ConfigSyncHandler(
            new DeviceRepository(_appSettings),
            null!, // WorkOrderRepository：SaveCollectorSettingsAsync 不触达，测试无需构造
            new Kanban.Collector.Services.SnapshotAggregator(),
            _appSettings,
            Substitute.For<ILogger<ConfigSyncHandler>>());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    [Fact]
    public async Task SaveCollectorSettings_UpdatesAppSettingsInstance_HotEffective()
    {
        var dto = new CollectorSettingsDto
        {
            PollingIntervalMs = 500,
            HistoryWriteIntervalScans = 50,
            PlcIpAddress = "192.168.1.99",
            PlcPort = 6000,
            Shifts = [new ShiftConfigDto { Name = "早班", StartTime = TimeSpan.FromHours(6), EndTime = TimeSpan.FromHours(18) }],
        };

        await _handler.SaveCollectorSettingsAsync(dto);

        // 热生效：AppSettings 单例实例属性已更新（轮询循环每次迭代读此值）
        Assert.Equal(500, _appSettings.PollingIntervalMs);
        Assert.Equal(50, _appSettings.HistoryWriteIntervalScans);
        Assert.Equal("192.168.1.99", _appSettings.PlcConfig.IpAddress);
        Assert.Equal(6000, _appSettings.PlcConfig.Port);
        Assert.Single(_appSettings.Shifts);
        Assert.Equal("早班", _appSettings.Shifts[0].Name);
    }

    [Fact]
    public async Task SaveCollectorSettings_PartialUpdate_NullFieldsUntouched()
    {
        await _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto { PollingIntervalMs = 800 });

        Assert.Equal(800, _appSettings.PollingIntervalMs);
        // 未提供的字段保持默认/原值（部分更新语义）
        Assert.Equal(25, _appSettings.HistoryWriteIntervalScans);
        Assert.Equal(2, _appSettings.Shifts.Count); // 默认白班+夜班未被清掉
    }

    [Fact]
    public async Task SaveCollectorSettings_PersistsToCollectorSideSettingsJson()
    {
        await _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto
        {
            PollingIntervalMs = 300,
            PlcBrand = (int)PlcBrand.ModbusTcp,
        });

        var settingsPath = Path.Combine(_tempDir, "Config", "settings.json");
        Assert.True(File.Exists(settingsPath), "Collector 侧 settings.json 应被落盘");

        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var root = doc.RootElement;
        Assert.Equal(300, root.GetProperty("PollingIntervalMs").GetInt32());
        Assert.Equal((int)PlcBrand.ModbusTcp, root.GetProperty("PlcConfig").GetProperty("Brand").GetInt32());
    }

    [Fact]
    public void GetServerVersion_ReturnsNonEmpty()
    {
        var version = _handler.GetServerVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
    }
}
