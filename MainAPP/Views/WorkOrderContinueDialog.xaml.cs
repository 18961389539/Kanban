using System.Windows;
using Kanban.Collector.Core.Entities;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.Views;

/// <summary>
/// 工单完成后的三选一弹窗：选择其他待开始工单、新建、或复制当前工单继续生产。
/// 关闭/稍后再说返回 <see cref="WorkOrderContinueChoice.Dismissed"/>，当前工单仍保持已完成。
/// </summary>
public partial class WorkOrderContinueDialog : Window
{
    public WorkOrderContinueDialog(WorkOrder completed, IReadOnlyList<WorkOrder> selectableOrders)
    {
        InitializeComponent();
        Message = string.Format(Strings.Wo_ContinueMessage, completed.OrderNo);
        PendingOrders = selectableOrders
            .Select(order => new WorkOrderContinueListItem(
                order,
                string.Format(Strings.Wo_ContinueItemFormat, order.OrderNo, order.ProductName, order.DeviceName)))
            .ToList();
        SelectedPending = PendingOrders.FirstOrDefault();
        DataContext = this;
    }

    public string Message { get; }

    public IReadOnlyList<WorkOrderContinueListItem> PendingOrders { get; }

    public bool HasPendingOrders => PendingOrders.Count > 0;

    public WorkOrderContinueListItem? SelectedPending { get; set; }

    /// <summary>用户确认后的选择。取消/关闭时为 <see cref="WorkOrderContinueChoice.Dismissed"/>。</summary>
    public WorkOrderContinueChoice Result { get; private set; } = WorkOrderContinueChoice.Dismissed;

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPending?.Order is not { } order)
        {
            HandyControl.Controls.Growl.Warning(Strings.Wo_ContinueNoPending);
            return;
        }

        CloseWith(WorkOrderContinueChoice.ForSelect(order));
    }

    private void Create_Click(object sender, RoutedEventArgs e)
        => CloseWith(WorkOrderContinueChoice.ForCreate);

    private void Copy_Click(object sender, RoutedEventArgs e)
        => CloseWith(WorkOrderContinueChoice.ForCopy);

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseWith(WorkOrderContinueChoice choice)
    {
        Result = choice;
        DialogResult = true;
        Close();
    }
}

/// <summary>待开始工单下拉项。</summary>
public sealed class WorkOrderContinueListItem
{
    public WorkOrderContinueListItem(WorkOrder order, string displayText)
    {
        Order = order;
        DisplayText = displayText;
    }

    public WorkOrder Order { get; }

    public string DisplayText { get; }
}
