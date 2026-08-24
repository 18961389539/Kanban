using System;
using System.Linq;
using Kanban.Collector.Core.Models;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 设备管理 Tab 3：缺陷管理（缺陷列表 + 缺陷详情编辑）。
/// 仅 XAML 视图，无代码逻辑。DataContext 由父级 DeviceManagerView 传入。
/// </summary>
public partial class DeviceManagerDefectsTab
{
    public DeviceManagerDefectsTab()
    {
        InitializeComponent();
    }

    public void FocusAddress(string address)
    {
        if (DataContext is not DeviceDefectManagerViewModel vm || vm.SelectedDevice is not { } device)
            return;

        var defect = vm.SelectedDefect;
        if (defect == null || !string.Equals(defect.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase))
            defect = device.Defects.FirstOrDefault(item =>
                string.Equals(item.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (defect == null) return;

        vm.SelectedDefect = defect;
        Dispatcher.BeginInvoke(
            new Action(() => DeviceManagerAddressFocus.FocusTextBox(this, defect!.PlcAddress)),
            System.Windows.Threading.DispatcherPriority.Input);
    }
}
