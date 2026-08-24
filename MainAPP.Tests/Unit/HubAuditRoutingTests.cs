using System.IO;
using Kanban.Collector.Hubs;
using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>审计静态门面（AuditLog）是进程级状态：操作它的测试类必须串行执行，避免互相 Reset。</summary>
[CollectionDefinition("AuditLogState", DisableParallelization = true)]
public sealed class AuditLogStateCollection;

/// <summary>
/// Remote 审计路由回归测试：Collector 侧 AuditLog 门面接通（CollectorWorker 初始化）
/// + KanbanAdminHub 管理写接口（设备/工单/采集设置）触发审计落库。
/// 历史修复背景：Hub 此前无任何 Audit 调用，Remote 模式管理操作零审计。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Collection("AuditLogState")]
public sealed class HubAuditRoutingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly AuditService _audit;
    private IServiceProvider? _serviceProvider;

    public HubAuditRoutingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AuditHubTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings { ConfigDirectory = _tempDir };
        var db = new DatabaseProvider(_settings);
        db.EnsureCreatedAll(); // 建审计表（AuditEntries），否则 QueryAll 报 no such table
        _audit = new AuditService(db, NullLogger<AuditService>.Instance);
        AuditLog.Initialize(_audit);
    }

    public void Dispose()
    {
        AuditLog.ResetForTest();
        _audit.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>等待审计队列异步落库（AuditService 后台 flush，最多 3 秒）。</summary>
    private async Task WaitFlushAsync(int expected)
    {
        for (var i = 0; i < 30 && _audit.FlushedCount < expected; i++)
            await Task.Delay(100);
        Assert.True(_audit.FlushedCount >= expected, $"审计落库超时: Flushed={_audit.FlushedCount} 期望={expected}");
    }

    private KanbanAdminHub CreateHub()
    {
        var db = new DatabaseProvider(_settings);
        var services = new ServiceCollection();
        services.AddSingleton(_settings);
        _serviceProvider = services.BuildServiceProvider();

        var aggregator = new SnapshotAggregator();
        var deviceRepo = new DeviceRepository(_settings);
        var workOrderRepo = new WorkOrderRepository(db, Substitute.For<AutoMapper.IMapper>());
        var configSync = new ConfigSyncHandler(
            deviceRepo, workOrderRepo, aggregator, _settings, _serviceProvider, NullLogger<ConfigSyncHandler>.Instance,
            null!, null!); // IRecipeStore/RecipeApplier：本测试只验证写接口审计路由，不触达配方

        return new KanbanAdminHub(configSync, _audit, NullLogger<KanbanAdminHub>.Instance);
    }

    [Fact]
    public async Task SaveDevices_RecordsAudit()
    {
        var hub = CreateHub();
        await hub.SaveDevicesAsync(
        [
            new DeviceConfigDto { Id = "dev-1", Name = "注塑机1", OkCountAddress = "D100", NgCountAddress = "D102", StatusCountAddress = "D104", ProductionResetAddress = "D106", TargetCycle = 600, RecipeName = "A", RecipeAddress = "D110" },
        ]);

        await WaitFlushAsync(1);
        var (items, _) = _audit.QueryAll(DateTime.MinValue, DateTime.MaxValue, null, "Device.SaveBatch", null, null);
        Assert.NotEmpty(items);
        Assert.Equal("Device", items[0].TargetType);
        Assert.Contains("1 台", items[0].Detail ?? string.Empty);
    }

    [Fact]
    public async Task UpsertAndDeleteWorkOrder_RecordsAudit()
    {
        var hub = CreateHub();
        var dto = new WorkOrderDto
        {
            Id = 0,
            OrderNo = "WO-TEST-001",
            ProductCode = "P-1",
            ProductName = "产品1",
            DeviceId = "dev-1",
            DeviceName = "注塑机1",
            TargetQuantity = 100,
            PlannedStart = DateTime.Now,
            PlannedEnd = DateTime.Now.AddHours(8),
            Status = WorkOrderStatus.Pending,
            CreatedAt = DateTime.Now,
        };

        var saved = await hub.UpsertWorkOrderAsync(dto);
        await WaitFlushAsync(1);
        var (upserts, _) = _audit.QueryAll(DateTime.MinValue, DateTime.MaxValue, null, "WorkOrder.Upsert", null, null);
        Assert.NotEmpty(upserts);
        Assert.Equal("新增工单", upserts[0].Detail);
        Assert.Contains("WO-TEST-001", upserts[0].AfterJson ?? string.Empty);

        await hub.DeleteWorkOrderAsync(saved.Id);
        await WaitFlushAsync(2);
        var (deletes, _) = _audit.QueryAll(DateTime.MinValue, DateTime.MaxValue, null, "WorkOrder.Delete", null, null);
        Assert.NotEmpty(deletes);
        Assert.Equal(saved.Id.ToString(), deletes[0].TargetId);
    }

    [Fact]
    public async Task SaveCollectorSettings_RecordsAudit()
    {
        var hub = CreateHub();
        await hub.SaveCollectorSettingsAsync(new CollectorSettingsDto
        {
            PollingIntervalMs = 500,
            HistoryWriteIntervalScans = 25,
            PlcBrand = 1,
        });

        await WaitFlushAsync(1);
        var (items, _) = _audit.QueryAll(DateTime.MinValue, DateTime.MaxValue, null, "CollectorSettings.Update", null, null);
        Assert.NotEmpty(items);
        Assert.Contains("\"pollingIntervalMs\":500", items[0].AfterJson ?? string.Empty);
    }

    [Fact]
    public void AuditLog_NotInitialized_IsSilent()
    {
        AuditLog.ResetForTest();
        // 未初始化时调用不抛异常（门面容错契约）
        AuditLog.Record("Test.Action", "Test", null, detail: "should-be-ignored");
    }
}
