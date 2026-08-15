using System.Windows;
using System.Windows.Controls;
using Kanban.Core.Entities;
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
}
