using System.IO;
using System.Text.Json;
using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using WorkOrderStatus = Kanban.Contracts.Enums.WorkOrderStatus;
using ContractsAlarmLevel = Kanban.Contracts.Enums.AlarmLevel;

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
    private readonly IPlcRuntimeProfileProvider _profileProvider;
    private readonly IPlcConnectionManager _connectionManager;

    public ConfigSyncHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanTests", "ConfigSync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _tempDir);
        _appSettings = new AppSettings();

        var services = new ServiceCollection();
        _profileProvider = Substitute.For<IPlcRuntimeProfileProvider>();
        _connectionManager = Substitute.For<IPlcConnectionManager>();
        services.AddSingleton(_profileProvider);
        services.AddSingleton(_connectionManager);

        _handler = new ConfigSyncHandler(
            new DeviceRepository(_appSettings),
            null!, // WorkOrderRepository：SaveCollectorSettingsAsync 不触达，测试无需构造
            new Kanban.Collector.Services.SnapshotAggregator(),
            _appSettings,
            services.BuildServiceProvider(),
            Substitute.For<ILogger<ConfigSyncHandler>>(),
            null!, // IRecipeStore：本测试不触达配方方法
            null!); // RecipeApplier：本测试不触达配方下发
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
            Shifts = [new ShiftConfigDto { Name = "早班", StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromHours(24) }],
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

    // ───────────── 管理写接口入参校验（审查修复 2026-08-13：此前零校验，非法枚举强转落库） ─────────────

    [Fact]
    public async Task UpsertWorkOrder_InvalidStatus_ThrowsArgumentOutOfRange()
    {
        var dto = new WorkOrderDto
        {
            OrderNo = "WO-1",
            ProductCode = "P",
            ProductName = "测试",
            DeviceId = "dev-1",
            DeviceName = "设备",
            Status = (WorkOrderStatus)99,
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _handler.UpsertWorkOrderAsync(dto));
    }

    [Fact]
    public async Task UpsertWorkOrder_BlankOrderNo_ThrowsArgumentException()
    {
        var dto = new WorkOrderDto
        {
            OrderNo = " ",
            ProductCode = "P",
            ProductName = "测试",
            DeviceId = "dev-1",
            DeviceName = "设备",
            Status = WorkOrderStatus.Pending,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _handler.UpsertWorkOrderAsync(dto));
    }

    [Fact]
    public async Task DeleteWorkOrder_NonPositiveId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _handler.DeleteWorkOrderAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _handler.DeleteWorkOrderAsync(-1));
    }

    [Fact]
    public async Task SaveDevices_InvalidAlarmLevel_Throws()
    {
        var dto = new DeviceConfigDto
        {
            Id = "dev-1",
            Name = "设备",
            OkCountAddress = "D100",
            NgCountAddress = "D102",
            StatusCountAddress = "D104",
            ProductionResetAddress = "D106",
            RecipeName = "",
            RecipeAddress = "",
            Alarms =
            [
                new AlarmConfigDto
                {
                    Id = "alm-1",
                    DeviceId = "dev-1",
                    Name = "报警",
                    PlcAddress = "M0",
                    Description = "",
                    Level = (ContractsAlarmLevel)99,
                },
            ],
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _handler.SaveDevicesAsync([dto]));
    }

    [Fact]
    public async Task SaveDevices_BlankIdOrName_Throws()
    {
        var dto = new DeviceConfigDto
        {
            Id = "",
            Name = "设备",
            OkCountAddress = "D100",
            NgCountAddress = "D102",
            StatusCountAddress = "D104",
            ProductionResetAddress = "D106",
            RecipeName = "",
            RecipeAddress = "",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _handler.SaveDevicesAsync([dto]));
    }

    [Fact]
    public async Task SaveRecipes_InvalidRecipe_ThrowsAndDoesNotPersist()
    {
        // 需要真实 RecipeStore（校验路径读取现有配方集合）
        var handler = new ConfigSyncHandler(
            new DeviceRepository(_appSettings),
            null!,
            new Kanban.Collector.Services.SnapshotAggregator(),
            _appSettings,
            new ServiceCollection().BuildServiceProvider(),
            Substitute.For<ILogger<ConfigSyncHandler>>(),
            new RecipeStore(_appSettings),
            null!);

        var bad = new RecipeDto { Id = "r-1", Name = "", MachineType = "" }; // 空名 → 校验失败

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.SaveRecipesAsync([bad]));
    }

    [Fact]
    public void GetTitle_ReturnsAppSettingsAppTitle()
    {
        // 默认值
        Assert.Equal("生产看板", _handler.GetTitle());

        // 修改 AppSettings 后反映（屏端经 Hub GetTitleAsync 拉取）
        _appSettings.AppTitle = "一号车间看板";
        Assert.Equal("一号车间看板", _handler.GetTitle());

        // 空白标题回退默认
        _appSettings.AppTitle = "   ";
        Assert.Equal("生产看板", _handler.GetTitle());
    }

    [Fact]
    public void GetLanguage_ReturnsAppSettingsLanguage()
    {
        // 默认中文
        Assert.Equal((int)AppLanguage.Zh, _handler.GetLanguage());

        // 修改 AppSettings 后反映（屏端经 Hub GetLanguageAsync 拉取）
        _appSettings.Language = AppLanguage.En;
        Assert.Equal(1, _handler.GetLanguage());

        _appSettings.Language = AppLanguage.Ja;
        Assert.Equal(2, _handler.GetLanguage());
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
            PlcBrand = (int)PlcBrand.Keyence,
        });

        var settingsPath = Path.Combine(_tempDir, "Config", "settings.json");
        Assert.True(File.Exists(settingsPath), "Collector 侧 settings.json 应被落盘");

        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var root = doc.RootElement;
        Assert.Equal(300, root.GetProperty("PollingIntervalMs").GetInt32());
        Assert.Equal((int)PlcBrand.Keyence, root.GetProperty("PlcConfig").GetProperty("Brand").GetInt32());
    }

    [Fact]
    public async Task SaveCollectorSettings_RejectsUnknownPlcBrand()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto { PlcBrand = 999 }));

        Assert.Equal(PlcBrand.Mitsubishi, _appSettings.PlcConfig.Brand);
        _connectionManager.DidNotReceive().Disconnect();
    }

    [Fact]
    public async Task SaveCollectorSettings_PlcConnectionChange_RefreshesProfileAndDisconnects()
    {
        await _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto
        {
            PlcBrand = (int)PlcBrand.Keyence,
            PlcIpAddress = "10.0.0.8",
            PlcPort = 5000,
        });

        Assert.Equal(PlcBrand.Keyence, _appSettings.PlcConfig.Brand);
        Assert.Equal("10.0.0.8", _appSettings.PlcConfig.IpAddress);
        Assert.Equal(5000, _appSettings.PlcConfig.Port);
        _profileProvider.Received(1).Refresh(Arg.Any<PlcConfig>());
        _connectionManager.Received(1).Disconnect();
    }

    [Fact]
    public async Task SaveCollectorSettings_WithoutPlcConnectionChange_DoesNotDisconnect()
    {
        await _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto { PollingIntervalMs = 300 });

        Assert.Equal(300, _appSettings.PollingIntervalMs);
        _profileProvider.DidNotReceive().Refresh(Arg.Any<PlcConfig>());
        _connectionManager.DidNotReceive().Disconnect();
    }

    [Fact]
    public async Task SaveCollectorSettings_InvalidValue_RejectsWithoutMutatingInstanceOrFile()
    {
        // 负轮询间隔：完整 Validate 必须拒绝（此前会直接写入并让采集循环 Task.Delay 抛异常）
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto { PollingIntervalMs = -5 }));

        // 运行实例保持原状（不落内存）
        Assert.Equal(200, _appSettings.PollingIntervalMs);
        // 磁盘不写入（settings.json 不应存在）
        var settingsPath = Path.Combine(_tempDir, "Config", "settings.json");
        Assert.False(File.Exists(settingsPath), "校验失败不应落盘");
        // PLC 驱动不热切换
        _profileProvider.DidNotReceive().Refresh(Arg.Any<PlcConfig>());
        _connectionManager.DidNotReceive().Disconnect();
    }

    [Fact]
    public async Task SaveCollectorSettings_OversizedPageBatchLimits_RejectedByValidation()
    {
        // PlcBatchReadMaxLength 超过 Clamp 上限：校验必须拒绝（此前会传入 PlcScanPipeline 抛 ArgumentOutOfRangeException）
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto { PlcBatchReadMaxLength = ushort.MaxValue + 1 }));

        Assert.Equal(64, _appSettings.PlcBatchReadMaxLength);
        Assert.False(File.Exists(Path.Combine(_tempDir, "Config", "settings.json")));
    }

    [Fact]
    public async Task SaveCollectorSettings_DiskWriteFailure_RunningInstanceAndDriverUnchanged()
    {
        // 制造磁盘写失败：用同名文件占据 Config 目录路径，EnsureDirectory 的 Directory.CreateDirectory 会抛 IOException
        // （对应"先落盘③→后生效④"的落盘失败分支：磁盘写失败时运行实例与 PLC 驱动必须保持原状）
        File.WriteAllText(Path.Combine(_tempDir, "Config"), "block-config-dir");

        await Assert.ThrowsAsync<IOException>(() =>
            _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto
            {
                PollingIntervalMs = 900,
                PlcIpAddress = "10.0.0.9",
                PlcPort = 7000,
            }));

        // 运行实例保持原状（不落内存）
        Assert.Equal(200, _appSettings.PollingIntervalMs);
        Assert.Equal("192.168.1.2", _appSettings.PlcConfig.IpAddress);
        Assert.Equal(4999, _appSettings.PlcConfig.Port);
        // PLC 驱动不热切换
        _profileProvider.DidNotReceive().Refresh(Arg.Any<PlcConfig>());
        _connectionManager.DidNotReceive().Disconnect();
    }

    [Fact]
    public async Task SaveCollectorSettings_NestedBrandOptions_AppliedAndHotSwitched()
    {
        // 嵌套品牌参数变化（Siemens.Model）也应触发 Profile 刷新 + 断开重连（此前只比较公共字段会漏）
        await _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto
        {
            PlcBrand = (int)PlcBrand.Siemens,
            PlcIpAddress = "10.0.0.8",
            PlcPort = 102,
            Siemens = new SiemensSettingsDto { Model = "S1500", Rack = 0, Slot = 2, BatchInt32Limit = 40 },
        });

        Assert.Equal(PlcBrand.Siemens, _appSettings.PlcConfig.Brand);
        Assert.Equal("S1500", _appSettings.PlcConfig.Siemens.Model);
        Assert.Equal(2, _appSettings.PlcConfig.Siemens.Slot);
        Assert.Equal(40, _appSettings.PlcConfig.Siemens.BatchInt32Limit);
        _profileProvider.Received(1).Refresh(Arg.Any<PlcConfig>());
        _connectionManager.Received(1).Disconnect();
    }

    [Fact]
    public async Task SaveCollectorSettings_NestedOmronOptions_Applied()
    {
        await _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto
        {
            PlcBrand = (int)PlcBrand.Omron,
            Omron = new OmronFinsSettingsDto { ReadSplits = 300 },
        });

        Assert.Equal(PlcBrand.Omron, _appSettings.PlcConfig.Brand);
        Assert.Equal(300, _appSettings.PlcConfig.Omron.ReadSplits);
    }

    [Fact]
    public async Task SaveCollectorSettings_PreservesNonCollectorFields_OnDisk()
    {
        // 模拟运行实例已加载的非采集配置（语言/标题/数据模式/主题/字号/报警音/日报/主页刷新/Collector 地址）
        _appSettings.Language = AppLanguage.En;
        _appSettings.AppTitle = "一号车间看板";
        _appSettings.DataMode = KanbanDataMode.Remote;
        _appSettings.RunMode = KanbanRunMode.Viewer;
        _appSettings.IsDarkTheme = true;
        _appSettings.UiScale = 1.3;
        _appSettings.EnableAlarmSound = false;
        _appSettings.EnableAutomaticDailyReport = true;
        _appSettings.AutomaticDailyReportTime = new TimeSpan(6, 30, 0);
        _appSettings.DashboardRefreshIntervalMs = 5000;
        _appSettings.CollectorHubUrl = "http://192.168.1.50:5129/hubs/kanban";

        // 只改采集字段
        await _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto { PollingIntervalMs = 500 });

        // 非采集字段必须原样保留在磁盘（此前草稿只拷 5 个采集字段 → 全量序列化把其余字段重置为默认值）
        var settingsPath = Path.Combine(_tempDir, "Config", "settings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var root = doc.RootElement;
        Assert.Equal((int)AppLanguage.En, root.GetProperty("Language").GetInt32());
        Assert.Equal("一号车间看板", root.GetProperty("AppTitle").GetString());
        Assert.Equal((int)KanbanDataMode.Remote, root.GetProperty("DataMode").GetInt32());
        Assert.Equal((int)KanbanRunMode.Viewer, root.GetProperty("RunMode").GetInt32());
        Assert.True(root.GetProperty("IsDarkTheme").GetBoolean());
        Assert.Equal(1.3, root.GetProperty("UiScale").GetDouble());
        Assert.False(root.GetProperty("EnableAlarmSound").GetBoolean());
        Assert.True(root.GetProperty("EnableAutomaticDailyReport").GetBoolean());
        Assert.Equal("06:30:00", root.GetProperty("AutomaticDailyReportTime").GetString());
        Assert.Equal(5000, root.GetProperty("DashboardRefreshIntervalMs").GetInt32());
        Assert.Equal("http://192.168.1.50:5129/hubs/kanban", root.GetProperty("CollectorHubUrl").GetString());
        // 采集字段更新生效
        Assert.Equal(500, root.GetProperty("PollingIntervalMs").GetInt32());
    }

    [Fact]
    public void CreateDraft_CopiesAllPersistentFields()
    {
        // 全字段草稿拷贝：任何持久化字段都必须进入草稿（漏字段 = 落盘被默认值覆盖）
        _appSettings.Language = AppLanguage.Ja;
        _appSettings.AppTitle = "测试标题";
        _appSettings.DataMode = KanbanDataMode.Remote;
        _appSettings.RunMode = KanbanRunMode.Viewer;
        _appSettings.IsDarkTheme = true;
        _appSettings.UiScale = 1.15;
        _appSettings.EnableAlarmSound = false;
        _appSettings.EnableAutomaticDailyReport = true;
        _appSettings.AutomaticDailyReportTime = new TimeSpan(5, 0, 0);
        _appSettings.DashboardRefreshIntervalMs = 4000;
        _appSettings.CollectorHubUrl = "http://10.0.0.9:5129/hubs/kanban";
        _appSettings.PollingIntervalMs = 700;
        _appSettings.HistoryWriteIntervalScans = 60;
        _appSettings.PlcBatchReadMaxLength = 128;
        _appSettings.PlcBatchReadMaxGapSlots = 2;
        _appSettings.PlcConfig.IpAddress = "10.1.1.1";
        _appSettings.PlcConfig.Port = 102;
        _appSettings.Shifts = [new ShiftConfig { Name = "甲班", StartTime = TimeSpan.FromHours(7), EndTime = TimeSpan.FromHours(19) }];

        var draft = _appSettings.CreateDraft();

        Assert.Equal(AppLanguage.Ja, draft.Language);
        Assert.Equal("测试标题", draft.AppTitle);
        Assert.Equal(KanbanDataMode.Remote, draft.DataMode);
        Assert.Equal(KanbanRunMode.Viewer, draft.RunMode);
        Assert.True(draft.IsDarkTheme);
        Assert.Equal(1.15, draft.UiScale);
        Assert.False(draft.EnableAlarmSound);
        Assert.True(draft.EnableAutomaticDailyReport);
        Assert.Equal(new TimeSpan(5, 0, 0), draft.AutomaticDailyReportTime);
        Assert.Equal(4000, draft.DashboardRefreshIntervalMs);
        Assert.Equal("http://10.0.0.9:5129/hubs/kanban", draft.CollectorHubUrl);
        Assert.Equal(700, draft.PollingIntervalMs);
        Assert.Equal(60, draft.HistoryWriteIntervalScans);
        Assert.Equal(128, draft.PlcBatchReadMaxLength);
        Assert.Equal(2, draft.PlcBatchReadMaxGapSlots);
        Assert.Equal("10.1.1.1", draft.PlcConfig.IpAddress);
        Assert.Equal(102, draft.PlcConfig.Port);
        Assert.Single(draft.Shifts);
        Assert.Equal("甲班", draft.Shifts[0].Name);
        // 草稿是独立实例：改草稿不影响运行实例（快照语义）
        draft.PlcConfig.Port = 999;
        Assert.Equal(102, _appSettings.PlcConfig.Port);
    }

    [Fact]
    public async Task SaveCollectorSettings_InvalidNestedDataFormat_RejectedWithoutMutation()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _handler.SaveCollectorSettingsAsync(new CollectorSettingsDto
            {
                PlcBrand = (int)PlcBrand.Siemens,
                Siemens = new SiemensSettingsDto { DataFormat = 99 },
            }));

        // 运行实例保持原状、不落盘、不热切换
        Assert.Equal(PlcBrand.Mitsubishi, _appSettings.PlcConfig.Brand);
        Assert.False(File.Exists(Path.Combine(_tempDir, "Config", "settings.json")));
        _profileProvider.DidNotReceive().Refresh(Arg.Any<PlcConfig>());
        _connectionManager.DidNotReceive().Disconnect();
    }

    [Fact]
    public void GetServerVersion_ReturnsNonEmpty()
    {
        var version = _handler.GetServerVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
    }
}
