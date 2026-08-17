using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Models;
using MainAPP.Services;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器「数据采集源」Tab 的子 ViewModel。
/// 持有数据源（温湿度/能耗等）CRUD 命令与选中状态；
/// 宿主状态同步（SelectedDevice / IsLoading 封送）由 <see cref="DeviceChildManagerViewModel"/> 基类提供。
/// 数据源编辑与设备报警/缺陷/计数报警一致：改动仅标记脏，统一由 Save 按钮校验后持久化。
/// </summary>
public partial class DeviceDataSourceManagerViewModel : DeviceChildManagerViewModel
{
    [ObservableProperty]
    private DataSource? _selectedSource;

    public DeviceDataSourceManagerViewModel(
        IDialogService dialog,
        IDeviceManagerHost host)
        : base(dialog, host)
    {
    }

    protected override void OnHostSelectedDeviceChanged()
    {
        // 切换设备时清空数据源选中，避免残留旧设备的引用
        SelectedSource = null;
        AddSourceCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostIsLoadingChanged()
    {
        AddSourceCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>新增数据源：默认追加到当前设备 Sources 末尾，由 Save 统一校验持久化。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void AddSource()
    {
        if (SelectedDevice == null) return;
        var baseName = string.Format(Strings.F198, SelectedDevice.Sources.Count + 1);
        var newName = DeviceManagerViewModel.EnsureUniqueName(baseName, SelectedDevice.Sources.Select(s => s.Name));
        var source = new DataSource { Name = newName, Type = "温湿度" };
        SelectedDevice.Sources.Add(source);
        // 不立即 SaveAll：统一由 Save 按钮校验（含阈值/地址规则）后持久化
        _host.MarkDirty();
    }

    /// <summary>删除选中数据源（确认后）。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveSource(DataSource source)
    {
        if (source == null) return;
        var confirm = _dialog.Show(
            string.Format(Strings.F503, source.Name),
            Strings.M118, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (SelectedSource == source) SelectedSource = null;
        SelectedDevice?.Sources.Remove(source);
        // 不立即 SaveAll：统一由 Save 按钮持久化，与 AddAlarm/RemoveAlarm/Defect 行为一致
        _host.MarkDirty();
    }
}