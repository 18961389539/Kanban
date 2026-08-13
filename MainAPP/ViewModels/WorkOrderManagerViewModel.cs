using System.Collections.ObjectModel;
using MainAPP.Resources;
using MainAPP.Helpers;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
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

    /// <summary>状态筛选选项（"全部"/"待开始"/"进行中"/"已完成"/"已中止"）。</summary>
    [ObservableProperty]
    private string _statusFilter = Strings.M040;

    /// <summary>设备筛选选项（"全部设备" + 各设备名称）。</summary>
    [ObservableProperty]
    private string _deviceFilter = Strings.M044;

    [ObservableProperty] private DateTime? _filterFromDate;
    [ObservableProperty] private DateTime? _filterToDate;
    [ObservableProperty] private bool _onlyOverdue;
    [ObservableProperty] private bool _onlyAchieved;
    [ObservableProperty] private bool _onlyHasNg;
    [ObservableProperty] private WorkOrderSortMode _sortMode = WorkOrderSortMode.ScheduleStart;

    /// <summary>可选状态筛选值列表（中文标签，绑定到 ComboBox）。</summary>
    public IReadOnlyList<string> StatusOptions { get; } = new[] { Strings.M040, Strings.M041, Strings.M042, Strings.M043, Strings.M031 };

    /// <summary>可选设备筛选值列表（"全部设备" + 各设备名称，启动时从 DeviceRepository 刷新）。</summary>
    public ObservableCollection<string> DeviceOptions { get; } = new() { Strings.M044 };

    /// <summary>中文状态标签 → 枚举值映射（过滤时用）。</summary>
    private static readonly Dictionary<string, WorkOrderStatus?> StatusLabelToEnum = new()
    {
        [Strings.M040] = null,
        [Strings.M041] = WorkOrderStatus.Pending,
        [Strings.M042] = WorkOrderStatus.Running,
        [Strings.M043] = WorkOrderStatus.Completed,
        [Strings.M031] = WorkOrderStatus.Aborted,
    };

    /// <summary>选中工单的产量聚合（详情页绑定）。null 表示未查询/未选中。</summary>
    [ObservableProperty]
    private WorkOrderProductionSummary? _selectedProduction;

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
    }

    /// <summary>工单集合变更 → 增量维护各状态计数（命名方法，可精确解绑）。
    /// 增量路径：Add/Remove/Replace 按新旧项 O(1) 调整计数，避免每次 8 次全量 LINQ Count
    /// （Remote 模式 Meta 每 5s Upsert 触发 Replace，全量重算在 UI 线程的成本集中在此时）；
    /// Reset（LoadAll 全量替换）回退全量重算。冲突检测依赖全局关系，仍全量（注释见下）。</summary>
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
        // 冲突计数与筛选视图仍需全量（冲突是设备内区间两两关系，增量维护复杂且工单量为百级时
        // O(n²) 在 5s 周期内可接受；7 个状态计数已增量，主要开销已消除）
        ScheduleConflictCount = CountScheduleConflicts(_workOrderRepo.GetSnapshot());
        RefreshFilteredView();
    }

    /// <summary>按工单的状态/逾期/达标/含不良属性增量调整计数（delta=±1；与全量 Count 口径一致，
    /// 每个工单恰好贡献 0 或 1——Replace 时先 -1 旧项再 +1 新项即得净变化）。</summary>
    private void AdjustCounts(WorkOrder w, int delta)
    {
        switch (w.Status)
        {
            case WorkOrderStatus.Pending: PendingCount += delta; break;
            case WorkOrderStatus.Running: RunningCount += delta; break;
            case WorkOrderStatus.Completed: CompletedCount += delta; break;
            case WorkOrderStatus.Aborted: AbortedCount += delta; break;
        }
        if (IsOverdue(w)) OverdueCount += delta;
        if (IsAchieved(w)) AchievedCount += delta;
        if (HasNgProduction(w)) NgWorkOrderCount += delta;
    }

    private bool _disposed;

    /// <summary>解除集合订阅（遵守事件治理约定：命名方法 + Dispose 解绑，防僵尸回调）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
        DeviceOptions.Add(Strings.M044);
        foreach (var d in devices)
            DeviceOptions.Add(d.Name);
        // 尝试恢复之前选中的设备筛选（若仍存在）
        DeviceFilter = DeviceOptions.Contains(current) ? current : Strings.M044;
    }

    /// <summary>过滤条件：关键字 + 状态 + 设备。</summary>
    private bool FilterWorkOrder(object obj)
    {
        if (obj is not WorkOrder w) return false;
        // 状态筛选（中文标签 → 枚举值）
        if (StatusLabelToEnum.TryGetValue(StatusFilter ?? Strings.M040, out var expected) && expected.HasValue && w.Status != expected.Value)
            return false;
        // 设备筛选
        if (!string.IsNullOrEmpty(DeviceFilter) && DeviceFilter != Strings.M044)
        {
            if (w.DeviceName != DeviceFilter) return false;
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
    partial void OnStatusFilterChanged(string value) => RefreshFilteredView();
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

    /// <summary>查询选中工单的产量聚合并更新 SelectedProduction 属性。</summary>
    private void RefreshSelectedProduction()
    {
        if (SelectedWorkOrder == null)
        {
            SelectedProduction = null;
            return;
        }
        SelectedProduction = _workOrderService.GetProductionSummary(SelectedWorkOrder);
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
        // 同步更新详情页选中工单的产量
        RefreshSelectedProduction();
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
        RefreshStatusCounts();
        NotifyActionReasonsChanged();
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
        RefreshStatusCounts();
        NotifyActionReasonsChanged();
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
        RefreshStatusCounts();
        NotifyActionReasonsChanged();
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

    /// <summary>CSV 字段转义：含逗号、双引号或换行时用双引号包裹，内部双引号翻倍。</summary>
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

        var samples = BuildSampleWorkOrders(devices);
        foreach (var wo in samples)
            await _workOrderRepo.UpsertAsync(wo);

        _dialog.NotifySuccess(string.Format(Strings.F115, samples.Count));
    }

    /// <summary>
    /// 为每台设备生成 3 个样本工单：1 个 Running、1 个 Pending、1 个 Completed 或 Aborted。
    /// 产品名称/工单号使用合理的模拟数据，计划时间围绕当前时间分布。
    /// </summary>
    private static List<WorkOrder> BuildSampleWorkOrders(IReadOnlyList<Device> devices)
    {
        var now = DateTime.Now;
        var products = new[]
        {
            ("P-1001", "外壳组件A"),
            ("P-1002", "外壳组件B"),
            ("P-2001", "电路板模组"),
            ("P-2002", "传感器模组"),
            ("P-3001", "连接器"),
            ("P-3002", "端子台"),
            ("P-4001", "散热片"),
            ("P-4002", "支架组件"),
        };

        var remarks = new[]
        {
            "常规生产批次",
            "客户加急订单",
            "试产验证",
            "返工批次",
            null,
        };

        List<WorkOrder> result = [];
        var rng = new Random(42); // 固定种子确保可复现

        for (var i = 0; i < devices.Count; i++)
        {
            var dev = devices[i];
            var product = products[i % products.Length];
            var dayOffset = i / 4; // 每 4 台设备错开一天

            // 1. Running 工单（当前进行中，计划时间覆盖现在）
            result.Add(new WorkOrder
            {
                OrderNo = $"WO-{now:yyyyMMdd}-{(i + 1):D3}-R",
                ProductCode = product.Item1,
                ProductName = product.Item2,
                DeviceId = dev.Id,
                DeviceName = dev.Name,
                TargetQuantity = 500 + rng.Next(0, 10) * 100,
                PlannedStart = now.AddDays(-dayOffset).AddHours(-6),
                PlannedEnd = now.AddDays(-dayOffset).AddHours(2),
                Status = WorkOrderStatus.Running,
                Remark = remarks[i % remarks.Length],
                CreatedAt = now.AddDays(-dayOffset).AddHours(-8),
                UpdatedAt = now.AddDays(-dayOffset).AddHours(-6),
            });

            // 2. Pending 工单（待开始，计划时间在未来）
            result.Add(new WorkOrder
            {
                OrderNo = $"WO-{now:yyyyMMdd}-{(i + 1):D3}-P",
                ProductCode = product.Item1,
                ProductName = product.Item2,
                DeviceId = dev.Id,
                DeviceName = dev.Name,
                TargetQuantity = 800 + rng.Next(0, 8) * 100,
                PlannedStart = now.AddDays(1 + dayOffset).Date.AddHours(8),
                PlannedEnd = now.AddDays(1 + dayOffset).Date.AddHours(20),
                Status = WorkOrderStatus.Pending,
                Remark = remarks[(i + 2) % remarks.Length],
                CreatedAt = now.AddDays(-1),
                UpdatedAt = now.AddDays(-1),
            });

            // 3. 已结束工单（Completed 或 Aborted，计划时间在过去）
            var completed = i % 3 != 0; // 2/3 为 Completed，1/3 为 Aborted
            result.Add(new WorkOrder
            {
                OrderNo = $"WO-{now:yyyyMMdd}-{(i + 1):D3}-{(completed ? "C" : "A")}",
                ProductCode = product.Item1,
                ProductName = product.Item2,
                DeviceId = dev.Id,
                DeviceName = dev.Name,
                TargetQuantity = 1000 + rng.Next(0, 6) * 100,
                PlannedStart = now.AddDays(-2 - dayOffset).Date.AddHours(8),
                PlannedEnd = now.AddDays(-2 - dayOffset).Date.AddHours(20),
                Status = completed ? WorkOrderStatus.Completed : WorkOrderStatus.Aborted,
                Remark = completed ? "已完成交付" : "因设备故障中止",
                CreatedAt = now.AddDays(-3 - dayOffset),
                UpdatedAt = now.AddDays(-2 - dayOffset).Date.AddHours(20),
            });
        }

        return result;
    }
}
