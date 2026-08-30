using System.IO;
using System.Security.Claims;
using Kanban.Collector.Hubs;
using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// KanbanHub 查询/快照路径测试（审查修复 2026-08-13 补 0 覆盖盲区）：
/// 此前 KanbanHub 仅 4 个管理写接口的审计路由有测试，快照/查询/设置/进度等读取路径 0 覆盖。
/// 长驻订阅循环（SubscribeSnapshotsAsync/Alarm/Status/Meta）依赖 HubCallerContext，
/// 无法脱离真实连接单测，由 RemoteRuntimeSinkTests 的契约测试从客户端侧间接锁定。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class KanbanHubQueriesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DatabaseProvider _db;
    private readonly DeviceRepository _deviceRepo;
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly SnapshotAggregator _aggregator;
    private readonly SnEventStore _snStore;
    private readonly KanbanHub _hub;

    /// <summary>最小 HubCallerContext：读取路径（快照/查询）依赖 Context.ConnectionAborted，脱离真实连接单测时注入。</summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        private readonly CancellationTokenSource _cts = new();

        public override string ConnectionId => "unit-test";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items => new Dictionary<object, object?>();
        public override IFeatureCollection Features => new FeatureCollection();
        public override CancellationToken ConnectionAborted => _cts.Token;
        public override void Abort() => _cts.Cancel();
    }

    public KanbanHubQueriesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KanbanHubQueries_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_settings);
        _db.EnsureCreatedAll();
        _deviceRepo = new DeviceRepository(_settings);
        _workOrderRepo = new WorkOrderRepository(_db, Substitute.For<AutoMapper.IMapper>());
        _aggregator = new SnapshotAggregator();
        _snStore = new SnEventStore(_db, NullLogger<SnEventStore>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_settings);
        using var provider = services.BuildServiceProvider();

        var configSync = new ConfigSyncHandler(
            _deviceRepo, _workOrderRepo, _aggregator, _settings, provider,
            NullLogger<ConfigSyncHandler>.Instance, null!, null!);

        // 历史服务替代：查询路径返回空数据（未配置返回时 NSubstitute 默认 null → 处理器 NRE 转 Error）
        var historyService = Substitute.For<IHistoryService>();
        historyService.QueryProductionLogs(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new List<Kanban.Collector.Core.Entities.ProductionLog>());
        historyService.QueryProductionLogsByWorkOrder(Arg.Any<int>())
            .Returns(new List<Kanban.Collector.Core.Entities.ProductionLog>());
        historyService.QueryProductionLogsPaged(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((new List<Kanban.Collector.Core.Entities.ProductionLog>(), 0));

        _hub = new KanbanHub(
            _aggregator,
            new EventBroadcaster(NullLogger<EventBroadcaster>.Instance),
            new HistoryQueryHandler(
                historyService,
                Substitute.For<IHistoryQueryExecutor>(),
                new DefectHistoryStore(_db),
                NullLogger<HistoryQueryHandler>.Instance),
            null!, // CollectorDiagnosticsProvider：本测试不触达诊断接口
            configSync,
            new ShiftProgressProvider(_settings),
            new MetaPublisher(configSync, new ShiftProgressProvider(_settings), NullLogger<MetaPublisher>.Instance),
            _workOrderRepo,
            new HistoryService(_db, NullLogger<HistoryService>.Instance),
            _settings,
            new AuditService(_db, NullLogger<AuditService>.Instance),
            _snStore);
        _hub.Context = new FakeHubCallerContext(); // 读取路径依赖 Context.ConnectionAborted
    }

    public void Dispose()
    {
        _snStore.Dispose(); // 停后台落库任务，防测试进程残留
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task GetCurrentSnapshots_PublishedSnapshot_Returned()
    {
        _aggregator.Publish(new DeviceSnapshotDto
        {
            DeviceId = "dev-1",
            DeviceName = "注塑机1",
            Status = DeviceStatus.Running,
            TotalOkProduction = 123,
        });

        var snapshots = await _hub.GetCurrentSnapshotsAsync();

        var snap = Assert.Single(snapshots);
        Assert.Equal("dev-1", snap.DeviceId);
        Assert.Equal(123, snap.TotalOkProduction);
    }

    [Fact]
    public async Task GetCurrentSnapshots_Empty_ReturnsEmptyList()
    {
        Assert.Empty(await _hub.GetCurrentSnapshotsAsync());
    }

    [Fact]
    public async Task QueryHistory_NoData_ReturnsSuccessEmptyResponse()
    {
        var response = await _hub.QueryHistoryAsync(new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.ProductionLog,
            From = DateTime.Now.AddHours(-1),
            To = DateTime.Now,
        });

        Assert.True(string.IsNullOrEmpty(response.Error));
        Assert.Empty(response.ProductionLogs);
    }

    [Fact]
    public async Task QuerySnEvents_BySn_ReturnsMappedEvents()
    {
        // 写入两条 SN 事件（Append 入队，查询时同步排空落库）
        _snStore.Append(new Kanban.Collector.Core.Entities.SnEventRecord
        {
            Sn = "SN-UNIT-001",
            DeviceId = "dev-1",
            DeviceName = "注塑机1",
            WorkOrderId = 7,
            ShiftName = "白班",
            Result = 0,
            Timestamp = DateTime.Now,
        });
        _snStore.Append(new Kanban.Collector.Core.Entities.SnEventRecord
        {
            Sn = "SN-UNIT-002",
            DeviceId = "dev-1",
            DeviceName = "注塑机1",
            WorkOrderId = 7,
            ShiftName = "白班",
            Result = 1,
            Timestamp = DateTime.Now.AddMinutes(1),
        });

        var response = await _hub.QuerySnEventsAsync(new SnEventQueryRequest { Sn = "SN-UNIT-001" });

        var record = Assert.Single(response.Items);
        Assert.Equal("SN-UNIT-001", record.Sn);
        Assert.Equal(7, record.WorkOrderId);
        Assert.Equal("白班", record.ShiftName);
        Assert.Equal(0, record.Result);
        Assert.Equal(1, response.Total);
    }

    [Fact]
    public async Task QuerySnEvents_ByWorkOrder_Paginates()
    {
        for (var i = 0; i < 5; i++)
        {
            _snStore.Append(new Kanban.Collector.Core.Entities.SnEventRecord
            {
                Sn = $"SN-WO-{i:D3}",
                DeviceId = "dev-2",
                DeviceName = "注塑机2",
                WorkOrderId = 42,
                ShiftName = "夜班",
                Result = 0,
                Timestamp = DateTime.Now.AddMinutes(i),
            });
        }

        var page1 = await _hub.QuerySnEventsAsync(new SnEventQueryRequest { WorkOrderId = 42, Page = 1, PageSize = 3 });
        Assert.Equal(5, page1.Total);
        Assert.Equal(3, page1.Items.Count);

        var page2 = await _hub.QuerySnEventsAsync(new SnEventQueryRequest { WorkOrderId = 42, Page = 2, PageSize = 3 });
        Assert.Equal(5, page2.Total);
        Assert.Equal(2, page2.Items.Count);
    }

    [Fact]
    public async Task QuerySnEvents_UnknownSn_ReturnsEmpty()
    {
        var response = await _hub.QuerySnEventsAsync(new SnEventQueryRequest { Sn = "SN-DOES-NOT-EXIST" });
        Assert.Equal(0, response.Total);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task GetShiftProgress_FullDayShift_IsInShift()
    {
        // 24 小时全覆盖班次 → 任意时刻均在班次内，断言确定
        lock (_settings.ShiftsLock)
        {
            _settings.Shifts.Clear();
            _settings.Shifts.Add(new Kanban.Collector.Core.Models.ShiftConfig
            {
                Name = "全天班",
                StartTime = TimeSpan.Zero,
                EndTime = new TimeSpan(23, 59, 59),
            });
        }

        var progress = await _hub.GetShiftProgressAsync();

        Assert.True(progress.IsInShift);
        Assert.Equal("全天班", progress.Name);
        Assert.InRange(progress.Ratio, 0.0, 1.0);
    }

    [Fact]
    public async Task GetCollectorSettings_ReflectsCurrentSettings()
    {
        _settings.PlcConfig.IpAddress = "10.1.2.3";
        _settings.PollingIntervalMs = 300;
        _settings.AppTitle = "一号车间";

        var dto = await _hub.GetCollectorSettingsAsync();

        Assert.Equal("10.1.2.3", dto.PlcIpAddress);
        Assert.Equal(300, dto.PollingIntervalMs);
        Assert.NotNull(dto.Shifts);
    }

    [Fact]
    public async Task GetWorkOrderProductionSummary_UnknownWorkOrder_ReturnsEmpty()
    {
        var summary = await _hub.GetWorkOrderProductionSummaryAsync(9999);
        Assert.Equal(0, summary.OkCount);
        Assert.Equal(0, summary.NgCount);
    }

    [Fact]
    public async Task GetDevices_GetWorkOrders_GetCurrentWorkOrder_EmptyRepos()
    {
        Assert.Empty(await _hub.GetDevicesAsync());
        Assert.Empty(await _hub.GetWorkOrdersAsync());
        Assert.Null(await _hub.GetCurrentWorkOrderAsync("dev-1"));
    }

    [Fact]
    public async Task GetTitle_GetLanguage_GetServerVersion_ReflectSettings()
    {
        _settings.AppTitle = "三号车间看板";
        _settings.Language = AppLanguage.En;

        Assert.Equal("三号车间看板", await _hub.GetTitleAsync());
        Assert.Equal((int)AppLanguage.En, await _hub.GetLanguageAsync());
        Assert.Equal("en-US", await _hub.GetLanguageCodeAsync());
        Assert.False(string.IsNullOrEmpty(await _hub.GetServerVersionAsync()));
    }
}
