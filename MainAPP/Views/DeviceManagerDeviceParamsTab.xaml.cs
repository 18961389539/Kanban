using System;
using System.Windows;
using System.Windows.Controls;

namespace MainAPP.Views;

/// <summary>
/// 设备管理 Tab 1：设备参数（基本参数 / PLC 地址配置 / 配方参数）。
/// DataContext 由父级 DeviceManagerView 传入。
/// </summary>
public partial class DeviceManagerDeviceParamsTab
{
    public DeviceManagerDeviceParamsTab()
    {
        InitializeComponent();
    }

    public void FocusAddress(string address)
    {
        var normalized = address.Trim();
        TextBox? target = normalized.Equals(GetBoundAddress(OkAddressTextBox), StringComparison.OrdinalIgnoreCase)
            ? OkAddressTextBox
            : normalized.Equals(GetBoundAddress(NgAddressTextBox), StringComparison.OrdinalIgnoreCase)
                ? NgAddressTextBox
                : normalized.Equals(GetBoundAddress(StatusAddressTextBox), StringComparison.OrdinalIgnoreCase)
                    ? StatusAddressTextBox
                    : normalized.Equals(GetBoundAddress(ResetAddressTextBox), StringComparison.OrdinalIgnoreCase)
                        ? ResetAddressTextBox
                        : null;
        if (target == null) return;
        target.Focus();
        target.SelectAll();
    }

    private static string GetBoundAddress(TextBox textBox) => textBox.Text.Trim();
}
