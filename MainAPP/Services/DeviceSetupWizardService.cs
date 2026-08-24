using System.Windows;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.ViewModels;
using MainAPP.Views;

namespace MainAPP.Services;

public interface IDeviceSetupWizardService
{
    /// <summary>显示新增设备向导；用户取消时返回 null。</summary>
    Device? Show(IReadOnlyList<Device> existingDevices, IPlcAddressCodec addressCodec);
}

/// <summary>
/// 新增设备向导的 WPF 宿主。ViewModel 只依赖这个窄接口，便于单元测试时不创建窗口。
/// </summary>
public sealed class DeviceSetupWizardService : IDeviceSetupWizardService
{
    public Device? Show(IReadOnlyList<Device> existingDevices, IPlcAddressCodec addressCodec)
    {
        var viewModel = new DeviceSetupWizardViewModel(existingDevices, addressCodec);
        var window = new DeviceSetupWizardWindow(viewModel);
        if (Application.Current?.MainWindow is Window owner)
            window.Owner = owner;

        return window.ShowDialog() == true ? viewModel.Result : null;
    }
}