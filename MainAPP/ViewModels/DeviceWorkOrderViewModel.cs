using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Services;

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteWorkOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortWorkOrderCommand))]
    private WorkOrder? _selectedWorkOrder;

    /// <summary>当前选中设备的工单过滤视图（按设备过滤，Running 优先排序）。</summary>
    public ICollectionView SelectedDeviceWorkOrders { get; }

    public DeviceWorkOrderViewModel(
        IDialogService dialog,
        IDeviceManagerHost host,
        WorkOrderRepository workOrderRepo,
        IWorkOrderService workOrderService)
        : base(dialog, host)
    {
        _workOrderRepo = workOrderRepo;
        _workOrderService = workOrderService;

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
