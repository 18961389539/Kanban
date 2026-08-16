using System.Collections.ObjectModel;
using MainAPP.Resources;
using Kanban.Analysis;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Helpers;
using MainAPP.Services;

namespace MainAPP.ViewModels;

public enum WorkOrderSortMode
{
    ScheduleStart,
    ScheduleEnd,
    StatusThenSchedule,
    CreatedAt,
}

public sealed record WorkOrderSortOption(WorkOrderSortMode Value, string Label);

/// <summary>工单设备筛选下拉项（按 DeviceId 匹配，显示设备名）。</summary>
public sealed record DeviceFilterOption(string Id, string Name);

/// <summary>
/// 工单管理页 ViewModel：提供工单列表查看、新增/编辑/删除、状态切换、筛选。
/// 工单业务逻辑（弹窗、状态机校验、二次确认、落库）已抽取到 <see cref="IWorkOrderService"/>，
/// 本 ViewModel 仅负责列表展示、筛选与命令转发，避免与 <see cref="DeviceManagerViewModel"/> 重复。
/// </summary>
public partial class WorkOrderManagerViewModel : ObservableObject, IDisposable
{
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly IWorkOrderService _workOrderService;
    private readonly DeviceRepository _deviceRepo;
    private readonly IDialogService _dialog;
    private readonly UserSession _userSession;

    /// <summary>工单集合（直接绑定到 Repository 的 ObservableCollection）。</summary>
    public ObservableCollection<WorkOrder> WorkOrders => _workOrderRepo.WorkOrders;

