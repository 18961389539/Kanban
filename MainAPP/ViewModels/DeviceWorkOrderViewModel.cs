using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Helpers;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器「工单」Tab 的子 ViewModel。
/// 持有选中工单、按设备过滤的工单视图与 6 个工单命令；
/// 业务逻辑（弹窗、状态机校验、二次确认、落库）已由 <see cref="IWorkOrderService"/> 承担，本类只做命令转发。
/// 宿主状态同步（SelectedDevice）由 <see cref="DeviceChildManagerViewModel"/> 基类提供。
/// </summary>
public partial class DeviceWorkOrderViewModel : DeviceChildManagerViewModel
{
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly IWorkOrderService _workOrderService;
    private readonly DeviceRepository _deviceRepository;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortWorkOrderCommand))]
    private WorkOrder? _selectedWorkOrder;

    private CancellationTokenSource? _selectedProductionCts;

    /// <summary>选中工单产量聚合（后台查询）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedOkCountText))]
    [NotifyPropertyChangedFor(nameof(SelectedNgCountText))]
    [NotifyPropertyChangedFor(nameof(SelectedAchievementText))]
    [NotifyPropertyChangedFor(nameof(SelectedDefectRateText))]
    private WorkOrderProductionSummary? _selectedProductionSummary;

    /// <summary>预计剩余时间（秒），用于详情面板显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRemainingText))]
    private double _selectedRemainingSeconds;

    /// <summary>预计完成时间文本。</summary>
    [ObservableProperty] private string _selectedEtaText = "—";

    public string SelectedOkCountText => SelectedProductionSummary is { } s ? s.OkCount.ToString("N0") : "—";
    public string SelectedNgCountText => SelectedProductionSummary is { } s ? s.NgCount.ToString("N0") : "—";
    public string SelectedAchievementText => SelectedProductionSummary is { } s ? s.AchievementRate.ToString("P0") : "—";
    public string SelectedDefectRateText => SelectedProductionSummary is { } s ? s.DefectRate.ToString("P1") : "—";
    public string SelectedRemainingText => SelectedWorkOrder == null ? "—" : FormatHelper.FormatDurationFull(SelectedRemainingSeconds);

    /// <summary>选中设备运行时（用于展示工单期间三态时长分解）。</summary>
    public DeviceRuntime? SelectedDeviceRuntime =>
        SelectedDevice != null && _deviceRepository.RuntimeMap.TryGetValue(SelectedDevice.Id, out var rt) ? rt : null;

    public string WorkOrderRunTimeFull => SelectedDeviceRuntime is { } rt ? FormatHelper.FormatDurationFull(rt.RunTime) : "—";
    public string WorkOrderAlarmTimeFull => SelectedDeviceRuntime is { } rt ? FormatHelper.FormatDurationFull(rt.AlarmTime) : "—";
    public string WorkOrderPausedTimeFull => SelectedDeviceRuntime is { } rt ? FormatHelper.FormatDurationFull(rt.PausedTime) : "—";

    /// <summary>当前设备配方名称。</summary>
    public string SelectedRecipeName => SelectedDevice?.RecipeName ?? "—";
    /// <summary>当前设备机型/模具类型。</summary>
    public string SelectedMachineType => SelectedDevice?.MachineType ?? "—";

    /// <summary>当前选中设备的工单过滤视图（按设备过滤，Running 优先排序）。</summary>
    public ICollectionView SelectedDeviceWorkOrders { get; }

    public DeviceWorkOrderViewModel(
        IDialogService dialog,
        IDeviceManagerHost host,
        WorkOrderRepository workOrderRepo,
        IWorkOrderService workOrderService,
        DeviceRepository deviceRepository)
        : base(dialog, host)
    {
        _workOrderRepo = workOrderRepo;
        _workOrderService = workOrderService;
        _deviceRepository = deviceRepository;

        // 独立的 ListCollectionView（不能用 GetDefaultView，否则与 WorkOrderManagerViewModel 共享同一视图导致 Filter 互相覆盖）
        SelectedDeviceWorkOrders = new ListCollectionView(_workOrderRepo.WorkOrders);
        SelectedDeviceWorkOrders.Filter = FilterWorkOrderByDevice;
        SelectedDeviceWorkOrders.SortDescriptions.Add(
            new SortDescription(nameof(WorkOrder.Status), ListSortDirection.Descending));
    }

    protected override void OnHostSelectedDeviceChanged()
    {
        SelectedWorkOrder = null;
        SelectedDeviceWorkOrders.Refresh();
        AddWorkOrderCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(SelectedDeviceRuntime));
        OnPropertyChanged(nameof(WorkOrderRunTimeFull));
        OnPropertyChanged(nameof(WorkOrderAlarmTimeFull));
        OnPropertyChanged(nameof(WorkOrderPausedTimeFull));
        OnPropertyChanged(nameof(SelectedRecipeName));
        OnPropertyChanged(nameof(SelectedMachineType));
    }

    partial void OnSelectedWorkOrderChanged(WorkOrder? value)
    {
        _selectedProductionCts?.Cancel();
        _selectedProductionCts?.Dispose();
        _selectedProductionCts = null;
        SelectedProductionSummary = null;
        SelectedRemainingSeconds = 0;
        SelectedEtaText = "—";

        if (value != null)
            _ = RefreshSelectedProductionAsync(value);
    }

    private async Task RefreshSelectedProductionAsync(WorkOrder order)
    {
        var cts = new CancellationTokenSource();
        _selectedProductionCts = cts;
        WorkOrderProductionSummary summary;
        try
        {
            summary = await Task.Run(() => _workOrderService.GetProductionSummary(order), cts.Token);
        }
        catch
        {
            if (cts.IsCancellationRequested) return;
            summary = new WorkOrderProductionSummary();
        }

        if (cts.IsCancellationRequested || SelectedWorkOrder?.Id != order.Id) return;
        SelectedProductionSummary = summary;
        UpdateEstimateTexts(order, summary);
        cts.Dispose();
    }

    private void UpdateEstimateTexts(WorkOrder order, WorkOrderProductionSummary summary)
    {
        if (order.Status == WorkOrderStatus.Completed || order.Status == WorkOrderStatus.Aborted)
        {
            SelectedRemainingSeconds = 0;
            SelectedEtaText = DateTime.Now.ToString("MM-dd HH:mm");
            return;
        }

        var plannedSeconds = Math.Max(0, (order.PlannedEnd - order.PlannedStart).TotalSeconds);
        var achievement = summary.AchievementRate;
        double remainingSeconds;
        DateTime eta;

        if (achievement <= 0)
        {
            remainingSeconds = plannedSeconds;
            eta = order.PlannedEnd;
        }
        else
        {
            remainingSeconds = Math.Max(0, plannedSeconds * (1 - achievement));
            eta = DateTime.Now.AddSeconds(remainingSeconds);
        }

        SelectedRemainingSeconds = remainingSeconds;
        SelectedEtaText = eta.ToString("MM-dd HH:mm");
    }

    private bool FilterWorkOrderByDevice(object obj)
        => obj is WorkOrder w && SelectedDevice != null && w.DeviceId == SelectedDevice.Id;

    /// <summary>新增工单按钮可用性：选中设备即可新增（预填当前设备）。</summary>
    private bool CanAddWorkOrder() => SelectedDevice != null;

    private bool CanEditWorkOrder() => SelectedWorkOrder != null;

    private bool CanDeleteWorkOrder() => SelectedWorkOrder != null;

    private bool CanStartWorkOrder() => SelectedWorkOrder != null && SelectedWorkOrder.Status == WorkOrderStatus.Pending;

    private bool CanCompleteWorkOrder() => SelectedWorkOrder != null && SelectedWorkOrder.Status == WorkOrderStatus.Running;

    private bool CanAbortWorkOrder() => SelectedWorkOrder != null
        && (SelectedWorkOrder.Status == WorkOrderStatus.Running || SelectedWorkOrder.Status == WorkOrderStatus.Pending);

    /// <summary>新增工单：以当前选中设备预填模板打开编辑对话框（Id=0 表示新增）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddWorkOrder))]
    private async Task AddWorkOrder()
    {
        if (SelectedDevice == null) return;
        var template = new WorkOrder
        {
            DeviceId = SelectedDevice.Id,
            DeviceName = SelectedDevice.Name,
        };
        var saved = await _workOrderService.AddWorkOrderAsync(template);
        if (saved != null) SelectedWorkOrder = saved;
    }

    [RelayCommand(CanExecute = nameof(CanEditWorkOrder))]
    private async Task EditWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.EditWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteWorkOrder))]
    private async Task DeleteWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        if (await _workOrderService.DeleteWorkOrderAsync(SelectedWorkOrder))
            SelectedWorkOrder = null;
    }

    [RelayCommand(CanExecute = nameof(CanStartWorkOrder))]
    private async Task StartWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.StartWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    [RelayCommand(CanExecute = nameof(CanCompleteWorkOrder))]
    private async Task CompleteWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.CompleteWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }

    [RelayCommand(CanExecute = nameof(CanAbortWorkOrder))]
    private async Task AbortWorkOrder()
    {
        if (SelectedWorkOrder == null) return;
        var saved = await _workOrderService.AbortWorkOrderAsync(SelectedWorkOrder);
        if (saved != null) SelectedWorkOrder = saved;
    }
}
