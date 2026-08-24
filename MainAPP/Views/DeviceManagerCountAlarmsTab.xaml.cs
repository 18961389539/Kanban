using System;
using System.Linq;
using Kanban.Collector.Core.Models;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 设备管理 Tab 4：计数报警（计数报警列表 + 详情编辑 + 当前值监控）。
/// 仅 XAML 视图，无代码逻辑。DataContext 由父级 DeviceManagerView 传入。
/// </summary>
public partial class DeviceManagerCounterAlarmsTab
{
    public DeviceManagerCounterAlarmsTab()
    {
        InitializeComponent();
    }

    public void FocusAddress(string address)
    {
        if (DataContext is not DeviceCounterAlarmManagerViewModel vm || vm.SelectedDevice is not { } device)
            return;

        var alarm = vm.SelectedCounterAlarm;
        if (alarm == null || !string.Equals(alarm.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase))
            alarm = device.CounterAlarms.FirstOrDefault(item =>
                string.Equals(item.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (alarm == null) return;

        vm.SelectedCounterAlarm = alarm;
        Dispatcher.BeginInvoke(
            new Action(() => DeviceManagerAddressFocus.FocusTextBox(this, alarm!.PlcAddress)),
            System.Windows.Threading.DispatcherPriority.Input);
    }
}
