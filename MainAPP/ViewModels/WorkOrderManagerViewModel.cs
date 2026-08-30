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
    private readonly ISnEventStore? _snEventStore;

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

    /// <summary>逾期工单 Id 集合（全量，不受筛选影响）。替换时通知 UI 使列表行标签/高亮重估。</summary>
    [ObservableProperty]
    private ISet<int> _overdueOrderIds = new HashSet<int>();

    /// <summary>计划冲突工单 Id 集合（同设备 Pending/Running 时间重叠）。替换时通知 UI 使列表行/徽章重估。</summary>
    [ObservableProperty]
    private ISet<int> _conflictOrderIds = new HashSet<int>();

    /// <summary>工单排程甘特图模型（只读，设备×时间矩形条）。筛选/集合变化时重建。</summary>
    public OxyPlot.PlotModel? WorkOrderGanttChartModel { get; private set; }

    /// <summary>可选设备筛选项（"全部设备" + 各设备，按 DeviceId 匹配）。</summary>
    public ObservableCollection<DeviceFilterOption> DeviceOptions { get; } = [new("", Strings.M044)];

    /// <summary>选中工单的产量聚合（详情页绑定）。null 表示未查询/未选中。</summary>
    [ObservableProperty]
    private WorkOrderProductionSummary? _selectedProduction;

    // ──────────── SN 明细（选中工单的序列号事件列表，Local 模式可用） ────────────

    /// <summary>SN 明细列表（详情页绑定，按时间降序）。</summary>
    public ObservableCollection<Kanban.Collector.Core.Entities.SnEventRecord> SnItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnItems))]
    private int _snTotalCount;

    [ObservableProperty]
    private int _snPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnItems))]
    private int _snTotalPages;

    private const int SnPageSize = 20;

    /// <summary>SN 追溯是否可用：注入的存储非空即可用（Local=本地库，Remote=SignalR 查询通道）。</summary>
    public bool IsSnSupported => _snEventStore != null;

    public bool HasSnItems => SnTotalCount > 0 && SnItems.Count > 0;

    partial void OnSnPageChanged(int value)
    {
        SnPreviousPageCommand.NotifyCanExecuteChanged();
        SnNextPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnSnTotalPagesChanged(int value)
    {
        SnPreviousPageCommand.NotifyCanExecuteChanged();
        SnNextPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSnPreviousPage))]
    private void SnPreviousPage()
    {
        if (SnPage > 1)
        {
            SnPage--;
            LoadSnItems();
        }
    }

    private bool CanSnPreviousPage() => SnPage > 1;

    [RelayCommand(CanExecute = nameof(CanSnNextPage))]
    private void SnNextPage()
    {
        if (SnPage < SnTotalPages)
        {
            SnPage++;
            LoadSnItems();
        }
    }

    private bool CanSnNextPage() => SnPage < SnTotalPages;

    /// <summary>查询选中工单的 SN 明细（后台执行 + UI 封送，选中工单变化时自动加载）。</summary>
    private void LoadSnItems()
    {
        var order = SelectedWorkOrder;
        if (order == null || order.Id <= 0 || _snEventStore == null)
        {
            SnItems.Clear();
            SnTotalCount = 0;
            SnTotalPages = 0;
            return;
        }

        var page = SnPage;
        var workOrderId = order.Id;
        Task.Run(() =>
        {
            var (total, items) = _snEventStore.QueryByWorkOrder(workOrderId, page, SnPageSize);
            UiDispatcher.Dispatch(() =>
            {
                if (SelectedWorkOrder?.Id != workOrderId) return; // 选中项已切换，丢弃过期结果
                SnItems.Clear();
                foreach (var record in items)
                    SnItems.Add(record);
                SnTotalCount = total;
                SnTotalPages = total <= 0 ? 0 : (total + SnPageSize - 1) / SnPageSize;
            });
        }).Forget();
    }

    /// <summary>
    /// 选中工单的逾期提示文本（详情页绑定，如"逾期 2h"；null 表示未逾期）。
    /// 不能直接绑实体 <see cref="WorkOrder.OverdueHintText"/>：实体非 INotifyPropertyChanged，
    /// 每分钟计时兜底刷新时详情页不会自动重绑；改由 VM 计算属性 + RecalcDerivedCounts 显式通知。
    /// </summary>
    [ObservableProperty]
    private string? _selectedOverdueHintText;

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

    // ──────────── 筛选结果汇总（当前 FilteredView 范围内，RefreshFilteredView 时重算） ────────────
    /// <summary>当前筛选结果的总目标产量（TargetQuantity 求和）。</summary>
    [ObservableProperty] private long _filteredTargetTotal;
    /// <summary>当前筛选结果的合格产量（Production.OkCount 求和；未刷新产量时为 0）。</summary>
    [ObservableProperty] private long _filteredOkTotal;
    /// <summary>当前筛选结果的平均达成率（TotalOk / TotalTarget，无目标时 0）。</summary>
    [ObservableProperty] private double _filteredAchievementRate;

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
        UserSession userSession,
        ISnEventStore? snEventStore = null)
    {
        _workOrderRepo = workOrderRepo;
        _workOrderService = workOrderService;
        _deviceRepo = deviceRepo;
        _dialog = dialog;
        _userSession = userSession;
        _snEventStore = snEventStore;

        // 创建独立的 ListCollectionView（不能用 GetDefaultView，否则与其他 ViewModel 共享同一视图导致 Filter 互相覆盖）
        FilteredView = new ListCollectionView(_workOrderRepo.WorkOrders);
        FilteredView.Filter = FilterWorkOrder;

        RefreshDeviceOptions();
        RefreshStatusCounts();
        ApplySort();
        // 订阅集合变化：工单增删/状态切换后重算各状态计数（命名方法，Dispose 时解绑）
        _workOrderRepo.WorkOrders.CollectionChanged += OnWorkOrdersCollectionChanged;
        // 每分钟兜底刷新时间相关计数 + 甘特"当前时刻线"右移（两者都依赖 DateTime.Now，无集合事件可订阅）。
        // 甘特重建仅当折叠区展开时由 PlotView 绑定更新生效（折叠时重建是廉价空操作）。
        _derivedCountsTimer.Tick += (_, _) =>
        {
            RecalcDerivedCounts();
            RefreshGanttForCurrentFilter();
        };
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

        // 逾期/冲突 Id 集合（替换引用触发 UI 通知，列表行用 CollectionContainsConverter 判定）
        OverdueOrderIds = _workOrderRepo.WorkOrders.Where(IsOverdue).Select(w => w.Id).ToHashSet();
        ConflictOrderIds = ComputeConflictOrderIds(_workOrderRepo.GetSnapshot());

        // 回填实体运行时标记（详情页"逾期 2h"/冲突标识绑定；选中变化时重绑，无需实体自带 INPC）
        foreach (var w in _workOrderRepo.WorkOrders)
        {
            w.IsOverdue = IsOverdue(w);
            w.OverdueHintText = FormatOverdueHint(w);
            w.IsScheduleConflict = ConflictOrderIds.Contains(w.Id);
        }

        // 详情页逾期徽章经 VM 计算属性驱动（实体无 INPC，分钟级计时刷新必须显式通知）
        RefreshSelectedOverdueHint();
    }

    /// <summary>按当前选中工单刷新详情页逾期提示文本（选中变化与定时兜底均触发）。</summary>
    private void RefreshSelectedOverdueHint()
        => SelectedOverdueHintText = SelectedWorkOrder == null
            ? null
            : FormatOverdueHint(SelectedWorkOrder);

    /// <summary>生成逾期提示文本（如"逾期 2h"）；未逾期返回 null。</summary>
    private static string? FormatOverdueHint(WorkOrder w)
    {
        if (!IsOverdue(w) || w.PlannedEnd == default) return null;
        var overdue = DateTime.Now - w.PlannedEnd;
        var duration = overdue.TotalHours < 1
            ? Math.Max(1, (int)overdue.TotalMinutes) + "m"
            : (int)overdue.TotalHours + "h";
        return string.Format(Strings.K799, duration);
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
        RecalcDerivedCounts(); // 集合与实体运行时标记（与计数同源，避免两处口径漂移）
        RefreshFilteredView(); // 汇总条依赖最新产量回填
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
        var current = FilteredView.OfType<WorkOrder>().ToList();
        FilteredCount = current.Count;

        // 汇总条：目标总量 / 合格总量 / 平均达成率（按当前筛选结果实时重算）
        long targetTotal = 0, okTotal = 0;
        foreach (var w in current)
        {
            if (w.TargetQuantity > 0) targetTotal += w.TargetQuantity;
            if (w.Production?.OkCount is { } ok && ok > 0) okTotal += ok;
        }
        FilteredTargetTotal = targetTotal;
        FilteredOkTotal = okTotal;
        FilteredAchievementRate = targetTotal > 0 ? (double)okTotal / targetTotal : 0;

        // 排程甘特随筛选联动：仅显示当前筛选结果对应的设备与工单
        RefreshGanttChart(current);
    }

    /// <summary>重建工单排程甘特图（跟随当前筛选结果；冲突集合来自 RecalcDerivedCounts 的最新值）。</summary>
    private void RefreshGanttChart(IReadOnlyList<WorkOrder> filtered)
    {
        var devices = _deviceRepo.GetDevicesSnapshot()
            .Where(d => filtered.Any(w => w.DeviceId == d.Id))
            .ToList();
        // 孤立工单（设备已删）无法投影行，回退显示设备集合含其 DeviceId —— 保守取已存在设备
        OxyPlot.PlotModel? chart = null;
        try
        {
            chart = MainAPP.Services.ChartService.BuildWorkOrderGanttChart(
                devices.Count > 0 ? devices : _deviceRepo.GetDevicesSnapshot(),
                filtered,
                ConflictOrderIds);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "构建工单排程甘特图失败，回退空模型");
            chart = new OxyPlot.PlotModel();
        }
        WorkOrderGanttChartModel = chart;
        OnPropertyChanged(nameof(WorkOrderGanttChartModel));
    }

    /// <summary>按当前筛选视图重建甘特图（timer 每分钟调用，驱动"当前时刻线"随时间右移）。</summary>
    private void RefreshGanttForCurrentFilter()
        => RefreshGanttChart(FilteredView.OfType<WorkOrder>().ToList());

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

    /// <summary>返回参与计划冲突的工单 Id 集合（与 <see cref="CountScheduleConflicts"/> 同一重叠判定）。
    /// 仅当两台工单计划区间真正重叠时两端都计入，供列表行高亮与详情冲突标识使用。</summary>
    private static ISet<int> ComputeConflictOrderIds(IReadOnlyList<WorkOrder> workOrders)
    {
        var conflictIds = new HashSet<int>();
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
                {
                    conflictIds.Add(ordered[i].Id);
                    conflictIds.Add(ordered[j].Id);
                }
            }
        }
        return conflictIds;
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
        // 选中变化立即刷新详情页逾期徽章（由 RecalcDerivedCounts 定时兜底，此处覆盖"切换时即最新"）
        RefreshSelectedOverdueHint();
        // SN 明细：重置到第 1 页并加载选中工单的序列号事件
        SnPage = 1;
        LoadSnItems();
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
    /// 与 <see cref="ImportCsv"/> 互为往返（表头同源 M346）。
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

    /// <summary>
    /// 从 CSV 文件批量导入工单（模板与导出互往返，表头同源 M346）。
    /// 逐行校验（工单号唯一、设备存在、计划时间有效），失败行跳过并提示明细；
    /// 导入的工单一律为 Pending 状态（供排产），已结束工单请用导出归档。
    /// </summary>
    [RelayCommand]
    private async Task ImportCsv()
    {
        var path = _dialog.ShowOpenFileDialog(Strings.K751, Strings.M310);
        if (string.IsNullOrEmpty(path)) return;

        List<WorkOrder> candidates;
        List<string> skipErrors;
        try
        {
            (candidates, skipErrors) = ReadWorkOrdersFromCsv(path);
        }
        catch (Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.K755, ex.Message));
            return;
        }

        if (candidates.Count == 0)
        {
            _dialog.NotifyWarning(string.Format(Strings.K756, Path.GetFileName(path)));
            return;
        }

        // 二次确认：避免误选文件批量写库
        var confirm = _dialog.Show(
            string.Format(Strings.K752, candidates.Count),
            Strings.K750, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var result = await _workOrderService.ImportWorkOrdersAsync(candidates);
        // 解析阶段跳过的行与业务校验失败统一汇总提示，避免用户以为全部导入成功
        var errors = skipErrors.Concat(result.Errors).Take(10).ToList();
        var hiddenCount = skipErrors.Count + result.Errors.Count - errors.Count;
        if (errors.Count > 0)
        {
            var detail = string.Join("\n", errors);
            if (hiddenCount > 0)
                detail += string.Format(Strings.M_MoreNotShown, hiddenCount);
            _dialog.NotifyWarning(string.Format(Strings.K754, result.Imported.Count, errors.Count, detail));
        }
        else
        {
            _dialog.NotifySuccess(string.Format(Strings.K753, result.Imported.Count));
        }
        RefreshStatusCounts();
    }

    /// <summary>读取工单 CSV（UTF-8 兼容 BOM，表头按 M346 列名匹配；简单解析，日期格式与导出一致）。
    /// 返回 (有效行, 解析跳过说明)。解析失败的行不静默丢弃——计入跳过列表，最终与业务校验错误一起提示。</summary>
    private static (List<WorkOrder> Rows, List<string> SkipErrors) ReadWorkOrdersFromCsv(string path)
    {
        var rows = new List<WorkOrder>();
        var skipErrors = new List<string>();
        using var reader = new StreamReader(path, new System.Text.UTF8Encoding(true), detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) return (rows, skipErrors);

        // 表头列名可能按界面语言本地化，但列顺序固定（与 M346 一致）：
        // 工单号,产品编码,产品名称,设备名,计划产量,计划开始,计划结束,状态,备注,创建时间,更新时间
        // 导入仅取前 7 列 + 备注（状态/时间戳列忽略，导入统一 Pending）。
        string? line;
        var lineNo = 1;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            if (fields.Length < 7)
            {
                skipErrors.Add(string.Format(Strings.K790, lineNo, fields.Length));
                continue;
            }

            if (!DateTime.TryParseExact(fields[5], "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var start)
                || !DateTime.TryParseExact(fields[6], "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var end))
            {
                skipErrors.Add(string.Format(Strings.K791, lineNo, fields[0]));
                continue;
            }

            rows.Add(new WorkOrder
            {
                OrderNo = fields[0],
                ProductCode = fields[1],
                ProductName = fields[2],
                DeviceName = fields[3],
                TargetQuantity = int.TryParse(fields[4], out var qty) ? qty : 0,
                PlannedStart = start,
                PlannedEnd = end,
                Remark = fields.Length > 8 ? fields[8] : null,
            });
        }
        return (rows, skipErrors);
    }

    /// <summary>简易 CSV 行解析：支持 RFC 4180 双引号包裹（字段内逗号/引号），用于工单导入。</summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    /// <summary>复制选中工单号到剪贴板。</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopyOrderNo()
    {
        if (SelectedWorkOrder == null) return;
        System.Windows.Clipboard.SetText(SelectedWorkOrder.OrderNo);
        _dialog.NotifyInfo(string.Format(Strings.K757, SelectedWorkOrder.OrderNo));
    }

    /// <summary>复制选中工单产品编码到剪贴板。</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopyProductCode()
    {
        if (SelectedWorkOrder == null) return;
        System.Windows.Clipboard.SetText(SelectedWorkOrder.ProductCode);
        _dialog.NotifyInfo(string.Format(Strings.K757, SelectedWorkOrder.ProductCode));
    }

    private bool CanCopySelected() => SelectedWorkOrder != null;

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