    /// <summary>工单过滤视图（支持按设备/状态/关键字筛选）。</summary>
    public ICollectionView FilteredView { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    private WorkOrder? _selectedWorkOrder;

    /// <summary>搜索关键字（按工单号/产品名称模糊匹配）。</summary>
    [ObservableProperty]
    private string _searchKeyword = "";

    /// <summary>状态筛选（null = 全部，否则按状态枚举过滤）。</summary>
    [ObservableProperty]
    private WorkOrderStatus? _statusFilter;

    /// <summary>设备筛选（存储 DeviceId，空串 = 全部设备）。</summary>
    [ObservableProperty]
    private string _deviceFilter = "";

    [ObservableProperty] private DateTime? _filterFromDate;
    [ObservableProperty] private DateTime? _filterToDate;
    [ObservableProperty] private bool _onlyOverdue;
    [ObservableProperty] private bool _onlyAchieved;
    [ObservableProperty] private bool _onlyHasNg;
    [ObservableProperty] private WorkOrderSortMode _sortMode = WorkOrderSortMode.ScheduleStart;

    /// <summary>可选设备筛选项（"全部设备" + 各设备，按 DeviceId 匹配）。</summary>
    public ObservableCollection<DeviceFilterOption> DeviceOptions { get; } = [new("", Strings.M044)];

    /// <summary>选中工单的产量聚合（详情页绑定）。null 表示未查询/未选中。</summary>
    [ObservableProperty]
    private WorkOrderProductionSummary? _selectedProduction;

    /// <summary>选中工单产量查询节流 + 取消（Remote 模式 GetProductionSummary 是 SignalR 往返，不能同步查）。</summary>
    private int? _lastProductionOrderId;
    private DateTime _lastProductionQueryUtc = DateTime.MinValue;
    private CancellationTokenSource? _productionCts;
    private static readonly TimeSpan ProductionThrottle = TimeSpan.FromSeconds(2);

    // ──────────── 状态计数（全量，不受筛选影响，供状态筛选下拉显示"进行中(2)"徽标） ────────────
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _abortedCount;
    [ObservableProperty] private int _overdueCount;
    [ObservableProperty] private int _achievedCount;
    [ObservableProperty] private int _ngWorkOrderCount;
    [ObservableProperty] private int _filteredCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduleConflicts))]
    private int _scheduleConflictCount;

    public bool HasScheduleConflicts => ScheduleConflictCount > 0;

    public IReadOnlyList<WorkOrderSortOption> SortOptions { get; } =
    [
        new(WorkOrderSortMode.ScheduleStart, Strings.M080),
        new(WorkOrderSortMode.ScheduleEnd, Strings.M081),
        new(WorkOrderSortMode.StatusThenSchedule, Strings.M082),
        new(WorkOrderSortMode.CreatedAt, Strings.M083),
    ];

    public string StartActionReason => SelectedWorkOrder == null
        ? Strings.M084
        : SelectedWorkOrder.Status != WorkOrderStatus.Pending
            ? Strings.M085
            : HasRunningWorkOrderOnDevice(SelectedWorkOrder)
                ? Strings.M086
                : Strings.M087;

    public string CompleteActionReason => SelectedWorkOrder == null
        ? Strings.M084
        : SelectedWorkOrder.Status != WorkOrderStatus.Running
            ? Strings.M088
            : Strings.M089;

    public string AbortActionReason => SelectedWorkOrder == null
        ? Strings.M084
        : SelectedWorkOrder.Status is not (WorkOrderStatus.Pending or WorkOrderStatus.Running)
            ? Strings.M090
            : Strings.M091;

    public WorkOrderManagerViewModel(
        WorkOrderRepository workOrderRepo,
        IWorkOrderService workOrderService,
        DeviceRepository deviceRepo,
        IDialogService dialog,
        UserSession userSession)
    {
        _workOrderRepo = workOrderRepo;
        _workOrderService = workOrderService;
        _deviceRepo = deviceRepo;
        _dialog = dialog;
        _userSession = userSession;

        // 创建独立的 ListCollectionView（不能用 GetDefaultView，否则与其他 ViewModel 共享同一视图导致 Filter 互相覆盖）
        FilteredView = new ListCollectionView(_workOrderRepo.WorkOrders);
        FilteredView.Filter = FilterWorkOrder;

        RefreshDeviceOptions();
        RefreshStatusCounts();
        ApplySort();
        // 订阅集合变化：工单增删/状态切换后重算各状态计数（命名方法，Dispose 时解绑）
        _workOrderRepo.WorkOrders.CollectionChanged += OnWorkOrdersCollectionChanged;
        // 每分钟兜底刷新时间相关计数（逾期判定随时间变化，无集合事件可订阅）
        _derivedCountsTimer.Tick += (_, _) => RecalcDerivedCounts();
        _derivedCountsTimer.Start();
    }

    /// <summary>工单集合变更 → 增量维护状态计数 + 全量重算时间/产量相关计数。
    /// 增量路径仅覆盖 Status 四计数（只随集合事件变化，增量安全）；
    /// 逾期/达标/NG 依赖 DateTime.Now 与回填的 Production（集合事件之外也会变），
    /// 一律全量重算（工单量百级，Count 开销可忽略），修复口径漂移（2026-08-16）。</summary>
    private void OnWorkOrdersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                foreach (var w in e.NewItems!.OfType<WorkOrder>()) AdjustCounts(w, +1);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                foreach (var w in e.OldItems!.OfType<WorkOrder>()) AdjustCounts(w, -1);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                foreach (var w in e.OldItems!.OfType<WorkOrder>()) AdjustCounts(w, -1);
                foreach (var w in e.NewItems!.OfType<WorkOrder>()) AdjustCounts(w, +1);
                break;
            default:
                RefreshStatusCounts();
                return;
        }
        // 时间/产量相关计数全量重算（见方法头注释）；冲突计数同样依赖全局关系，全量。
        RecalcDerivedCounts();
        RefreshFilteredView();
    }

    /// <summary>按状态增量调整四个状态计数（仅 Status——其余计数见 RecalcDerivedCounts）。</summary>
    private void AdjustCounts(WorkOrder w, int delta)
    {
        switch (w.Status)
        {
            case WorkOrderStatus.Pending: PendingCount += delta; break;
            case WorkOrderStatus.Running: RunningCount += delta; break;
            case WorkOrderStatus.Completed: CompletedCount += delta; break;
            case WorkOrderStatus.Aborted: AbortedCount += delta; break;
        }
    }

    /// <summary>全量重算逾期/达标/NG/冲突计数：这些判定随时间流逝（IsOverdue 用 DateTime.Now）
    /// 与产量回填（Production 属性）变化，集合事件无法覆盖，必须周期性/事件后全量。</summary>
    private void RecalcDerivedCounts()
    {
        OverdueCount = _workOrderRepo.WorkOrders.Count(IsOverdue);
        AchievedCount = _workOrderRepo.WorkOrders.Count(IsAchieved);
        NgWorkOrderCount = _workOrderRepo.WorkOrders.Count(HasNgProduction);
        ScheduleConflictCount = CountScheduleConflicts(_workOrderRepo.GetSnapshot());
    }

    /// <summary>低频定时器：每分钟全量重算时间相关计数，覆盖"工单跨过 PlannedEnd 变逾期"的无事件转变。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _derivedCountsTimer = new()
    {
        Interval = TimeSpan.FromMinutes(1),
    };

    private bool _disposed;

    /// <summary>解除集合订阅与定时器（遵守事件治理约定：命名方法 + Dispose 解绑，防僵尸回调）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _derivedCountsTimer.Stop();
        _productionCts?.Cancel();
        _productionCts?.Dispose();
        _workOrderRepo.WorkOrders.CollectionChanged -= OnWorkOrdersCollectionChanged;
        GC.SuppressFinalize(this);
    }

    /// <summary>按状态统计全量工单数量，更新各计数属性（供下拉显示"进行中(2)"）。</summary>
    private void RefreshStatusCounts()
    {
        PendingCount = _workOrderRepo.WorkOrders.Count(w => w.Status == WorkOrderStatus.Pending);
        RunningCount = _workOrderRepo.WorkOrders.Count(w => w.Status == WorkOrderStatus.Running);
        CompletedCount = _workOrderRepo.WorkOrders.Count(w => w.Status == WorkOrderStatus.Completed);
        AbortedCount = _workOrderRepo.WorkOrders.Count(w => w.Status == WorkOrderStatus.Aborted);
        OverdueCount = _workOrderRepo.WorkOrders.Count(IsOverdue);
        AchievedCount = _workOrderRepo.WorkOrders.Count(IsAchieved);
        NgWorkOrderCount = _workOrderRepo.WorkOrders.Count(HasNgProduction);
        ScheduleConflictCount = CountScheduleConflicts(_workOrderRepo.GetSnapshot());
        RefreshFilteredView();
    }

    /// <summary>从 DeviceRepository 刷新设备筛选下拉选项。</summary>
    public void RefreshDeviceOptions()
    {
        var devices = _deviceRepo.GetDevicesSnapshot();
        var current = DeviceFilter;
        DeviceOptions.Clear();
        DeviceOptions.Add(new DeviceFilterOption("", Strings.M044));
        foreach (var d in devices)
            DeviceOptions.Add(new DeviceFilterOption(d.Id, d.Name));
        // 尝试恢复之前选中的设备筛选（若仍存在）
        DeviceFilter = DeviceOptions.Any(o => o.Id == current) ? current : "";
    }

    /// <summary>过滤条件：关键字 + 状态 + 设备。</summary>
    private bool FilterWorkOrder(object obj)
    {
        if (obj is not WorkOrder w) return false;
        // 状态筛选
        if (StatusFilter.HasValue && w.Status != StatusFilter.Value)
            return false;
        // 设备筛选（按 DeviceId，避免设备改名后历史工单匹配不到）
        if (!string.IsNullOrEmpty(DeviceFilter))
        {
            if (w.DeviceId != DeviceFilter) return false;
        }
        // 关键字筛选（工单号/产品编码/产品名称/设备名）
        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            var kw = SearchKeyword.Trim();
            if (!w.OrderNo.Contains(kw, StringComparison.OrdinalIgnoreCase)
                && !w.ProductCode.Contains(kw, StringComparison.OrdinalIgnoreCase)
                && !w.ProductName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                && !w.DeviceName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (FilterFromDate.HasValue && w.PlannedEnd < FilterFromDate.Value.Date)
            return false;
        if (FilterToDate.HasValue && w.PlannedStart >= FilterToDate.Value.Date.AddDays(1))
            return false;
        if (OnlyOverdue && !IsOverdue(w)) return false;
        if (OnlyAchieved && !IsAchieved(w)) return false;
        if (OnlyHasNg && !HasNgProduction(w)) return false;
        return true;
    }

    partial void OnSearchKeywordChanged(string value) => RefreshFilteredView();
    partial void OnStatusFilterChanged(WorkOrderStatus? value) => RefreshFilteredView();
    partial void OnDeviceFilterChanged(string value) => RefreshFilteredView();
    partial void OnFilterFromDateChanged(DateTime? value) => RefreshFilteredView();
    partial void OnFilterToDateChanged(DateTime? value) => RefreshFilteredView();
    partial void OnOnlyOverdueChanged(bool value) => RefreshFilteredView();
    partial void OnOnlyAchievedChanged(bool value) => RefreshFilteredView();
    partial void OnOnlyHasNgChanged(bool value) => RefreshFilteredView();
    partial void OnSortModeChanged(WorkOrderSortMode value)
    {
        ApplySort();
        RefreshFilteredView();
    }

    private void ApplySort()
    {
        FilteredView.SortDescriptions.Clear();
        switch (SortMode)
        {
            case WorkOrderSortMode.ScheduleEnd:
                FilteredView.SortDescriptions.Add(new SortDescription(nameof(WorkOrder.PlannedEnd), ListSortDirection.Ascending));
                FilteredView.SortDescriptions.Add(new SortDescription(nameof(WorkOrder.PlannedStart), ListSortDirection.Ascending));
                break;
            case WorkOrderSortMode.StatusThenSchedule:
                FilteredView.SortDescriptions.Add(new SortDescription(nameof(WorkOrder.Status), ListSortDirection.Ascending));
                FilteredView.SortDescriptions.Add(new SortDescription(nameof(WorkOrder.PlannedStart), ListSortDirection.Ascending));
                break;
            case WorkOrderSortMode.CreatedAt:
                FilteredView.SortDescriptions.Add(new SortDescription(nameof(WorkOrder.CreatedAt), ListSortDirection.Descending));
                break;
            default:
                FilteredView.SortDescriptions.Add(new SortDescription(nameof(WorkOrder.PlannedStart), ListSortDirection.Ascending));
                FilteredView.SortDescriptions.Add(new SortDescription(nameof(WorkOrder.PlannedEnd), ListSortDirection.Ascending));
                break;
        }
    }

    private void RefreshFilteredView()
    {
        FilteredView.Refresh();
        FilteredCount = FilteredView.Cast<WorkOrder>().Count();
    }

    private static int CountScheduleConflicts(IReadOnlyList<WorkOrder> workOrders)
    {
        var conflicts = 0;
        foreach (var group in workOrders
                     .Where(w => w.Status is WorkOrderStatus.Pending or WorkOrderStatus.Running
                         && w.PlannedEnd > w.PlannedStart)
                     .GroupBy(w => w.DeviceId))
        {
            var ordered = group.OrderBy(w => w.PlannedStart).ToList();
            for (var i = 0; i < ordered.Count; i++)
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (ordered[j].PlannedStart >= ordered[i].PlannedEnd) break;
                if (ordered[i].PlannedStart < ordered[j].PlannedEnd)
                    conflicts++;
            }
        }
        return conflicts;
    }

    private static bool IsOverdue(WorkOrder w)
        => w.Status is WorkOrderStatus.Pending or WorkOrderStatus.Running
            && w.PlannedEnd != default
            && w.PlannedEnd < DateTime.Now;

    private static bool IsAchieved(WorkOrder w)
        => w.CompletedOkCount.HasValue
            ? w.TargetQuantity > 0 && w.CompletedOkCount.Value >= w.TargetQuantity
            : w.Production?.AchievementRate >= 1.0;

    private static bool HasNgProduction(WorkOrder w)
        => w.CompletedNgCount > 0 || w.Production?.NgCount > 0;

    /// <summary>选中工单变化时查询产量聚合。</summary>
    partial void OnSelectedWorkOrderChanged(WorkOrder? value)
    {
        RefreshSelectedProduction();
        NotifyActionReasonsChanged();
    }

    private void NotifyActionReasonsChanged()
    {
        OnPropertyChanged(nameof(StartActionReason));
        OnPropertyChanged(nameof(CompleteActionReason));
        OnPropertyChanged(nameof(AbortActionReason));
    }

    private bool HasRunningWorkOrderOnDevice(WorkOrder workOrder)
        => _workOrderRepo.GetRunningByDevice(workOrder.DeviceId) is { } running
            && running.Id != workOrder.Id;

    /// <summary>查询选中工单的产量聚合并更新 SelectedProduction 属性（后台查询 + 2s 节流）。</summary>
    private void RefreshSelectedProduction()
    {
        var order = SelectedWorkOrder;
        if (order == null)
        {
            _productionCts?.Cancel();
            _productionCts?.Dispose();
            _productionCts = null;
            _lastProductionOrderId = null;
            SelectedProduction = null;
            return;
        }

        // 节流：同一工单 2s 内复用上次结果（Remote 模式为 SignalR 往返）
        if (_lastProductionOrderId == order.Id && DateTime.UtcNow - _lastProductionQueryUtc < ProductionThrottle)
            return;

        _productionCts?.Cancel();
        _productionCts?.Dispose();
        var cts = _productionCts = new CancellationTokenSource();
        var token = cts.Token;
        _lastProductionOrderId = order.Id;
        _lastProductionQueryUtc = DateTime.UtcNow;

        Task.Run(() =>
        {
            WorkOrderProductionSummary? summary;
            try
            {
                summary = _workOrderService.GetProductionSummary(order);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "查询工单 {OrderNo} 产量聚合失败", order.OrderNo);
                return;
            }
            if (token.IsCancellationRequested) return;
            UiDispatcher.Dispatch(() =>
            {
                if (token.IsCancellationRequested || SelectedWorkOrder?.Id != order.Id) return;
                SelectedProduction = summary;
            });
        }, token).Forget();
    }

    /// <summary>
    /// 刷新所有可见工单的产量聚合（批量查询，回填到 WorkOrder.Production 运行时属性）。
    /// 列表项绑定 Production.OkCount 显示进度。由用户点击"刷新产量"按钮触发，避免自动批量查询卡顿。
    /// </summary>
    [RelayCommand]
    private void RefreshAllProduction()
    {
        var snapshot = _workOrderRepo.GetSnapshot();
        var summaries = _workOrderService.GetProductionSummaries(snapshot);
        foreach (var w in snapshot)
        {
            if (!summaries.TryGetValue(w.Id, out var summary)) continue;
            w.Production = new WorkOrderRuntimeProduction
            {
                OkCount = summary.OkCount,
                NgCount = summary.NgCount,
                AchievementRate = summary.AchievementRate,
                ScheduleStatusText = GetScheduleStatusText(w, summary.AchievementRate),
                ProgressDeviation = GetProgressDeviation(w, summary.AchievementRate),
            };
        }
        // 触发列表刷新让绑定更新
        RefreshStatusCounts();
        // 同步更新详情页选中工单的产量（直接取批量结果，避免再发一次单工单查询）
        if (SelectedWorkOrder != null && summaries.TryGetValue(SelectedWorkOrder.Id, out var selSummary))
            SelectedProduction = selSummary;
        _dialog.NotifyInfo(string.Format(Strings.F098, WorkOrders.Count));
    }

    private static double GetProgressDeviation(WorkOrder workOrder, double achievementRate)
    {
        if (workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Aborted)
            return achievementRate - 1.0;
        if (workOrder.PlannedStart == default || workOrder.PlannedEnd <= workOrder.PlannedStart)
            return 0;
        var total = (workOrder.PlannedEnd - workOrder.PlannedStart).TotalSeconds;
        var elapsed = (DateTime.Now - workOrder.PlannedStart).TotalSeconds;
        var plannedRate = Math.Clamp(elapsed / total, 0, 1);
        return achievementRate - plannedRate;
    }

    private static string GetScheduleStatusText(WorkOrder workOrder, double achievementRate)
    {
        if (workOrder.Status == WorkOrderStatus.Completed)
            return achievementRate >= 1 ? Strings.M092 : Strings.M093;
        if (workOrder.Status == WorkOrderStatus.Aborted)
            return Strings.M031;
        if (achievementRate >= 1.0)
            return Strings.M032;
        if (workOrder.PlannedEnd < DateTime.Now)
            return Strings.M033;
        return GetProgressDeviation(workOrder, achievementRate) < -0.1 ? Strings.M094 : Strings.M095;
    }

    [RelayCommand]
    private async Task Add()
    {
        var saved = await _workOrderService.AddWorkOrderAsync(null);
        if (saved != null)
        {
            SelectedWorkOrder = saved;
            AuditLog.Record("WorkOrder.Add", "WorkOrder", saved.OrderNo, detail: $"产品={saved.ProductCode} 目标={saved.TargetQuantity}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task Edit()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.EditWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    private bool CanEdit() => SelectedWorkOrder != null;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task Copy()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.CopyWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    private bool CanCopy() => SelectedWorkOrder != null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        if (SelectedWorkOrder == null) return;
        var orderNo = SelectedWorkOrder.OrderNo;
        if (await _workOrderService.DeleteWorkOrderAsync(SelectedWorkOrder))
        {
            AuditLog.Record("WorkOrder.Delete", "WorkOrder", orderNo);
            SelectedWorkOrder = null;
        }
    }

    private bool CanDelete() => SelectedWorkOrder != null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (SelectedWorkOrder == null) return;
        var statusBefore = SelectedWorkOrder.Status;
        var saved = await _workOrderService.StartWorkOrderAsync(SelectedWorkOrder);
        if (saved != null)
        {
            SelectedWorkOrder = saved;
            AuditLog.Record("WorkOrder.Start", "WorkOrder", saved.OrderNo,
                before: new { Status = statusBefore.ToString() },
                after: new { Status = saved.Status.ToString() });
        }
    }

    private bool CanStart() => SelectedWorkOrder != null && SelectedWorkOrder.Status == WorkOrderStatus.Pending;

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task Complete()
    {
        if (SelectedWorkOrder == null) return;
        var statusBefore = SelectedWorkOrder.Status;
        var saved = await _workOrderService.CompleteWorkOrderAsync(SelectedWorkOrder);
        if (saved != null)
        {
            SelectedWorkOrder = saved;
            AuditLog.Record("WorkOrder.Complete", "WorkOrder", saved.OrderNo,
                before: new { Status = statusBefore.ToString() },
                after: new { Status = saved.Status.ToString() });
        }
    }

    private bool CanComplete() => SelectedWorkOrder != null && SelectedWorkOrder.Status == WorkOrderStatus.Running;

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task Abort()
    {
        if (SelectedWorkOrder == null) return;
        var statusBefore = SelectedWorkOrder.Status;
        var saved = await _workOrderService.AbortWorkOrderAsync(SelectedWorkOrder);
        if (saved != null)
        {
            SelectedWorkOrder = saved;
            AuditLog.Record("WorkOrder.Abort", "WorkOrder", saved.OrderNo,
                before: new { Status = statusBefore.ToString() },
                after: new { Status = saved.Status.ToString() });
        }
    }

    private bool CanAbort() => SelectedWorkOrder != null
        && (SelectedWorkOrder.Status == WorkOrderStatus.Running || SelectedWorkOrder.Status == WorkOrderStatus.Pending);

    /// <summary>
    /// 导出当前筛选结果到 CSV 文件。
    /// 包含工单号、产品编码、产品名称、设备名、计划产量、计划时间、状态、备注等字段。
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var filtered = FilteredView.OfType<WorkOrder>().ToList();
        if (filtered.Count == 0)
        {
            _dialog.NotifyWarning(Strings.M034);
            return;
        }

        var defaultFileName = $"工单列表_{DateTime.Now:yyyyMMddHHmm}.csv";
        var path = _dialog.ShowSaveFileDialog(Strings.M_ExportWorkOrders, defaultFileName, Strings.M310);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            // UTF-8 with BOM：Excel 打开中文不乱码
            using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
            // 表头（多语言资源；文件名模板有意保留中文，跨语言归档稳定）
            writer.WriteLine(Strings.M346);
            foreach (var w in filtered)
            {
                var statusText = w.Status switch
                {
                    WorkOrderStatus.Pending => Strings.M041,
                    WorkOrderStatus.Running => Strings.M042,
                    WorkOrderStatus.Completed => Strings.M043,
                    WorkOrderStatus.Aborted => Strings.M031,
                    _ => w.Status.ToString(),
                };
                // CSV 字段含逗号需双引号包裹
                var remark = w.Remark ?? "";
                writer.WriteLine(string.Join(",",
                    CsvUtil.Escape(w.OrderNo),
                    CsvUtil.Escape(w.ProductCode),
                    CsvUtil.Escape(w.ProductName),
                    CsvUtil.Escape(w.DeviceName),
                    w.TargetQuantity,
                    w.PlannedStart.ToString("yyyy-MM-dd HH:mm"),
                    w.PlannedEnd.ToString("yyyy-MM-dd HH:mm"),
                    statusText,
                    CsvUtil.Escape(remark),
                    w.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                    w.UpdatedAt.ToString("yyyy-MM-dd HH:mm")));
            }

            _dialog.NotifySuccess(string.Format(Strings.F106, filtered.Count, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F090, ex.Message));
        }
    }

    // ──────────── 样本数据生成（DEBUG） ────────────

    /// <summary>
    /// 是否为 DEBUG 编译版本。绑定到"生成样本工单"按钮的 Visibility，
    /// 避免发布版暴露虚拟数据生成功能。Release 编译时按钮折叠。
    /// </summary>
    public bool IsDebugBuild => MainAPP.Helpers.BuildInfo.IsDebug;

    /// <summary>
    /// 生成样本工单用于 UI 预览/调试。读取当前设备列表，为每台设备生成 2-3 个工单，
    /// 覆盖 Pending/Running/Completed/Aborted 四种状态。需密码确认，已有工单时提示是否追加。
    /// </summary>
    [RelayCommand]
    private async Task SeedSampleWorkOrders()
    {
        var devices = _deviceRepo.GetDevicesSnapshot();
        if (devices.Count == 0)
        {
            _dialog.NotifyWarning(Strings.M035);
            return;
        }

        // 权限验证：生成虚拟数据需工程师或以上角色
        if (!_userSession.IsEngineerOrAbove)
        {
            _dialog.NotifyWarning(Strings.M336);
            return;
        }

        if (WorkOrders.Count > 0)
        {
            var confirm = _dialog.Show(
                string.Format(Strings.F121, WorkOrders.Count, devices.Count * 3),
                Strings.M345, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var samples = WorkOrderSampleBuilder.BuildSampleWorkOrders(devices);
        foreach (var wo in samples)
            await _workOrderRepo.UpsertAsync(wo);

        _dialog.NotifySuccess(string.Format(Strings.F115, samples.Count));
    }
}
