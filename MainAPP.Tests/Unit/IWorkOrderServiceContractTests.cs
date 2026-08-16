using System.IO;
using System.Windows;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IWorkOrderService 契约测试：验证 <see cref="WorkOrderService"/> 生产实现对接口契约的遵守。
///
/// 使用真实 Repository + 临时 SQLite 数据库 + FakeDialogService + InMemoryHistoryService，
/// 与 <see cref="Integration.WorkOrderManagerViewModelTests"/> 相同的隔离模式，
/// 保证测试行为与生产一致（避免 NSubstitute 无法 mock 非 virtual 方法的限制）。
///
/// 契约覆盖：
/// - <see cref="IWorkOrderService.GetAvailableDevices"/> 返回非 null 列表
/// - <see cref="IWorkOrderService.Clone"/> 返回值相等但引用不同的副本
/// - <see cref="IWorkOrderService.AddWorkOrder"/> 用户取消返回 null；成功返回已保存实体
/// - <see cref="IWorkOrderService.DeleteWorkOrder"/> 用户取消返回 false；成功返回 true
/// - <see cref="IWorkOrderService.StartWorkOrder"/> 状态机违反返回 null；成功返回已更新实体
/// - <see cref="IWorkOrderService.GetProductionSummary"/> 始终返回非 null Summary（即使无数据）
///
/// 若未来引入其他 IWorkOrderService 实现（如基于远程 API 的 RemoteWorkOrderService），
/// 应将其加入 <see cref="Implementations"/> 列表，自动跑同一组用例。
/// </summary>
[Trait("Category","Contract")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class IWorkOrderServiceContractTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _dbProvider;
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly DeviceRepository _deviceRepo;
    private readonly InMemoryHistoryService _historyService;

    public IWorkOrderServiceContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanContractTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _dbProvider = new DatabaseProvider(_appSettings);
        _dbProvider.EnsureCreatedAll();
        _workOrderRepo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        _deviceRepo = new DeviceRepository(_appSettings);
        _historyService = new InMemoryHistoryService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// 所有应通过契约测试的 IWorkOrderService 实现工厂。
    /// 使用 TheoryData&lt;T&gt; 强类型成员数据：编译期检查工厂类型，避免运行时 InvalidCastException；
    /// 且测试名显示为工厂类型而非 object[]，可读性更好。
    /// </summary>
    public static TheoryData<Func<IWorkOrderServiceContractTests, IWorkOrderService>> Implementations => new()
    {
        // 每个工厂接收 IWorkOrderServiceContractTests 实例以访问共享依赖
        t => t.CreateService(),
    };

    /// <summary>创建注入默认 FakeDialogService 的 WorkOrderService。</summary>
    private IWorkOrderService CreateService(FakeDialogService? dialog = null)
        => new WorkOrderService(_workOrderRepo, _deviceRepo, dialog ?? new FakeDialogService(), _historyService);

    // ──────────── 接口契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Implements_IWorkOrderService(Func<IWorkOrderServiceContractTests, IWorkOrderService> factory)
    {
        var svc = factory(this);
        Assert.IsAssignableFrom<IWorkOrderService>(svc);
    }

    // ──────────── GetAvailableDevices 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void GetAvailableDevices_ReturnsNonNullList(Func<IWorkOrderServiceContractTests, IWorkOrderService> factory)
    {
        var svc = factory(this);
        var devices = svc.GetAvailableDevices();
        Assert.NotNull(devices);
    }

    // ──────────── Clone 契约 ────────────

    [Fact]
    public void Clone_ReturnsDifferentReference()
    {
        var svc = CreateService();
        var original = new WorkOrder { OrderNo = "WO-001", ProductName = "产品A" };
        var clone = svc.Clone(original);
        Assert.NotSame(original, clone);
        Assert.Equal(original.OrderNo, clone.OrderNo);
        Assert.Equal(original.ProductName, clone.ProductName);
    }

    [Fact]
    public void Clone_PreservesAllFields()
    {
        var svc = CreateService();
        var original = new WorkOrder
        {
            Id = 42,
            OrderNo = "WO-001",
            ProductCode = "P-A",
            ProductName = "产品A",
            DeviceId = "dev-1",
            DeviceName = "设备1",
            TargetQuantity = 1000,
            Status = WorkOrderStatus.Running,
            CompletedOkCount = 500,
            CompletedNgCount = 10,
        };
        var clone = svc.Clone(original);
        Assert.Equal(42, clone.Id);
        Assert.Equal("WO-001", clone.OrderNo);
        Assert.Equal(WorkOrderStatus.Running, clone.Status);
        Assert.Equal(500, clone.CompletedOkCount);
        Assert.Equal(10, clone.CompletedNgCount);
    }

    // ──────────── AddWorkOrder 契约 ────────────

    [Fact]
    public void AddWorkOrder_WhenUserCancels_ReturnsNull()
    {
        var svc = CreateService(new FakeDialogService { WorkOrderEditorResult = null });
        var result = svc.AddWorkOrder(null);
        Assert.Null(result);
    }

    [Fact]
    public void AddWorkOrder_WhenUserConfirms_ReturnsSavedEntity()
    {
        var savedWo = new WorkOrder { OrderNo = "WO-NEW", ProductName = "新产品", DeviceId = "dev-1" };
        var svc = CreateService(new FakeDialogService { WorkOrderEditorResult = savedWo });
        var result = svc.AddWorkOrder(null);
        Assert.NotNull(result);
        Assert.Equal("WO-NEW", result!.OrderNo);
    }

    // ──────────── DeleteWorkOrder 契约 ────────────

    [Fact]
    public void DeleteWorkOrder_WhenUserCancels_ReturnsFalse()
    {
        var svc = CreateService(new FakeDialogService { ShowResult = MessageBoxResult.No });
        var target = new WorkOrder { OrderNo = "WO-001", Id = 1 };
        var result = svc.DeleteWorkOrder(target);
        Assert.False(result);
    }

    [Fact]
    public void DeleteWorkOrder_WhenUserConfirms_ReturnsTrue()
    {
        // 先插入一条工单用于删除
        var wo = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-DEL", ProductName = "删除测试", DeviceId = "dev-1" });
        var svc = CreateService(new FakeDialogService { ShowResult = MessageBoxResult.Yes });
        var result = svc.DeleteWorkOrder(wo);
        Assert.True(result);
    }

    // ──────────── StartWorkOrder 状态机契约 ────────────

    [Fact]
    public void StartWorkOrder_WhenNotPending_ReturnsNull()
    {
        var svc = CreateService();
        var target = new WorkOrder { OrderNo = "WO-001", Status = WorkOrderStatus.Completed };
        var result = svc.StartWorkOrder(target);
        Assert.Null(result);
    }

    [Fact]
    public void StartWorkOrder_WhenPending_ReturnsRunningEntity()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-START", Status = WorkOrderStatus.Pending, DeviceId = "dev-1" });
        var svc = CreateService();
        var result = svc.StartWorkOrder(wo);
        Assert.NotNull(result);
        Assert.Equal(WorkOrderStatus.Running, result!.Status);
    }

    [Fact]
    public void StartWorkOrder_WhenSameDeviceHasRunning_ReturnsNull()
    {
        // 先插入一条 Running 工单
        _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-RUNNING", Status = WorkOrderStatus.Running, DeviceId = "dev-1" });
        var wo2 = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-PENDING", Status = WorkOrderStatus.Pending, DeviceId = "dev-1" });
        var svc = CreateService();
        var result = svc.StartWorkOrder(wo2);
        Assert.Null(result);  // 同设备冲突
    }

    // ──────────── CompleteWorkOrder 状态机契约 ────────────

    [Fact]
    public void CompleteWorkOrder_WhenNotRunning_ReturnsNull()
    {
        var svc = CreateService();
        var target = new WorkOrder { OrderNo = "WO-001", Status = WorkOrderStatus.Pending };
        var result = svc.CompleteWorkOrder(target);
        Assert.Null(result);
    }

    [Fact]
    public void CompleteWorkOrder_WhenRunning_ReturnsCompletedEntity()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-COMP", Status = WorkOrderStatus.Running, DeviceId = "dev-1" });
        var svc = CreateService();
        var result = svc.CompleteWorkOrder(wo);
        Assert.NotNull(result);
        Assert.Equal(WorkOrderStatus.Completed, result!.Status);
    }

    // ──────────── AbortWorkOrder 状态机契约 ────────────

    [Fact]
    public void AbortWorkOrder_WhenAlreadyAborted_ReturnsNull()
    {
        var svc = CreateService(new FakeDialogService { ShowResult = MessageBoxResult.Yes });
        var target = new WorkOrder { OrderNo = "WO-001", Status = WorkOrderStatus.Aborted };
        var result = svc.AbortWorkOrder(target);
        Assert.Null(result);
    }

    [Fact]
    public void AbortWorkOrder_WhenUserCancels_ReturnsNull()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-ABT", Status = WorkOrderStatus.Running, DeviceId = "dev-1" });
        var svc = CreateService(new FakeDialogService { ShowResult = MessageBoxResult.No });
        var result = svc.AbortWorkOrder(wo);
        Assert.Null(result);
    }

    [Fact]
    public void AbortWorkOrder_WhenRunningAndUserConfirms_ReturnsAbortedEntity()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-ABT2", Status = WorkOrderStatus.Running, DeviceId = "dev-1" });
        var svc = CreateService(new FakeDialogService { ShowResult = MessageBoxResult.Yes });
        var result = svc.AbortWorkOrder(wo);
        Assert.NotNull(result);
        Assert.Equal(WorkOrderStatus.Aborted, result!.Status);
    }

    [Fact]
    public void AbortWorkOrder_WhenPendingAndUserConfirms_ReturnsAbortedEntity()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-ABT3", Status = WorkOrderStatus.Pending, DeviceId = "dev-1" });
        var svc = CreateService(new FakeDialogService { ShowResult = MessageBoxResult.Yes });
        var result = svc.AbortWorkOrder(wo);
        Assert.NotNull(result);
        Assert.Equal(WorkOrderStatus.Aborted, result!.Status);
    }

    // ──────────── GetProductionSummary 契约 ────────────

    [Fact]
    public void GetProductionSummary_AlwaysReturnsNonNull()
    {
        var svc = CreateService();
        var wo = new WorkOrder { Id = 1, Status = WorkOrderStatus.Pending, DeviceId = "dev-1" };
        var summary = svc.GetProductionSummary(wo);
        Assert.NotNull(summary);
    }

    [Fact]
    public void GetProductionSummary_WithCompletedSnapshot_ReturnsSnapshot()
    {
        var svc = CreateService();
        var wo = new WorkOrder
        {
            Id = 1,
            Status = WorkOrderStatus.Completed,
            CompletedOkCount = 800,
            CompletedNgCount = 20,
            TargetQuantity = 1000,
        };
        var summary = svc.GetProductionSummary(wo);
        Assert.Equal(800, summary.OkCount);
        Assert.Equal(20, summary.NgCount);
        Assert.Equal(820, summary.TotalCount);
        // 达成率 = 800/1000 = 0.8
        Assert.InRange(summary.AchievementRate, 0.79, 0.81);
    }

    [Fact]
    public void GetProductionSummary_WithZeroTarget_AchievementRateIsZero()
    {
        var svc = CreateService();
        var wo = new WorkOrder
        {
            Id = 1,
            Status = WorkOrderStatus.Completed,
            CompletedOkCount = 100,
            CompletedNgCount = 0,
            TargetQuantity = 0,  // 目标为 0
        };
        var summary = svc.GetProductionSummary(wo);
        Assert.Equal(0, summary.AchievementRate);
    }
}
