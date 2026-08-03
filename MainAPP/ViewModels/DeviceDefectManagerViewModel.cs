using System.ComponentModel;
using System.Windows;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器「缺陷管理」Tab 的子 ViewModel。
/// 持有缺陷 CRUD 命令与缺陷选中状态；通过 IDeviceManagerHost 获取选中设备、
/// 感知共享 IsLoading 并回写脏标记，避免与父 DeviceManagerViewModel 形成循环依赖。
/// </summary>
public partial class DeviceDefectManagerViewModel : ObservableObject
{
    private readonly IDeviceManagerHost _host;

    /// <summary>
    /// 当前选中设备（由父 VM 的 SelectedDevice 同步）。
    /// 内层 Grid 重设 DataContext={Binding SelectedDevice} 切换到当前设备，
    /// 供缺陷列表 ItemsSource={Binding Defects} 等绑定使用。
    /// </summary>
    [ObservableProperty]
    private Device? _selectedDevice;

    [ObservableProperty]
    private Defect? _selectedDefect;

    public DeviceDefectManagerViewModel(IDeviceManagerHost host)
    {
        _host = host;
        _host.PropertyChanged += OnHostPropertyChanged;
    }

    /// <summary>解绑父级 PropertyChanged 订阅，供父 VM Dispose 时调用。</summary>
    public void Detach() => _host.PropertyChanged -= OnHostPropertyChanged;

    /// <summary>
    /// 父级共享状态变更：SelectedDevice 同步到本子 VM；IsLoading 变化时刷新依赖命令可用状态。
    /// </summary>
    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => OnHostPropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName == nameof(IDeviceManagerHost.SelectedDevice))
            SelectedDevice = _host.SelectedDevice;
        else if (e.PropertyName == nameof(IDeviceManagerHost.IsLoading))
        {
            AddDefectCommand.NotifyCanExecuteChanged();
            RemoveDefectCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedDeviceChanged(Device? value)
    {
        // 切换设备时清空缺陷选中，避免残留旧设备的引用
        SelectedDefect = null;
        AddDefectCommand.NotifyCanExecuteChanged();
        RemoveDefectCommand.NotifyCanExecuteChanged();
    }

    // 选中设备且不在 PLC 写入中（避免异步回调访问已删除设备）
    private bool CanEditSelected() => SelectedDevice != null && !_host.IsLoading;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void AddDefect()
    {
        if (SelectedDevice == null) return;
        // 缺陷名在所属设备内唯一
        var baseName = string.Format(Strings.F188, SelectedDevice.Defects.Count + 1);
        var newName = DeviceManagerViewModel.EnsureUniqueName(baseName, SelectedDevice.Defects.Select(d => d.Name));
        var defect = new Defect { Name = newName };
        SelectedDevice.Defects.Add(defect);
        _host.MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveDefect(Defect defect)
    {
        if (SelectedDefect == defect) SelectedDefect = null;
        SelectedDevice?.Defects.Remove(defect);
        _host.MarkDirty();
    }
}
