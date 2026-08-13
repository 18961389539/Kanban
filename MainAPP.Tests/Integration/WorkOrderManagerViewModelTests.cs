using System.IO;
using System.Linq;
using System.Windows;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.Tests;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// WorkOrderManagerViewModel 集成测试：验证命令逻辑 + 状态机转换。
/// 使用 FakeDialogService 模拟用户交互，临时 SQLite 数据库隔离。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","None")]
public class WorkOrderManagerViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _dbProvider;
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly DeviceRepository _deviceRepo;
    private readonly FakeDialogService _dialog;
    private readonly InMemoryHistoryService _historyService;

    public WorkOrderManagerViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _dbProvider = new DatabaseProvider(_appSettings);
        _dbProvider.EnsureCreatedAll();
        _workOrderRepo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        _deviceRepo = new DeviceRepository(_appSettings);
        _dialog = new FakeDialogService();
        _historyService = new InMemoryHistoryService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private WorkOrderManagerViewModel CreateVm()
    {
        var workOrderService = new WorkOrderService(_workOrderRepo, _deviceRepo, _dialog, _historyService);
        return new WorkOrderManagerViewModel(_workOrderRepo, workOrderService, _deviceRepo, _dialog, new UserSession());
    }

    private static WorkOrder CreateWorkOrder(
        string orderNo = "WO-001",
        string deviceId = "D1",
        WorkOrderStatus status = WorkOrderStatus.Pending)
    {
        return new WorkOrder
        {
            OrderNo = orderNo,
            ProductCode = "P-001",
            ProductName = "测试产品",
            DeviceId = deviceId,
            DeviceName = "设备" + deviceId,
            TargetQuantity = 1000,
            PlannedStart = DateTime.Now,
            PlannedEnd = DateTime.Now.AddHours(8),
            Status = status,
        };
    }

    // ──────────── Add 命令 ────────────

    [Fact]
    public void Add_DialogReturnsWorkOrder_PersistsAndNotifies()
    {
        var vm = CreateVm();
        var wo = CreateWorkOrder();
        _dialog.WorkOrderEditorResult = wo;

        vm.AddCommand.Execute(null);

        Assert.Single(_workOrderRepo.WorkOrders);
        Assert.Contains(_dialog.Success, s => s.Contains("已新增"));
        Assert.Equal(wo, vm.SelectedWorkOrder);
    }

    [Fact]
    public void Add_DialogReturnsNull_DoesNotPersist()
    {
        var vm = CreateVm();
        _dialog.WorkOrderEditorResult = null;

        vm.AddCommand.Execute(null);

        Assert.Empty(_workOrderRepo.WorkOrders);
        Assert.Empty(_dialog.Success);
    }

    // ──────────── Edit 命令 ────────────

    [Fact]
    public void Edit_NoSelection_CommandCannotExecute()
    {
        var vm = CreateVm();
        Assert.False(vm.EditCommand.CanExecute(null));
    }

    [Fact]
    public void Edit_DialogReturnsWorkOrder_UpdatesAndNotifies()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder());
        vm.SelectedWorkOrder = saved;

        var updated = CreateWorkOrder();
        updated.Id = saved.Id;
        updated.ProductName = "更新后产品";
        _dialog.WorkOrderEditorResult = updated;

        vm.EditCommand.Execute(null);

        Assert.Contains(_dialog.Success, s => s.Contains("已更新"));
        Assert.Equal("更新后产品", _workOrderRepo.WorkOrders[0].ProductName);
    }

    [Fact]
    public void Edit_DialogReturnsNull_DoesNotUpdate()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder());
        vm.SelectedWorkOrder = saved;
        _dialog.WorkOrderEditorResult = null;

        vm.EditCommand.Execute(null);

        Assert.Empty(_dialog.Success);
        Assert.Equal("测试产品", _workOrderRepo.WorkOrders[0].ProductName);
    }

    [Fact]
    public void Copy_ResetsExecutionState_AndSelectsNewWorkOrder()
    {
        var vm = CreateVm();
        var source = CreateWorkOrder(orderNo: "WO-DONE", status: WorkOrderStatus.Completed);
        source.CompletedOkCount = 900;
        source.CompletedNgCount = 10;
        var saved = _workOrderRepo.Upsert(source);
        vm.SelectedWorkOrder = saved;

        _dialog.WorkOrderEditorResult = new WorkOrder
        {
            Id = 0,
            OrderNo = "WO-DONE-COPY",
            ProductCode = saved.ProductCode,
            ProductName = saved.ProductName,
            DeviceId = saved.DeviceId,
            DeviceName = saved.DeviceName,
            TargetQuantity = saved.TargetQuantity,
            PlannedStart = saved.PlannedStart.AddDays(1),
            PlannedEnd = saved.PlannedEnd.AddDays(1),
            Status = WorkOrderStatus.Pending,
        };

        vm.CopyCommand.Execute(null);

        var copy = Assert.Single(_workOrderRepo.WorkOrders, w => w.OrderNo == "WO-DONE-COPY");
        Assert.NotEqual(saved.Id, copy.Id);
        Assert.Equal(WorkOrderStatus.Pending, copy.Status);
        Assert.Null(copy.CompletedOkCount);
        Assert.Null(copy.CompletedNgCount);
        Assert.Same(copy, vm.SelectedWorkOrder);
        Assert.Contains(_dialog.Success, message => message.Contains("已复制"));
    }

    [Fact]
    public void Copy_PassesFreshPendingTemplateToEditor()
    {
        var vm = CreateVm();
        var source = CreateWorkOrder(orderNo: "WO-RUN", status: WorkOrderStatus.Running);
        source.CompletedOkCount = 12;
        source.CompletedNgCount = 3;
        var saved = _workOrderRepo.Upsert(source);
        vm.SelectedWorkOrder = saved;
        _dialog.WorkOrderEditorResult = null;

        vm.CopyCommand.Execute(null);

        var template = Assert.Single(_dialog.WorkOrderEditorCalls).Template;
        Assert.NotNull(template);
        Assert.Equal(0, template.Id);
        Assert.Equal("WO-RUN-COPY", template.OrderNo);
        Assert.Equal(WorkOrderStatus.Pending, template.Status);
        Assert.Null(template.CompletedOkCount);
        Assert.Null(template.CompletedNgCount);
        Assert.Single(_workOrderRepo.WorkOrders);
        Assert.Same(saved, _workOrderRepo.WorkOrders[0]);
    }

    [Fact]
    public void Add_DuplicateOrderNo_IsRejected()
    {
        var vm = CreateVm();
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-DUP"));
        _dialog.WorkOrderEditorResult = CreateWorkOrder(orderNo: " wo-dup ");

        vm.AddCommand.Execute(null);

        Assert.Single(_workOrderRepo.WorkOrders);
        Assert.Contains(_dialog.Warning, message => message.Contains("已存在"));
    }

    [Fact]
    public void Add_OverlappingDeviceSchedule_IsRejected()
    {
        var vm = CreateVm();
        var existing = CreateWorkOrder(orderNo: "WO-EXIST");
        existing.PlannedStart = new DateTime(2026, 7, 31, 8, 0, 0);
        existing.PlannedEnd = new DateTime(2026, 7, 31, 16, 0, 0);
        _workOrderRepo.Upsert(existing);

        var candidate = CreateWorkOrder(orderNo: "WO-CONFLICT");
        candidate.PlannedStart = new DateTime(2026, 7, 31, 12, 0, 0);
        candidate.PlannedEnd = new DateTime(2026, 7, 31, 20, 0, 0);
        _dialog.WorkOrderEditorResult = candidate;

        vm.AddCommand.Execute(null);

        Assert.Single(_workOrderRepo.WorkOrders);
        Assert.Contains(_dialog.Warning, message => message.Contains("重叠"));
    }

    // ──────────── Delete 命令 ────────────

    [Fact]
    public void Delete_Confirmed_RemovesAndNotifies()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder());
        vm.SelectedWorkOrder = saved;
        _dialog.ShowResult = MessageBoxResult.Yes;

        vm.DeleteCommand.Execute(null);

        Assert.Empty(_workOrderRepo.WorkOrders);
        Assert.Contains(_dialog.Success, s => s.Contains("已删除"));
        Assert.Null(vm.SelectedWorkOrder);
    }

    [Fact]
    public void Delete_Cancelled_DoesNotRemove()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder());
        vm.SelectedWorkOrder = saved;
        _dialog.ShowResult = MessageBoxResult.No;

        vm.DeleteCommand.Execute(null);

        Assert.Single(_workOrderRepo.WorkOrders);
        Assert.Empty(_dialog.Success);
    }

    [Fact]
    public void Delete_NoSelection_CommandCannotExecute()
    {
        var vm = CreateVm();
        Assert.False(vm.DeleteCommand.CanExecute(null));
    }

    // ──────────── Start 命令 ────────────

    [Fact]
    public void Start_PendingWorkOrder_SetsRunning()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder(status: WorkOrderStatus.Pending));
        vm.SelectedWorkOrder = saved;

        vm.StartCommand.Execute(null);

        Assert.Equal(WorkOrderStatus.Running, _workOrderRepo.WorkOrders[0].Status);
        Assert.Contains(_dialog.Success, s => s.Contains("已开始"));
    }

    [Fact]
    public void Start_RunningWorkOrder_ShowsWarning()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder(status: WorkOrderStatus.Running));
        vm.SelectedWorkOrder = saved;

        vm.StartCommand.Execute(null);

        Assert.Contains(_dialog.Warning, w => w.Contains("Pending"));
        Assert.Empty(_dialog.Success);
    }

    [Fact]
    public void Start_DeviceAlreadyHasRunning_ShowsWarning()
    {
        var vm = CreateVm();
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1", status: WorkOrderStatus.Running));
        var pending = _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-002", deviceId: "D1", status: WorkOrderStatus.Pending));
        vm.SelectedWorkOrder = pending;

        vm.StartCommand.Execute(null);

        Assert.Contains(_dialog.Warning, w => w.Contains("进行中工单"));
    }

    [Fact]
    public void Start_NoSelection_CommandCannotExecute()
    {
        var vm = CreateVm();
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    // ──────────── Complete 命令 ────────────

    [Fact]
    public void Complete_RunningWorkOrder_SetsCompleted()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder(status: WorkOrderStatus.Running));
        vm.SelectedWorkOrder = saved;

        vm.CompleteCommand.Execute(null);

        Assert.Equal(WorkOrderStatus.Completed, _workOrderRepo.WorkOrders[0].Status);
        Assert.Contains(_dialog.Success, s => s.Contains("已完成"));
    }

    [Fact]
    public void Complete_PendingWorkOrder_ShowsWarning()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder(status: WorkOrderStatus.Pending));
        vm.SelectedWorkOrder = saved;

        vm.CompleteCommand.Execute(null);

        Assert.Contains(_dialog.Warning, w => w.Contains("Running"));
        Assert.Empty(_dialog.Success);
    }

    [Fact]
    public void Complete_NoSelection_CommandCannotExecute()
    {
        var vm = CreateVm();
        Assert.False(vm.CompleteCommand.CanExecute(null));
    }

    // ──────────── Abort 命令 ────────────

    [Fact]
    public void Abort_RunningWorkOrder_Confirmed_SetsAborted()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder(status: WorkOrderStatus.Running));
        vm.SelectedWorkOrder = saved;
        _dialog.ShowResult = MessageBoxResult.Yes;

        vm.AbortCommand.Execute(null);

        Assert.Equal(WorkOrderStatus.Aborted, _workOrderRepo.WorkOrders[0].Status);
        Assert.Contains(_dialog.Success, s => s.Contains("已中止"));
    }

    [Fact]
    public void Abort_RunningWorkOrder_Cancelled_DoesNotChange()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder(status: WorkOrderStatus.Running));
        vm.SelectedWorkOrder = saved;
        _dialog.ShowResult = MessageBoxResult.No;

        vm.AbortCommand.Execute(null);

        Assert.Equal(WorkOrderStatus.Running, _workOrderRepo.WorkOrders[0].Status);
        Assert.Empty(_dialog.Success);
    }

    [Fact]
    public void Abort_PendingWorkOrder_Confirmed_SetsAborted()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder(status: WorkOrderStatus.Pending));
        vm.SelectedWorkOrder = saved;
        _dialog.ShowResult = MessageBoxResult.Yes;

        vm.AbortCommand.Execute(null);

        Assert.Equal(WorkOrderStatus.Aborted, _workOrderRepo.WorkOrders[0].Status);
    }

    [Fact]
    public void Abort_CompletedWorkOrder_ShowsWarning()
    {
        var vm = CreateVm();
        var saved = _workOrderRepo.Upsert(CreateWorkOrder(status: WorkOrderStatus.Completed));
        vm.SelectedWorkOrder = saved;

        vm.AbortCommand.Execute(null);

        Assert.Contains(_dialog.Warning, w => w.Contains("Running/Pending"));
    }

    [Fact]
    public void Abort_NoSelection_CommandCannotExecute()
    {
        var vm = CreateVm();
        Assert.False(vm.AbortCommand.CanExecute(null));
    }

    // ──────────── 筛选 ────────────

    [Fact]
    public void FilteredView_StatusFilter_FiltersCorrectly()
    {
        var vm = CreateVm();
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-001", status: WorkOrderStatus.Pending));
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-002", status: WorkOrderStatus.Running));
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-003", status: WorkOrderStatus.Completed));

        vm.StatusFilter = "进行中";
        var filtered = vm.FilteredView.Cast<WorkOrder>().ToList();
        Assert.Single(filtered);
        Assert.Equal("WO-002", filtered[0].OrderNo);
    }

    [Fact]
    public void FilteredView_KeywordFilter_FiltersCorrectly()
    {
        var vm = CreateVm();
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-001"));
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-002"));

        vm.SearchKeyword = "WO-001";
        var filtered = vm.FilteredView.Cast<WorkOrder>().ToList();
        Assert.Single(filtered);
        Assert.Equal("WO-001", filtered[0].OrderNo);
    }

    [Fact]
    public void FilteredView_StatusAll_ShowsAllWorkOrders()
    {
        var vm = CreateVm();
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-001", status: WorkOrderStatus.Pending));
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-002", status: WorkOrderStatus.Running));

        vm.StatusFilter = "全部";
        var filtered = vm.FilteredView.Cast<WorkOrder>().ToList();
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void FilteredView_DefaultSortsByPlannedStart()
    {
        var vm = CreateVm();
        var later = CreateWorkOrder(orderNo: "WO-LATER");
        later.PlannedStart = DateTime.Today.AddHours(16);
        later.PlannedEnd = later.PlannedStart.AddHours(2);
        var earlier = CreateWorkOrder(orderNo: "WO-EARLIER");
        earlier.PlannedStart = DateTime.Today.AddHours(8);
        earlier.PlannedEnd = earlier.PlannedStart.AddHours(2);
        _workOrderRepo.Upsert(later);
        _workOrderRepo.Upsert(earlier);

        var filtered = vm.FilteredView.Cast<WorkOrder>().ToList();

        Assert.Equal(new[] { "WO-EARLIER", "WO-LATER" }, filtered.Select(w => w.OrderNo));
    }

    [Fact]
    public void ScheduleConflictCount_CountsOverlappingPendingAndRunningOrdersPerDevice()
    {
        var vm = CreateVm();
        var first = CreateWorkOrder(orderNo: "WO-FIRST", deviceId: "D1", status: WorkOrderStatus.Pending);
        first.PlannedStart = DateTime.Today.AddHours(8);
        first.PlannedEnd = DateTime.Today.AddHours(16);
        var second = CreateWorkOrder(orderNo: "WO-SECOND", deviceId: "D1", status: WorkOrderStatus.Running);
        second.PlannedStart = DateTime.Today.AddHours(12);
        second.PlannedEnd = DateTime.Today.AddHours(20);
        _workOrderRepo.Upsert(first);
        _workOrderRepo.Upsert(second);

        Assert.Equal(1, vm.ScheduleConflictCount);
        Assert.True(vm.HasScheduleConflicts);
    }

    [Fact]
    public void FilteredView_OnlyOverdue_FiltersPendingPastEnd()
    {
        var vm = CreateVm();
        var overdue = CreateWorkOrder(orderNo: "WO-OLD");
        overdue.PlannedEnd = DateTime.Now.AddMinutes(-1);
        _workOrderRepo.Upsert(overdue);
        var active = CreateWorkOrder(orderNo: "WO-NEW");
        active.PlannedEnd = DateTime.Now.AddHours(1);
        _workOrderRepo.Upsert(active);

        vm.OnlyOverdue = true;

        Assert.Single(vm.FilteredView.Cast<WorkOrder>());
        Assert.Equal("WO-OLD", vm.FilteredView.Cast<WorkOrder>().Single().OrderNo);
    }

    [Fact]
    public void FilteredView_OnlyHasNg_UsesCompletedSnapshot()
    {
        var vm = CreateVm();
        var withNg = CreateWorkOrder(orderNo: "WO-NG", status: WorkOrderStatus.Completed);
        withNg.CompletedOkCount = 90;
        withNg.CompletedNgCount = 10;
        _workOrderRepo.Upsert(withNg);
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-OK", status: WorkOrderStatus.Completed));

        vm.OnlyHasNg = true;

        Assert.Single(vm.FilteredView.Cast<WorkOrder>());
        Assert.Equal("WO-NG", vm.FilteredView.Cast<WorkOrder>().Single().OrderNo);
    }
}
