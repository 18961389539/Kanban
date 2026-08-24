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
    private readonly DataSourceCsvIOService _dataSourceCsvIO;

    [ObservableProperty]
    private bool _isBusyDataSourcesCsv;

    [ObservableProperty]
    private DataSource? _selectedSource;

    /// <summary>选中源的值项表格选中行。</summary>
    [ObservableProperty]
    private DataSourceValue? _selectedValue;

    /// <summary>值项枚举取值表选中行。</summary>
    [ObservableProperty]
    private DataSourceEnumValue? _selectedEnumValue;

    public IReadOnlyList<DataSourceValueType> DataSourceValueTypes { get; } = Enum.GetValues<DataSourceValueType>();

    public DeviceDataSourceManagerViewModel(
        IDialogService dialog,
        IDeviceManagerHost host,
        DataSourceCsvIOService dataSourceCsvIO)
        : base(dialog, host)
    {
        _dataSourceCsvIO = dataSourceCsvIO;
    }

    protected override bool IsCsvBusy => IsBusyDataSourcesCsv;

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

    partial void OnIsBusyDataSourcesCsvChanged(bool value)
    {
        AddSourceCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        AddValueCommand.NotifyCanExecuteChanged();
        RemoveValueCommand.NotifyCanExecuteChanged();
        AddEnumValueCommand.NotifyCanExecuteChanged();
        RemoveEnumValueCommand.NotifyCanExecuteChanged();
        ExportDataSourcesCsvCommand.NotifyCanExecuteChanged();
        ImportDataSourcesCsvCommand.NotifyCanExecuteChanged();
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
        ExportDataSourcesCsvCommand.NotifyCanExecuteChanged();
        ImportDataSourcesCsvCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostIsLoadingChanged()
    {
        AddSourceCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        AddValueCommand.NotifyCanExecuteChanged();
        RemoveValueCommand.NotifyCanExecuteChanged();
        AddEnumValueCommand.NotifyCanExecuteChanged();
        RemoveEnumValueCommand.NotifyCanExecuteChanged();
        ExportDataSourcesCsvCommand.NotifyCanExecuteChanged();
        ImportDataSourcesCsvCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostPermissionChanged()
    {
        AddSourceCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        AddValueCommand.NotifyCanExecuteChanged();
        RemoveValueCommand.NotifyCanExecuteChanged();
        AddEnumValueCommand.NotifyCanExecuteChanged();
        RemoveEnumValueCommand.NotifyCanExecuteChanged();
        ExportDataSourcesCsvCommand.NotifyCanExecuteChanged();
        ImportDataSourcesCsvCommand.NotifyCanExecuteChanged();
    }

    /// <summary>新增数据源：默认带一个值项，由 Save 统一校验持久化。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void AddSource()
    {
        if (!CanEditSelected()) return;
        if (SelectedDevice == null) return;
        var baseName = string.Format(Strings.F198, SelectedDevice.Sources.Count + 1);
        var newName = DeviceManagerViewModel.EnsureUniqueName(baseName, SelectedDevice.Sources.Select(s => s.Name));
        var source = new DataSource { DeviceId = SelectedDevice.Id, Name = newName };
        source.Values.Add(new DataSourceValue { Name = string.Format(Strings.Dsm_DefaultValueName, 1) });
        SelectedDevice.Sources.Add(source);
        // 不立即 SaveAll：统一由 Save 按钮校验（含阈值/地址规则）后持久化
        _host.MarkDirty();
    }

    /// <summary>删除选中数据源（确认后）。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveSource(DataSource source)
    {
        if (!CanEditSelected()) return;
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
    private bool CanEditValue() => SelectedSource != null && !_host.IsLoading && _host.CanManageDevices && !IsCsvBusy;

    /// <summary>新增值项（当前选中源）。</summary>
    [RelayCommand(CanExecute = nameof(CanEditValue))]
    private void AddValue()
    {
        if (!CanEditValue()) return;
        var source = SelectedSource;
        if (source == null) return;
        var next = source.Values.Count + 1;
        source.Values.Add(new DataSourceValue { Name = string.Format(Strings.Dsm_DefaultValueName, next) });
        _host.MarkDirty();
    }

    /// <summary>删除选中的值项。</summary>
    [RelayCommand(CanExecute = nameof(CanEditValue))]
    private void RemoveValue(DataSourceValue value)
    {
        if (!CanEditValue()) return;
        if (value == null || SelectedSource == null) return;
        if (SelectedValue == value) SelectedValue = null;
        SelectedSource.Values.Remove(value);
        _host.MarkDirty();
    }

    /// <summary>枚举值行可编辑性：选中值项且不在加载中。</summary>
    private bool CanEditEnumValue() => SelectedValue != null && !_host.IsLoading && _host.CanManageDevices && !IsCsvBusy;

    /// <summary>新增枚举取值映射行（值项级：非数值型的 值→显示名）。</summary>
    [RelayCommand(CanExecute = nameof(CanEditEnumValue))]
    private void AddEnumValue()
    {
        if (!CanEditEnumValue()) return;
        var value = SelectedValue;
        if (value == null) return;
        var nextValue = value.EnumValues.Count == 0
            ? 0
            : value.EnumValues.Max(e => e.Value) + 1;
        value.EnumValues.Add(new DataSourceEnumValue
        {
            Value = nextValue,
            DisplayName = string.Format(Strings.Dsm_DefaultEnumStateName, nextValue),
        });
        _host.MarkDirty();
    }

    /// <summary>删除选中的枚举取值映射行。</summary>
    [RelayCommand(CanExecute = nameof(CanEditEnumValue))]
    private void RemoveEnumValue(DataSourceEnumValue item)
    {
        if (!CanEditEnumValue()) return;
        if (item == null || SelectedValue == null) return;
        if (SelectedEnumValue == item) SelectedEnumValue = null;
        SelectedValue.EnumValues.Remove(item);
        _host.MarkDirty();
    }

    /// <summary>导出当前选中设备的全部数据源配置到 CSV。</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ExportDataSourcesCsvAsync()
    {
        if (!CanEditSelected() || SelectedDevice is not { } device || IsBusyDataSourcesCsv) return;
        IsBusyDataSourcesCsv = true;
        try
        {
            var path = _dataSourceCsvIO.PickExportPath(device);
            if (string.IsNullOrEmpty(path)) return;

            var summary = await Task.Run(() => _dataSourceCsvIO.ExportDataSourcesToPath(device, path)).ConfigureAwait(true);
            _dialog.NotifySuccess(string.Format(
                Strings.F331,
                summary.SourceCount,
                summary.ValueCount,
                System.IO.Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F090, ex.Message));
        }
        finally
        {
            IsBusyDataSourcesCsv = false;
        }
    }

    /// <summary>
    /// 从 CSV 导入当前设备的数据源配置；是=替换、否=追加、取消=放弃。
    /// 解析校验完成后才修改 ObservableCollection，导入结果仍需点击保存持久化。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ImportDataSourcesCsvAsync()
    {
        if (!CanEditSelected() || SelectedDevice is not { } device || IsBusyDataSourcesCsv) return;
        IsBusyDataSourcesCsv = true;
        try
        {
            var path = _dataSourceCsvIO.PickImportPath();
            if (string.IsNullOrEmpty(path)) return;

            var result = await Task.Run(() => _dataSourceCsvIO.ParseAndValidate(path)).ConfigureAwait(true);
            if (result.Imported.Count == 0)
            {
                var details = result.Errors.Count == 0
                    ? Strings.F333
                    : string.Join("\n  · ", result.Errors.Take(5));
                _dialog.NotifyError(string.Format(Strings.F348, details));
                return;
            }

            var choice = _dialog.Show(
                string.Format(Strings.F344, result.Imported.Count, result.ImportedValueCount, device.Sources.Count)
                    + "\n"
                    + Strings.F228,
                Strings.M120,
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;

            _dataSourceCsvIO.ApplyImportedDataSources(device, result.Imported, replace: choice == MessageBoxResult.Yes);
            _host.MarkDirty();

            if (result.HasErrors)
            {
                _dialog.NotifyWarning(string.Format(
                    Strings.F347,
                    result.Imported.Count,
                    result.ImportedValueCount,
                    result.Errors.Count,
                    string.Join("\n  · ", result.Errors.Take(5))));
            }
            else
            {
                _dialog.NotifySuccess(string.Format(
                    Strings.F345,
                    result.Imported.Count,
                    result.ImportedValueCount));
            }
        }
        finally
        {
            IsBusyDataSourcesCsv = false;
        }
    }
}