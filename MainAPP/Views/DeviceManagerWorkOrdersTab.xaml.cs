using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MainAPP.Views;

/// <summary>
/// 设备管理 Tab 5：工单管理（设备维度）。展示当前设备的工单列表与状态操作。
/// DataContext 由父级 DeviceManagerView 传入。
/// </summary>
public partial class DeviceManagerWorkOrdersTab
{
    public DeviceManagerWorkOrdersTab()
    {
        InitializeComponent();
    }

    /// <summary>更多下拉：编辑/删除收进 ContextMenu（与设备列表页"更多"按钮同交互）。</summary>
    private void OnWorkOrderMoreClick(object sender, RoutedEventArgs e)
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
