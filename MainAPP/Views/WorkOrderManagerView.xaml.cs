using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
}