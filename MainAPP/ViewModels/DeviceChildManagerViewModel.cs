using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanban.Collector.Core.Models;
using MainAPP.Services;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器「报警 / 缺陷 / 计数报警」三个子 Tab ViewModel 的共享基类。
/// 统一 SelectedDevice 同步、父级 PropertyChanged 的 Dispatcher 封送、Detach 解绑与 CanEditSelected，
/// 消除三个子 VM 中重复的宿主状态同步样板。子类保留各自的选中项与命令名，XAML 绑定保持不变。
/// </summary>
public abstract partial class DeviceChildManagerViewModel : ObservableObject
{
    protected readonly IDeviceManagerHost _host;
    protected readonly IDialogService _dialog;

    /// <summary>当前选中设备（由父 VM 的 SelectedDevice 同步）。</summary>
    [ObservableProperty]
    private Device? _selectedDevice;

    public bool CanManageDevices => _host.CanManageDevices;

    public bool IsConfigurationReadOnly => !CanManageDevices;

    /// <summary>子 Tab 的 CSV 操作进行中时，连同普通 CRUD 一起锁定。</summary>
    protected virtual bool IsCsvBusy => false;

    protected DeviceChildManagerViewModel(IDialogService dialog, IDeviceManagerHost host)
    {
        _dialog = dialog;
        _host = host;
        _host.PropertyChanged += OnHostPropertyChanged;
    }

    /// <summary>解绑父级 PropertyChanged 订阅，供父 VM Dispose 时调用。</summary>
    public void Detach() => _host.PropertyChanged -= OnHostPropertyChanged;

    /// <summary>父级 SelectedDevice 变化后的子类钩子（清空各自的选中项 + 刷新命令可用状态）。</summary>
    protected virtual void OnHostSelectedDeviceChanged() { }

    /// <summary>父级 IsLoading 变化后的子类钩子（刷新命令可用状态）。</summary>
    protected virtual void OnHostIsLoadingChanged() { }

    /// <summary>父级设备管理权限变化后的子类钩子（刷新命令可用状态）。</summary>
    protected virtual void OnHostPermissionChanged() { }

    /// <summary>子类补充处理父级其它属性变化（如 IsPlcConnected）。</summary>
    protected virtual void OnHostOtherPropertyChanged(string? propertyName) { }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 父级属性可能在 PLC 采集后台线程被触发（如 IsPlcConnected），必须封送回 UI 线程
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => OnHostPropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName == nameof(IDeviceManagerHost.SelectedDevice))
        {
            SelectedDevice = _host.SelectedDevice;
            OnHostSelectedDeviceChanged();
        }
        else if (e.PropertyName == nameof(IDeviceManagerHost.IsLoading))
        {
            OnHostIsLoadingChanged();
        }
        else if (e.PropertyName == nameof(IDeviceManagerHost.CanManageDevices))
        {
            OnPropertyChanged(nameof(CanManageDevices));
            OnPropertyChanged(nameof(IsConfigurationReadOnly));
            OnHostPermissionChanged();
        }
        else
        {
            OnHostOtherPropertyChanged(e.PropertyName);
        }
    }

    /// <summary>选中设备且不在 PLC 写入中（避免异步回调访问已删除设备）。</summary>
    protected bool CanEditSelected() => SelectedDevice != null
        && !_host.IsLoading
        && _host.CanManageDevices
        && !IsCsvBusy;
}
