using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Kanban.Collector.Core.Entities;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 工单管理页 code-behind。DataContext 由 MainWindow.xaml.cs 设置为 WorkOrderManagerViewModel。
/// </summary>
public partial class WorkOrderManagerView : UserControl
{
    public WorkOrderManagerView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 页面级快捷键（Preview 隧道事件）：
    /// - Ctrl+F：焦点切到工单搜索框（与设备管理/产线页一致，全站搜索统一入口）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            WorkOrderSearchBox.Focus();
            Keyboard.Focus(WorkOrderSearchBox);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 进入页面（页面切换为当前页时 Visibility 变 Visible）自动聚焦搜索框，
    /// 免点击直接输入关键字过滤工单。
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible)
                {
                    WorkOrderSearchBox.Focus();
                    Keyboard.Focus(WorkOrderSearchBox);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 列表键盘操作（焦点在工单列表时）：
    /// - Enter：编辑当前选中工单（与双击行为一致）。
    /// - Delete：删除当前选中工单（DeleteCommand 内部有二次确认）。
    /// </summary>
    private void OnWorkOrderListKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not WorkOrderManagerViewModel vm) return;

        if (e.Key == Key.Enter && vm.EditCommand.CanExecute(null))
        {
            vm.EditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && vm.DeleteCommand.CanExecute(null))
        {
            vm.DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 状态筛选 chip 点击：将 RadioButton.Tag（WorkOrderStatus?，null=全部）写入 ViewModel.StatusFilter。
    /// IsChecked 通过 OneWay 绑定 + EqualityConverter 同步显示态，此处仅负责写入。
    /// </summary>
    private void OnStatusChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && DataContext is WorkOrderManagerViewModel vm)
        {
            vm.StatusFilter = fe.Tag as WorkOrderStatus?;
        }
    }

    /// <summary>
    /// 双击列表行 = 快速编辑（建议 #2）：选中行已由 SelectedItem 绑定同步，直接触发编辑命令。
    /// 双击空白处（无选中行）不动作。
    /// </summary>
    private void OnWorkOrderListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox
            && listBox.SelectedItem is WorkOrder
            && DataContext is WorkOrderManagerViewModel vm)
        {
            vm.EditCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 右键菜单定位（建议 #3）：右键行先设为选中，保证行内右键菜单的命令作用于该行。
    /// 菜单命令绑定无 CommandParameter，依赖 SelectedWorkOrder。
    /// </summary>
    private void OnWorkOrderListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            // 命中测试定位到 ListBoxItem（行），右键时把它选中
            var hit = e.OriginalSource as DependencyObject;
            while (hit is not null and not ListBoxItem && hit is not ListBox)
            {
                hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
            }
            if (hit is ListBoxItem item)
            {
                item.IsSelected = true;
            }
        }
    }

    /// <summary>
    /// 更多下拉：复制 / 生成样本（低频操作收进 ContextMenu，与设备管理页同交互）。
    /// </summary>
    private void OnMoreActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is { } menu)
        {
            menu.PlacementTarget = btn;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 4;
            menu.DataContext = btn.DataContext;
            menu.IsOpen = true;
        }
    }
}