using System.IO;
using System.Windows;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// WorkOrderService 深度单元测试：聚焦契约测试未覆盖的边界场景。
///
/// 覆盖范围：
/// - 产量聚合的班次差分逻辑（多班次、单条记录基线查询）
/// - 完成时产量快照写入验证
/// - 中止时产量快照写入验证（仅 Running 状态写入，Pending 不写）
/// - GetProductionSummary 异常吞并（HistoryService 抛异常时返回空 Summary）
/// - EditWorkOrder 用户取消/确认路径
/// - 同设备 Running 冲突的边界（同工单 Id 不冲突）
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class WorkOrderServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _dbProvider;
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly DeviceRepository _deviceRepo;
    private readonly InMemoryHistoryService _historyService;

    public WorkOrderServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WoServiceTests_" + Guid.NewGuid().ToString("N"));
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

    private WorkOrderService CreateService(FakeDialogService? dialog = null)
        => new(_workOrderRepo, _deviceRepo, dialog ?? new FakeDialogService(), _historyService);

    // ──────────── 产量聚合：多班次差分 ────────────

    [Fact]
    public void GetProductionSummary_MultipleShifts_SumsDeltasAcrossShifts()
    {
        var svc = CreateService();
        var wo = _workOrderRepo.Upsert(new WorkOrder
        {
            OrderNo = "WO-MULTI",
            Status = WorkOrderStatus.Running,
            DeviceId = "dev-1",
            TargetQuantity = 1000,
        });

        // 班次 A：累计从 100 → 200（差分 100）
        // 班次 B：累计从 50 → 80（差分 30）
        // 总合格 = 130
        var now = DateTime.Now;
        _historyService.ProductionLogs.AddRange(new[]
        {
            new ProductionLog { DeviceId = "dev-1", ShiftName = "A", OkProduction = 100, NgProduction = 0, Timestamp = now.AddMinutes(-60), WorkOrderId = wo.Id },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "A", OkProduction = 200, NgProduction = 5, Timestamp = now.AddMinutes(-30), WorkOrderId = wo.Id },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "B", OkProduction = 50, NgProduction = 0, Timestamp = now.AddMinutes(-20), WorkOrderId = wo.Id },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "B", OkProduction = 80, NgProduction = 2, Timestamp = now.AddMinutes(-5), WorkOrderId = wo.Id },
        });

        var summary = svc.GetProductionSummary(wo);
        Assert.Equal(130, summary.OkCount);    // (200-100) + (80-50)
        Assert.Equal(7, summary.NgCount);       // (5-0) + (2-0)
    }

    [Fact]
    public void GetProductionSummary_SingleRecordPerShift_UsesBaselineBeforeWorkOrder()
    {
        var svc = CreateService();
        var wo = _workOrderRepo.Upsert(new WorkOrder
        {
            OrderNo = "WO-SINGLE",
            Status = WorkOrderStatus.Running,
            DeviceId = "dev-1",
            TargetQuantity = 1000,
        });

        var now = DateTime.Now;
        // 工单时间窗口内只有 1 条记录（OkProduction=150）
        _historyService.ProductionLogs.Add(new ProductionLog
        {
            DeviceId = "dev-1",
            ShiftName = "A",
            OkProduction = 150,
            NgProduction = 3,
            Timestamp = now.AddMinutes(-10),
            WorkOrderId = wo.Id,
        });
        // 工单开始前的基线（OkProduction=100）
        _historyService.ProductionLogs.Add(new ProductionLog
        {
            DeviceId = "dev-1",
            ShiftName = "A",
            OkProduction = 100,
            NgProduction = 0,
            Timestamp = now.AddMinutes(-30),
        });

        var summary = svc.GetProductionSummary(wo);
        // 单条记录：差分 = 末条 - 基线 = 150 - 100 = 50
        Assert.Equal(50, summary.OkCount);
        Assert.Equal(3, summary.NgCount);
    }

    [Fact]
    public void GetProductionSummary_SingleRecordNoBaseline_ReturnsAbsoluteValue()
    {
        var svc = CreateService();
        var wo = _workOrderRepo.Upsert(new WorkOrder
        {
            OrderNo = "WO-NOBASE",
            Status = WorkOrderStatus.Running,
            DeviceId = "dev-1",
            TargetQuantity = 1000,
        });

        var now = DateTime.Now;
        // 工单时间窗口内只有 1 条记录，无基线
        _historyService.ProductionLogs.Add(new ProductionLog
        {
            DeviceId = "dev-1",
            ShiftName = "A",
            OkProduction = 200,
            NgProduction = 10,
            Timestamp = now.AddMinutes(-5),
            WorkOrderId = wo.Id,
        });

        var summary = svc.GetProductionSummary(wo);
        // 无基线：直接取末条值
        Assert.Equal(200, summary.OkCount);
        Assert.Equal(10, summary.NgCount);
    }

    // ──────────── 完成时产量快照写入 ────────────

    [Fact]
    public void CompleteWorkOrder_WritesProductionSnapshot()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder
        {
            OrderNo = "WO-SNAP",
            Status = WorkOrderStatus.Running,
            DeviceId = "dev-1",
            TargetQuantity = 1000,
        });

        var now = DateTime.Now;
        _historyService.ProductionLogs.AddRange(new[]
        {
            new ProductionLog { DeviceId = "dev-1", ShiftName = "A", OkProduction = 100, NgProduction = 0, Timestamp = now.AddMinutes(-60), WorkOrderId = wo.Id },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "A", OkProduction = 300, NgProduction = 15, Timestamp = now.AddMinutes(-5), WorkOrderId = wo.Id },
        });

        var svc = CreateService();
        var result = svc.CompleteWorkOrder(wo);

        Assert.NotNull(result);
        Assert.Equal(WorkOrderStatus.Completed, result!.Status);
        Assert.Equal(200, result.CompletedOkCount);   // 300 - 100
        Assert.Equal(15, result.CompletedNgCount);
    }

    // ──────────── 中止时产量快照写入 ────────────

    [Fact]
    public void AbortWorkOrder_WhenRunning_WritesProductionSnapshot()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder
        {
            OrderNo = "WO-ABTSNAP",
            Status = WorkOrderStatus.Running,
            DeviceId = "dev-1",
            TargetQuantity = 1000,
        });

        var now = DateTime.Now;
        _historyService.ProductionLogs.AddRange(new[]
        {
            new ProductionLog { DeviceId = "dev-1", ShiftName = "A", OkProduction = 50, NgProduction = 0, Timestamp = now.AddMinutes(-30), WorkOrderId = wo.Id },
            new ProductionLog { DeviceId = "dev-1", ShiftName = "A", OkProduction = 250, NgProduction = 8, Timestamp = now.AddMinutes(-2), WorkOrderId = wo.Id },
        });

        var svc = CreateService(new FakeDialogService { ShowResult = MessageBoxResult.Yes });
        var result = svc.AbortWorkOrder(wo);

        Assert.NotNull(result);
        Assert.Equal(WorkOrderStatus.Aborted, result!.Status);
        Assert.Equal(200, result.CompletedOkCount);   // 250 - 50
        Assert.Equal(8, result.CompletedNgCount);
    }

    [Fact]
    public void AbortWorkOrder_WhenPending_DoesNotWriteSnapshot()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder
        {
            OrderNo = "WO-ABTPEND",
            Status = WorkOrderStatus.Pending,
            DeviceId = "dev-1",
        });

        var svc = CreateService(new FakeDialogService { ShowResult = MessageBoxResult.Yes });
        var result = svc.AbortWorkOrder(wo);

        Assert.NotNull(result);
        Assert.Equal(WorkOrderStatus.Aborted, result!.Status);
        // Pending 状态中止：未进入产量写入分支
        Assert.Null(result.CompletedOkCount);
        Assert.Null(result.CompletedNgCount);
    }

    // ──────────── GetProductionSummary 异常吞并 ────────────

    [Fact]
    public void GetProductionSummary_WhenHistoryServiceThrows_ReturnsEmptySummary()
    {
        var throwingHistory = new ThrowingHistoryService();
        var svc = new WorkOrderService(_workOrderRepo, _deviceRepo, new FakeDialogService(), throwingHistory);

        var wo = new WorkOrder
        {
            Id = 999,
            Status = WorkOrderStatus.Running,
            DeviceId = "dev-1",
        };

        // HistoryService 抛异常时，GetProductionSummary 内部 catch 吞并并返回空 Summary
        var summary = svc.GetProductionSummary(wo);
        Assert.NotNull(summary);
        Assert.Equal(0, summary.OkCount);
        Assert.Equal(0, summary.NgCount);
    }

    // ──────────── EditWorkOrder ────────────

    [Fact]
    public void EditWorkOrder_WhenUserCancels_ReturnsNull()
    {
        var svc = CreateService(new FakeDialogService { WorkOrderEditorResult = null });
        var source = new WorkOrder { OrderNo = "WO-EDIT", Status = WorkOrderStatus.Pending };
        var result = svc.EditWorkOrder(source);
        Assert.Null(result);
    }

    [Fact]
    public void EditWorkOrder_WhenUserConfirms_ReturnsUpdatedEntity()
    {
        var wo = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-EDIT2", ProductName = "旧名", DeviceId = "dev-1" });
        var edited = new WorkOrder { Id = wo.Id, OrderNo = "WO-EDIT2", ProductName = "新名", DeviceId = "dev-1" };
        var svc = CreateService(new FakeDialogService { WorkOrderEditorResult = edited });
        var result = svc.EditWorkOrder(wo);
        Assert.NotNull(result);
        Assert.Equal("新名", result!.ProductName);
    }

    // ──────────── 同设备 Running 冲突边界 ────────────

    [Fact]
    public void StartWorkOrder_SameWorkOrderId_DoesNotTriggerConflict()
    {
        // 同一工单 Id 不应被自己判定为冲突
        var wo = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "WO-SELF", Status = WorkOrderStatus.Pending, DeviceId = "dev-1" });
        // 模拟已 Running（实际 Start 后的状态）
        wo.Status = WorkOrderStatus.Running;
        _workOrderRepo.Upsert(wo);
        // 再次调用 Start（此时已经是 Running，应被状态机拒绝，而非冲突）
        var svc = CreateService();
        var result = svc.StartWorkOrder(wo);
        Assert.Null(result);  // 状态机拒绝（Running → Start 非法）
    }

    // ──────────── 辅助类 ────────────

    /// <summary>所有方法都抛异常的 IHistoryService，用于验证 GetProductionSummary 的异常吞并。</summary>
    private sealed class ThrowingHistoryService : IHistoryService
    {
        public List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
            => throw new InvalidOperationException("测试异常");
        public (List<ProductionLog> Items, int Total) QueryProductionLogsPaged(
            DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize)
            => throw new InvalidOperationException("测试异常");
        public ProductionLog? QueryLatestProductionLog(DateTime from, DateTime to, string? deviceId, string? shiftName)
            => throw new InvalidOperationException("测试异常");
        public List<ProductionLog> QueryProductionLogsByWorkOrder(int workOrderId)
            => throw new InvalidOperationException("测试异常");
        public ProductionLog? GetLatestProductionBefore(string deviceId, DateTime before, string shiftName)
            => throw new InvalidOperationException("测试异常");
        public List<StatusTransitionRecord> QueryStatusTransitions(string deviceId, DateTime from, DateTime to, string? shiftName = null)
            => throw new InvalidOperationException("测试异常");
        public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null)
            => throw new InvalidOperationException("测试异常");
        public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
            => throw new InvalidOperationException("测试异常");
        public Dictionary<string, List<ProductionLog>> QueryProductionLogsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
            => throw new InvalidOperationException("测试异常");
        public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
            => throw new InvalidOperationException("测试异常");
        public Dictionary<string, List<StatusTransitionRecord>> QueryStatusTransitionsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
            => throw new InvalidOperationException("测试异常");
        public AlarmEventRecord? GetLatestAlarmEvent(string alarmId)
            => throw new InvalidOperationException("测试异常");
        public void LogProduction(ProductionLog log) => throw new InvalidOperationException("测试异常");
        public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId, string alarmName, string plcAddress, AlarmEventType eventType, DateTime eventTime, string? shiftName = null)
            => throw new InvalidOperationException("测试异常");
        public bool LogStatusTransition(string deviceId, string deviceName, int previousState, int currentState, DateTime eventTime, string? shiftName = null)
            => throw new InvalidOperationException("测试异常");
    }
}
