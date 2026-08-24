using System;
using System.Linq;
using Kanban.Collector.Core.Models;
using MainAPP.ViewModels;
using System.Windows.Controls;

namespace MainAPP.Views;

/// <summary>
/// 设备管理器「数据采集源」Tab（温湿度/能耗等新维度数据，设计稿《采集模块扩展设计方案》）。
/// </summary>
public partial class DeviceManagerSourcesTab : UserControl
{
    public DeviceManagerSourcesTab()
    {
        InitializeComponent();
    }

    public void FocusAddress(string address)
    {
        if (DataContext is not DeviceDataSourceManagerViewModel vm || vm.SelectedDevice is not { } device)
            return;

        var source = vm.SelectedSource;
        if (source == null
            || (!string.Equals(source.TriggerAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase)
                && !source.Values.Any(value => string.Equals(value.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase))))
        {
            source = device.Sources.FirstOrDefault(item =>
                string.Equals(item.TriggerAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.Values.Any(value => string.Equals(value.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase)));
        }
        if (source == null) return;

        vm.SelectedSource = source;
        if (source.Values.FirstOrDefault(value =>
                string.Equals(value.PlcAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase)) is { } value)
        {
            vm.SelectedValue = value;
            DeviceManagerAddressFocus.BeginEditAddress(ValuesGrid, value, address, columnIndex: 2);
        }
        else
        {
            DeviceManagerAddressFocus.BeginEditAddress(SourcesGrid, source, address, columnIndex: 2);
        }
    }
}