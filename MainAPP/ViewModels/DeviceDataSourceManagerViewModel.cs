using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Models;
using MainAPP.Services;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器「数据采集源」Tab 的子 ViewModel（多值重构版）。
/// 两级结构：源（容器：标识 + 触发配置）与值项（采集地址 + 判定参数）。
/// 持有源/值项/枚举值 CRUD 命令与选中状态；
/// 宿主状态同步（SelectedDevice / IsLoading 封送）由 <see cref="DeviceChildManagerViewModel"/> 基类提供。
/// 数据源编辑与设备报警/缺陷/计数报警一致：改动仅标记脏，统一由 Save 按钮校验后持久化。
/// </summary>
public partial class DeviceDataSourceManagerViewModel : DeviceChildManagerViewModel
{
    [ObservableProperty]
    private DataSource? _selectedSource;

    /// <summary>选中源的值项表格选中行。</summary>
    [ObservableProperty]
    private DataSourceValue? _selectedValue;

    /// <summary>值项枚举取值表选中行。</summary>
    [ObservableProperty]
    private DataSourceEnumValue? _selectedEnumValue;

    public DeviceDataSourceManagerViewModel(
        IDialogService dialog,
        IDeviceManagerHost host)
        : base(dialog, host)
    {
    }

    /// <summary>切换选中源时刷新命令可用状态并清空选中行。</summary>
    partial void OnSelectedSourceChanged(DataSource? value)
    {
        SelectedValue = null;
        SelectedEnumValue = null;
        AddValueCommand.NotifyCanExecuteChanged();
        RemoveValueCommand.NotifyCanExecuteChanged();
        AddEnumValueCommand.NotifyCanExecuteChanged();
        RemoveEnumValueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>切换选中值项时清空枚举选中行并刷新枚举命令。</summary>
    partial void OnSelectedValueChanged(DataSourceValue? value)
    {
        SelectedEnumValue = null;
        AddEnumValueCommand.NotifyCanExecuteChanged();
        RemoveEnumValueCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostSelectedDeviceChanged()
    {
        // 切换设备时清空选中，避免残留旧设备的引用
        SelectedSource = null;
        SelectedValue = null;
        SelectedEnumValue = null;
        AddSourceCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        AddValueCommand.NotifyCanExecuteChanged();
        RemoveValueCommand.NotifyCanExecuteChanged();
        AddEnumValueCommand.NotifyCanExecuteChanged();
        RemoveEnumValueCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostIsLoadingChanged()
    {
        AddSourceCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        AddValueCommand.NotifyCanExecuteChanged();
        RemoveValueCommand.NotifyCanExecuteChanged();
        AddEnumValueCommand.NotifyCanExecuteChanged();
        RemoveEnumValueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>新增数据源：默认带一个值项，由 Save 统一校验持久化。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void AddSource()
    {
        if (SelectedDevice == null) return;
        var baseName = string.Format(Strings.F198, SelectedDevice.Sources.Count + 1);
        var newName = DeviceManagerViewModel.EnsureUniqueName(baseName, SelectedDevice.Sources.Select(s => s.Name));
        var source = new DataSource { DeviceId = SelectedDevice.Id, Name = newName };
        source.Values.Add(new DataSourceValue { Name = "值1" });
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

    /// <summary>值项可编辑性：选中源且不在加载中（基类 CanEditSelected 校验设备）。</summary>
    private bool CanEditValue() => SelectedSource != null && !_host.IsLoading;

    /// <summary>新增值项（当前选中源）。</summary>
    [RelayCommand(CanExecute = nameof(CanEditValue))]
    private void AddValue()
    {
        var source = SelectedSource;
        if (source == null) return;
        var next = source.Values.Count + 1;
        source.Values.Add(new DataSourceValue { Name = $"值{next}" });
        _host.MarkDirty();
    }

    /// <summary>删除选中的值项。</summary>
    [RelayCommand(CanExecute = nameof(CanEditValue))]
    private void RemoveValue(DataSourceValue value)
    {
        if (value == null || SelectedSource == null) return;
        if (SelectedValue == value) SelectedValue = null;
        SelectedSource.Values.Remove(value);
        _host.MarkDirty();
    }

    /// <summary>枚举值行可编辑性：选中值项且不在加载中。</summary>
    private bool CanEditEnumValue() => SelectedValue != null && !_host.IsLoading;

    /// <summary>新增枚举取值映射行（值项级：非数值型的 值→显示名）。</summary>
    [RelayCommand(CanExecute = nameof(CanEditEnumValue))]
    private void AddEnumValue()
    {
        var value = SelectedValue;
        if (value == null) return;
        var nextValue = value.EnumValues.Count == 0
            ? 0
            : value.EnumValues.Max(e => e.Value) + 1;
        value.EnumValues.Add(new DataSourceEnumValue
        {
            Value = nextValue,
            DisplayName = $"状态{nextValue}",
        });
        _host.MarkDirty();
    }

    /// <summary>删除选中的枚举取值映射行。</summary>
    [RelayCommand(CanExecute = nameof(CanEditEnumValue))]
    private void RemoveEnumValue(DataSourceEnumValue item)
    {
        if (item == null || SelectedValue == null) return;
        if (SelectedEnumValue == item) SelectedEnumValue = null;
        SelectedValue.EnumValues.Remove(item);
        _host.MarkDirty();
    }
}