using System.IO;
using System.Linq;
using System.Windows;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
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

        Assert.Single(_workOrderRepo.GetSnapshot());
        Assert.Contains(_dialog.Success, s => s.Contains("已新增"));
        Assert.Equal(wo, vm.SelectedWorkOrder);
    }

    [Fact]
    public void Add_DialogReturnsNull_DoesNotPersist()
    {
        var vm = CreateVm();
        _dialog.WorkOrderEditorResult = null;

        vm.AddCommand.Execute(null);

        Assert.Empty(_workOrderRepo.GetSnapshot());
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
        Assert.Equal("更新后产品", _workOrderRepo.GetSnapshot()[0].ProductName);
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
        Assert.Equal("测试产品", _workOrderRepo.GetSnapshot()[0].ProductName);
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

        var copy = Assert.Single(_workOrderRepo.GetSnapshot(), w => w.OrderNo == "WO-DONE-COPY");
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
        Assert.Single(_workOrderRepo.GetSnapshot());
        Assert.Same(saved, _workOrderRepo.GetSnapshot()[0]);
    }

    [Fact]
    public void Add_DuplicateOrderNo_IsRejected()
    {
        var vm = CreateVm();
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-DUP"));
        _dialog.WorkOrderEditorResult = CreateWorkOrder(orderNo: " wo-dup ");

        vm.AddCommand.Execute(null);

        Assert.Single(_workOrderRepo.GetSnapshot());
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

        Assert.Single(_workOrderRepo.GetSnapshot());
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

        Assert.Empty(_workOrderRepo.GetSnapshot());
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

        Assert.Single(_workOrderRepo.GetSnapshot());
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

        Assert.Equal(WorkOrderStatus.Running, _workOrderRepo.GetSnapshot()[0].Status);
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
    public void Start_DeviceAlreadyHasRunning_CommandCannotExecute()
    {
        var vm = CreateVm();
        _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-001", deviceId: "D1", status: WorkOrderStatus.Running));
        var pending = _workOrderRepo.Upsert(CreateWorkOrder(orderNo: "WO-002", deviceId: "D1", status: WorkOrderStatus.Pending));
        vm.SelectedWorkOrder = pending;

        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(WorkOrderStatus.Pending, _workOrderRepo.GetSnapshot().Single(w => w.OrderNo == "WO-002").Status);
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

        Assert.Equal(WorkOrderStatus.Completed, _workOrderRepo.GetSnapshot()[0].Status);
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

        Assert.Equal(WorkOrderStatus.Aborted, _workOrderRepo.GetSnapshot()[0].Status);
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

        Assert.Equal(WorkOrderStatus.Running, _workOrderRepo.GetSnapshot()[0].Status);
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

        Assert.Equal(WorkOrderStatus.Aborted, _workOrderRepo.GetSnapshot()[0].Status);
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

        vm.StatusFilter = WorkOrderStatus.Running;
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

        vm.StatusFilter = null;
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

    // ──────────── 冲突集合 / 汇总条（#4 / #10）────────────

    [Fact]
    public void ConflictOrderIds_ContainsOnlyOverlappingOrders_NotNonOverlappingOnSameDevice()
    {
        var vm = CreateVm();
        var first = CreateWorkOrder(orderNo: "WO-C1", deviceId: "D1", status: WorkOrderStatus.Pending);
        first.PlannedStart = DateTime.Today.AddHours(8);
        first.PlannedEnd = DateTime.Today.AddHours(12);
        var second = CreateWorkOrder(orderNo: "WO-C2", deviceId: "D1", status: WorkOrderStatus.Pending);
        second.PlannedStart = DateTime.Today.AddHours(10);
        second.PlannedEnd = DateTime.Today.AddHours(14);
        var others = CreateWorkOrder(orderNo: "WO-IND", deviceId: "D2", status: WorkOrderStatus.Pending);
        others.PlannedStart = DateTime.Today.AddHours(8);
        others.PlannedEnd = DateTime.Today.AddHours(12);
        _workOrderRepo.Upsert(first);
        _workOrderRepo.Upsert(second);
        _workOrderRepo.Upsert(others);
        Assert.Equal(1, vm.ScheduleConflictCount); // 1 对重叠
        Assert.Equal(2, vm.ConflictOrderIds.Count); // 双方都计入高亮
        Assert.True(vm.ConflictOrderIds.Contains(first.Id) && vm.ConflictOrderIds.Contains(second.Id));
        Assert.False(vm.ConflictOrderIds.Contains(others.Id));
    }

    [Fact]
    public void ConflictOrderIds_EmptyWhenNoOverlap()
    {
        var vm = CreateVm();
        var first = CreateWorkOrder(orderNo: "WO-A", deviceId: "D1");
        first.PlannedStart = DateTime.Today.AddHours(8);
        first.PlannedEnd = DateTime.Today.AddHours(10);
        var second = CreateWorkOrder(orderNo: "WO-B", deviceId: "D1");
        second.PlannedStart = DateTime.Today.AddHours(11);
        second.PlannedEnd = DateTime.Today.AddHours(13);
        _workOrderRepo.Upsert(first);
        _workOrderRepo.Upsert(second);

        Assert.Equal(0, vm.ConflictOrderIds.Count);
        Assert.False(vm.HasScheduleConflicts);
    }

    [Fact]
    public void FilteredSummary_TotalsAndRate_ComputeOverCurrentFilter()
    {
        var vm = CreateVm();
        var wo1 = CreateWorkOrder(orderNo: "WO-S1", status: WorkOrderStatus.Running);
        wo1.TargetQuantity = 1000;
        wo1.Production = new WorkOrderRuntimeProduction { OkCount = 400 };
        _workOrderRepo.Upsert(wo1);
        var wo2 = CreateWorkOrder(orderNo: "WO-S2", status: WorkOrderStatus.Running);
        wo2.TargetQuantity = 500;
        wo2.Production = new WorkOrderRuntimeProduction { OkCount = 200 };
        _workOrderRepo.Upsert(wo2);

        Assert.Equal(1500, vm.FilteredTargetTotal);
        Assert.Equal(600, vm.FilteredOkTotal);
        Assert.Equal(0.4, vm.FilteredAchievementRate, 3);
    }

    [Fact]
    public void OverdueOrderIds_ContainsOnlyOverdueOrders()
    {
        var vm = CreateVm();
        var overdue = CreateWorkOrder(orderNo: "WO-OLD");
        overdue.PlannedEnd = DateTime.Now.AddMinutes(-5);
        _workOrderRepo.Upsert(overdue);
        var active = CreateWorkOrder(orderNo: "WO-NEW");
        active.PlannedEnd = DateTime.Now.AddHours(2);
        _workOrderRepo.Upsert(active);

        Assert.Single(vm.OverdueOrderIds);
        Assert.Contains(overdue.Id, vm.OverdueOrderIds);
        Assert.False(vm.OverdueOrderIds.Contains(active.Id));
        Assert.DoesNotContain(_workOrderRepo.GetSnapshot(), w => string.IsNullOrWhiteSpace(w.OverdueHintText) && w.Id == overdue.Id);
    }

    // ──────────── 详情页逾期提示（P2 修复：VM 计算属性驱动）────────────

    [Fact]
    public void SelectedOverdueHintText_RefreshedOnSelectionChange()
    {
        var vm = CreateVm();
        var overdue = CreateWorkOrder(orderNo: "WO-DTL-OLD");
        overdue.PlannedEnd = DateTime.Now.AddMinutes(-5);
        var saved = _workOrderRepo.Upsert(overdue);

        // 选中逾期工单：详情页徽章立即显示
        vm.SelectedWorkOrder = saved;
        Assert.NotNull(vm.SelectedOverdueHintText);
        Assert.Contains("逾期", vm.SelectedOverdueHintText);
    }

    [Fact]
    public void SelectedOverdueHintText_NullForNonOverdueOrNoSelection()
    {
        var vm = CreateVm();
        var active = CreateWorkOrder(orderNo: "WO-DTL-OK");
        active.PlannedEnd = DateTime.Now.AddHours(2);
        var saved = _workOrderRepo.Upsert(active);

        vm.SelectedWorkOrder = saved;
        Assert.Null(vm.SelectedOverdueHintText);

        vm.SelectedWorkOrder = null;
        Assert.Null(vm.SelectedOverdueHintText);
    }

    [Fact]
    public void SelectedOverdueHintText_RefreshesAsTimePasses_TimerPath()
    {
        var vm = CreateVm();
        // 工单计划结束是过去 2 分钟 —— 启动即逾期，约束放 RecalcDerivedCounts 兜底路径
        // 模拟：先选中时不逾期，随后 PlannedEnd 跨过（不可能真实改实体时间），
        // 改为直接验证 RecalcDerivedCounts 的刷新动作对选中项生效：
        var wo = CreateWorkOrder(orderNo: "WO-DTL-TIMER");
        wo.PlannedEnd = DateTime.Now.AddHours(1); // 未逾期
        var saved = _workOrderRepo.Upsert(wo);
        vm.SelectedWorkOrder = saved;
        Assert.Null(vm.SelectedOverdueHintText);

        // 让工单变为逾期（模拟时间流逝后实体时间被更新；触发集合事件 → RecalcDerivedCounts → RefreshSelectedOverdueHint）
        saved.PlannedEnd = DateTime.Now.AddMinutes(-1);
        _workOrderRepo.Upsert(saved); // Replace 触发 CollectionChanged → RecalcDerivedCounts
        Assert.NotNull(vm.SelectedOverdueHintText);
    }

    [Fact]
    public void TimerPath_RefreshGanttForCurrentFilter_RebuildsChartModel_WithNewNowLine()
    {
        var vm = CreateVm();
        // 甘特图构建有 _pageActive 守卫（导航生命周期延迟重活），必须先进入页面才会构建
        vm.OnPageEnter();
        var wo = CreateWorkOrder(orderNo: "WO-GANTT-TIMER");
        wo.PlannedStart = DateTime.Now.AddHours(-4);
        wo.PlannedEnd = DateTime.Now.AddHours(4);
        _workOrderRepo.Upsert(wo);

        // 初始甘特已构建且含"现在"线。构建在 Task.Run 后台完成后经 UiDispatcher 回填
        //（无 Application 时同步执行），自旋等待就绪，避免与后台构建竞态
        SpinUntil(() => vm.WorkOrderGanttChartModel != null);
        var first = vm.WorkOrderGanttChartModel;
        Assert.NotNull(first);

        // timer 回调 = RecalcDerivedCounts + RefreshGanttForCurrentFilter；
        // 该私有方法经 RefreshFilteredView → RefreshGanttChart 等价路径驱动，
        // 此处用筛选变化触发同一路径：模型应被替换为新实例（新"现在"线 X 值）
        vm.SearchKeyword = "GANTT-TIMER";
        SpinUntil(() => !ReferenceEquals(vm.WorkOrderGanttChartModel, first));
        Assert.NotSame(first, vm.WorkOrderGanttChartModel);
    }

    /// <summary>轮询等待条件满足（最多 5s）。甘特图由 Task.Run 后台构建后异步回填，断言前须等待。
    /// 甘特重建入口经 ChartDiffGate 的 DispatcherTimer 防抖（Background 优先级），测试线程没有
    /// 运行中的 Dispatcher 时 Tick 永不触发——每轮泵一次 Dispatcher 帧驱动防抖到期（审查修复 2026-09-03）。</summary>
    private static void SpinUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            System.Threading.Thread.Sleep(10);
            PumpCurrentDispatcher();
        }
    }

    /// <summary>在当前线程的 Dispatcher 上泵一帧：处理所有已排队的操作（含 Background 优先级的防抖 Tick）后返回。
    /// Continue=false 必须用低于 Background 的优先级：同优先级 FIFO 会先执行退出指令，
    /// Dispatcher 每帧只处理这一个操作，防抖 Tick 永远排不上队。</summary>
    private static void PumpCurrentDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            new Action(() => frame.Continue = false),
            System.Windows.Threading.DispatcherPriority.SystemIdle);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
