using System;
using System.Linq;
using Kanban.Collector.Core.Models;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 设备管理 Tab 2：报警管理（报警列表 + 报警详情编辑）。
/// 仅 XAML 视图，无代码逻辑。DataContext 由父级 DeviceManagerView 传入。
/// </summary>
public partial class DeviceManagerAlarmsTab
{
    public DeviceManagerAlarmsTab()
    {
        InitializeComponent();
    }

    public void FocusAddress(string address)
    {
        if (DataContext is not DeviceAlarmManagerViewModel vm || vm.SelectedDevice is not { } device)
            return;

        var alarm = vm.SelectedAlarm;
        if (alarm == null || !string.Equals(alarm.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase))
            alarm = device.Alarms.FirstOrDefault(item =>
                string.Equals(item.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (alarm == null) return;

        vm.SelectedAlarm = alarm;
        Dispatcher.BeginInvoke(
            new Action(() => DeviceManagerAddressFocus.FocusTextBox(this, alarm!.PlcAddress)),
            System.Windows.Threading.DispatcherPriority.Input);
    }
}
