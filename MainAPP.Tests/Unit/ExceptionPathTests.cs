using System.IO;
using AutoMapper;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 异常路径测试：AutoMapper 配置验证 + 工单清理任务。
///
/// 覆盖范围：
/// - MappingProfile.AssertConfigurationIsValid：启动时映射配置无误
/// - WorkOrder → WorkOrder 映射：忽略 Id/CreatedAt，拷贝其他字段
/// - WorkOrderRepository.CleanupOldWorkOrders：仅清理 Completed/Aborted 且超过保留期的工单
/// - CleanupOldWorkOrders 不影响 Pending/Running 工单
/// - CleanupOldWorkOrders 保留期内不清理
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ExceptionPathTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _dbProvider;
    private readonly WorkOrderRepository _workOrderRepo;

    public ExceptionPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ExcPathTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _dbProvider = new DatabaseProvider(_appSettings);
        _dbProvider.EnsureCreatedAll();
        _workOrderRepo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// 直接通过 EF Core 更新工单的 UpdatedAt（绕过 Upsert 强制设置 DateTime.Now 的行为），
    /// 用于构造"超过保留期"的测试场景。
    /// </summary>
    private void SetUpdatedAtDirectly(WorkOrder wo, DateTime updatedAt)
    {
        using var ctx = _dbProvider.CreateWorkOrderContext();
        var existing = ctx.WorkOrders.Find(wo.Id);
        if (existing != null)
        {
            existing.UpdatedAt = updatedAt;
            ctx.SaveChanges();
        }
    }

    // ──────────── AutoMapper 配置验证 ────────────

    [Fact]
    public void MappingProfile_ConfigurationIsValid()
    {
        // 应用启动时应立即验证 AutoMapper 配置，避免运行时映射失败
        var config = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>());
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void MappingProfile_WorkOrderToWorkOrder_PreservesFieldsExceptIdAndCreatedAt()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>());
        var mapper = config.CreateMapper();

        var source = new WorkOrder
        {
            Id = 42,
            OrderNo = "WO-001",
            ProductName = "产品A",
            DeviceId = "dev-1",
            Status = WorkOrderStatus.Running,
            CreatedAt = new DateTime(2025, 1, 1),
            UpdatedAt = new DateTime(2026, 7, 30),
            TargetQuantity = 1000,
            CompletedOkCount = 500,
            CompletedNgCount = 10,
        };
        var target = new WorkOrder { Id = 0, CreatedAt = default };

        mapper.Map(source, target);

        // Id 和 CreatedAt 被忽略（不覆盖）
        Assert.Equal(0, target.Id);
        Assert.Equal(default, target.CreatedAt);
        // 其他字段已拷贝
        Assert.Equal("WO-001", target.OrderNo);
        Assert.Equal("产品A", target.ProductName);
        Assert.Equal("dev-1", target.DeviceId);
        Assert.Equal(WorkOrderStatus.Running, target.Status);
        Assert.Equal(new DateTime(2026, 7, 30), target.UpdatedAt);
        Assert.Equal(1000, target.TargetQuantity);
        Assert.Equal(500, target.CompletedOkCount);
        Assert.Equal(10, target.CompletedNgCount);
    }

    [Fact]
    public void TestMapper_Instance_ConfigurationIsValid()
    {
        // 验证测试项目共享的 TestMapper 实例配置正确
        TestMapper.Instance.ConfigurationProvider.AssertConfigurationIsValid();
    }

    // ──────────── 工单清理任务 ────────────

    [Fact]
    public void CleanupOldWorkOrders_RemovesCompletedOlderThanRetention()
    {
        // 插入一条 Completed 工单，UpdatedAt 在 400 天前
        var oldWo = new WorkOrder
        {
            OrderNo = "WO-OLD",
            ProductName = "旧工单",
            DeviceId = "dev-1",
            Status = WorkOrderStatus.Completed,
        };
        var inserted = _workOrderRepo.Upsert(oldWo);
        // Upsert 会强制设置 UpdatedAt = DateTime.Now，需绕过它直接写旧时间
        SetUpdatedAtDirectly(inserted, DateTime.Now.AddDays(-400));

        var removed = _workOrderRepo.CleanupOldWorkOrders(retentionDays: 365);

        Assert.Equal(1, removed);
        Assert.Empty(_workOrderRepo.WorkOrders);
    }

    [Fact]
    public void CleanupOldWorkOrders_RemovesAbortedOlderThanRetention()
    {
        var oldWo = new WorkOrder
        {
            OrderNo = "WO-ABORTED",
            ProductName = "中止工单",
            DeviceId = "dev-1",
            Status = WorkOrderStatus.Aborted,
        };
        var inserted = _workOrderRepo.Upsert(oldWo);
        SetUpdatedAtDirectly(inserted, DateTime.Now.AddDays(-500));

        var removed = _workOrderRepo.CleanupOldWorkOrders(365);

        Assert.Equal(1, removed);
    }

    [Fact]
    public void CleanupOldWorkOrders_DoesNotRemovePending()
    {
        var oldPending = new WorkOrder
        {
            OrderNo = "WO-PENDING",
            ProductName = "待开始",
            DeviceId = "dev-1",
            Status = WorkOrderStatus.Pending,
            UpdatedAt = DateTime.Now.AddDays(-400),  // 超过保留期但状态不对
        };
        _workOrderRepo.Upsert(oldPending);

        var removed = _workOrderRepo.CleanupOldWorkOrders(365);

        Assert.Equal(0, removed);
        Assert.Single(_workOrderRepo.WorkOrders);
    }

    [Fact]
    public void CleanupOldWorkOrders_DoesNotRemoveRunning()
    {
        var oldRunning = new WorkOrder
        {
            OrderNo = "WO-RUNNING",
            ProductName = "进行中",
            DeviceId = "dev-1",
            Status = WorkOrderStatus.Running,
            UpdatedAt = DateTime.Now.AddDays(-400),
        };
        _workOrderRepo.Upsert(oldRunning);

        var removed = _workOrderRepo.CleanupOldWorkOrders(365);

        Assert.Equal(0, removed);
        Assert.Single(_workOrderRepo.WorkOrders);
    }

    [Fact]
    public void CleanupOldWorkOrders_DoesNotRemoveRecentCompleted()
    {
        var recentCompleted = new WorkOrder
        {
            OrderNo = "WO-RECENT",
            ProductName = "近期完成",
            DeviceId = "dev-1",
            Status = WorkOrderStatus.Completed,
            UpdatedAt = DateTime.Now.AddDays(-30),  // 保留期内
        };
        _workOrderRepo.Upsert(recentCompleted);

        var removed = _workOrderRepo.CleanupOldWorkOrders(365);

        Assert.Equal(0, removed);
        Assert.Single(_workOrderRepo.WorkOrders);
    }

    [Fact]
    public void CleanupOldWorkOrders_MixedBatch_RemovesOnlyEligible()
    {
        var now = DateTime.Now;
        var oldComp = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "OLD-COMP", Status = WorkOrderStatus.Completed, DeviceId = "d1" });
        var oldAbort = _workOrderRepo.Upsert(new WorkOrder { OrderNo = "OLD-ABORT", Status = WorkOrderStatus.Aborted, DeviceId = "d1" });
        _workOrderRepo.Upsert(new WorkOrder { OrderNo = "OLD-PEND", Status = WorkOrderStatus.Pending, DeviceId = "d1", UpdatedAt = now.AddDays(-400) });
        _workOrderRepo.Upsert(new WorkOrder { OrderNo = "NEW-COMP", Status = WorkOrderStatus.Completed, DeviceId = "d1", UpdatedAt = now.AddDays(-10) });
        _workOrderRepo.Upsert(new WorkOrder { OrderNo = "OLD-RUN", Status = WorkOrderStatus.Running, DeviceId = "d1", UpdatedAt = now.AddDays(-400) });
        // Upsert 会强制设置 UpdatedAt = DateTime.Now，需绕过它直接写旧时间
        SetUpdatedAtDirectly(oldComp, now.AddDays(-400));
        SetUpdatedAtDirectly(oldAbort, now.AddDays(-500));

        var removed = _workOrderRepo.CleanupOldWorkOrders(365);

        // 应删除 2 条（OLD-COMP + OLD-ABORT），保留 3 条
        Assert.Equal(2, removed);
        Assert.Equal(3, _workOrderRepo.WorkOrders.Count);
        Assert.DoesNotContain(_workOrderRepo.WorkOrders, w => w.OrderNo == "OLD-COMP");
        Assert.DoesNotContain(_workOrderRepo.WorkOrders, w => w.OrderNo == "OLD-ABORT");
    }

    [Fact]
    public void CleanupOldWorkOrders_WithNoWorkOrders_ReturnsZero()
    {
        var removed = _workOrderRepo.CleanupOldWorkOrders(365);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void CleanupOldWorkOrders_WithZeroRetention_RemovesAllCompletedAborted()
    {
        // retentionDays=0：所有已结束工单都应被清理（即使刚刚完成）
        var now = DateTime.Now;
        _workOrderRepo.Upsert(new WorkOrder { OrderNo = "JUST-COMP", Status = WorkOrderStatus.Completed, DeviceId = "d1", UpdatedAt = now });
        _workOrderRepo.Upsert(new WorkOrder { OrderNo = "JUST-ABORT", Status = WorkOrderStatus.Aborted, DeviceId = "d1", UpdatedAt = now });
        _workOrderRepo.Upsert(new WorkOrder { OrderNo = "RUNNING", Status = WorkOrderStatus.Running, DeviceId = "d1", UpdatedAt = now });

        var removed = _workOrderRepo.CleanupOldWorkOrders(0);

        Assert.Equal(2, removed);
        Assert.Single(_workOrderRepo.WorkOrders);
    }
}
