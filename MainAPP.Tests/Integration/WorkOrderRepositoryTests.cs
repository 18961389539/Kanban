using System.IO;
using System.Linq;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Tests;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// WorkOrderRepository 集成测试：使用临时 SQLite 数据库验证 CRUD + 查询。
/// 参照 DeviceRepositoryRoundTripTests 模式：临时目录隔离 + IDisposable 清理。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","Database")]
public class WorkOrderRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _dbProvider;

    public WorkOrderRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _dbProvider = new DatabaseProvider(_appSettings);
        _dbProvider.EnsureCreatedAll();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void LoadAll_EmptyDatabase_ReturnsEmptyCollection()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo.LoadAll();
        Assert.Empty(repo.WorkOrders);
    }

    [Fact]
    public void LoadAll_AfterUpsert_ReturnsWorkOrders()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1"));
        repo.Upsert(CreateWorkOrder(orderNo: "WO-002", deviceId: "D2"));

        var repo2 = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo2.LoadAll();

        Assert.Equal(2, repo2.WorkOrders.Count);
    }

    [Fact]
    public void Upsert_NewWorkOrder_AssignsIdAndAddsToCollection()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        var wo = CreateWorkOrder(orderNo: "WO-001", deviceId: "D1");

        var saved = repo.Upsert(wo);

        Assert.NotEqual(0, saved.Id);
        Assert.Single(repo.WorkOrders);
        Assert.Equal("WO-001", repo.WorkOrders[0].OrderNo);
    }

    [Fact]
    public void Upsert_ExistingWorkOrder_UpdatesFieldsInDatabase()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        var saved = repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1", targetQty: 1000));

        saved.TargetQuantity = 2000;
        saved.ProductName = "更新后产品";
        repo.Upsert(saved);

        var repo2 = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo2.LoadAll();
        var loaded = Assert.Single(repo2.WorkOrders);
        Assert.Equal(2000, loaded.TargetQuantity);
        Assert.Equal("更新后产品", loaded.ProductName);
    }

    [Fact]
    public void Delete_RemovesFromDatabaseAndCollection()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        var saved = repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1"));

        repo.Delete(saved.Id);

        Assert.Empty(repo.WorkOrders);

        var repo2 = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo2.LoadAll();
        Assert.Empty(repo2.WorkOrders);
    }

    [Fact]
    public void Delete_NonExistentId_DoesNotThrow()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        var ex = Record.Exception(() => repo.Delete(99999));
        Assert.Null(ex);
    }

    [Fact]
    public void GetRunningByDevice_ReturnsRunningWorkOrder()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1", status: WorkOrderStatus.Pending));
        repo.Upsert(CreateWorkOrder(orderNo: "WO-002", deviceId: "D1", status: WorkOrderStatus.Running));
        repo.Upsert(CreateWorkOrder(orderNo: "WO-003", deviceId: "D2", status: WorkOrderStatus.Running));

        var running = repo.GetRunningByDevice("D1");

        Assert.NotNull(running);
        Assert.Equal("WO-002", running!.OrderNo);
    }

    [Fact]
    public void GetRunningByDevice_NoRunningWorkOrder_ReturnsNull()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1", status: WorkOrderStatus.Pending));

        var running = repo.GetRunningByDevice("D1");
        Assert.Null(running);
    }

    [Fact]
    public void GetRunningByDevice_NoWorkOrderForDevice_ReturnsNull()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1"));

        var running = repo.GetRunningByDevice("D2");
        Assert.Null(running);
    }

    [Fact]
    public void GetLatestPendingByDevice_ReturnsEarliestPlannedStart()
    {
        var now = DateTime.Now;
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1",
            status: WorkOrderStatus.Pending, plannedStart: now.AddHours(2)));
        repo.Upsert(CreateWorkOrder(orderNo: "WO-002", deviceId: "D1",
            status: WorkOrderStatus.Pending, plannedStart: now.AddHours(1)));
        repo.Upsert(CreateWorkOrder(orderNo: "WO-003", deviceId: "D1",
            status: WorkOrderStatus.Running, plannedStart: now.AddHours(0.5)));

        var pending = repo.GetLatestPendingByDevice("D1");

        Assert.NotNull(pending);
        Assert.Equal("WO-002", pending!.OrderNo);
    }

    [Fact]
    public void GetLatestPendingByDevice_NoPendingWorkOrder_ReturnsNull()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1", status: WorkOrderStatus.Running));

        var pending = repo.GetLatestPendingByDevice("D1");
        Assert.Null(pending);
    }

    [Fact]
    public void GetSnapshot_ReturnsCopyNotReference()
    {
        var repo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        repo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1"));

        var snapshot1 = repo.GetSnapshot();
        var snapshot2 = repo.GetSnapshot();

        Assert.NotSame(snapshot1, snapshot2);
        Assert.Single(snapshot1);
    }

    /// <summary>创建测试用工单实体。</summary>
    private static WorkOrder CreateWorkOrder(
        string orderNo,
        string deviceId,
        int targetQty = 1000,
        WorkOrderStatus status = WorkOrderStatus.Pending,
        DateTime? plannedStart = null)
    {
        return new WorkOrder
        {
            OrderNo = orderNo,
            ProductCode = "P-001",
            ProductName = "测试产品",
            DeviceId = deviceId,
            DeviceName = "设备" + deviceId,
            TargetQuantity = targetQty,
            PlannedStart = plannedStart ?? DateTime.Now,
            PlannedEnd = (plannedStart ?? DateTime.Now).AddHours(8),
            Status = status,
        };
    }
}
