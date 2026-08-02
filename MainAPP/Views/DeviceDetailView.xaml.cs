using System.Windows.Controls;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 设备详情页：单设备深度监控视图。
/// 显示当前选中设备的 KPI、OEE 分解、状态时长、活跃报警、最近事件。
/// 通过 IDeviceSelectionService.SelectedDeviceId 跨页同步选中设备。
/// </summary>
public partial class DeviceDetailView : UserControl
{
    public DeviceDetailView()
    {
        InitializeComponent();
    }
}
