using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器「计数报警」Tab 的子 ViewModel。
/// 持有计数报警 CRUD 命令、清空当前值（PLC 写）命令、CSV 导入/导出与选中状态；
/// 宿主状态同步（SelectedDevice / IsLoading / IsPlcConnected 封送）由 <see cref="DeviceChildManagerViewModel"/> 基类提供。
/// </summary>
public partial class DeviceCounterAlarmManagerViewModel : DeviceChildManagerViewModel
{
    private readonly DevicePlcCommandHandler _plcCommands;
    private readonly CounterAlarmCsvIOService _counterAlarmCsvIO;

    [ObservableProperty]
    private CounterAlarm? _selectedCounterAlarm;

    /// <summary>
    /// 计数报警 CSV 导入/导出进行中标志：绑定到计数报警 Tab 工具条按钮 IsEnabled=false，
    /// 避免大文件 CSV 解析/写入期间用户重复点击。
    /// </summary>
    [ObservableProperty]
    private bool _isBusyCounterAlarmsCsv;

    public DeviceCounterAlarmManagerViewModel(
        IDialogService dialog,
        DevicePlcCommandHandler plcCommands,
        CounterAlarmCsvIOService counterAlarmCsvIO,
        IDeviceManagerHost host)
        : base(dialog, host)
    {
        _plcCommands = plcCommands;
        _counterAlarmCsvIO = counterAlarmCsvIO;
    }

    protected override void OnHostSelectedDeviceChanged()
    {
        // 切换设备时清空计数报警选中，避免残留旧设备的引用
        SelectedCounterAlarm = null;
        AddCounterAlarmCommand.NotifyCanExecuteChanged();
        RemoveCounterAlarmCommand.NotifyCanExecuteChanged();
        ResetCounterAlarmValueCommand.NotifyCanExecuteChanged();
        ExportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
        ImportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostIsLoadingChanged()
    {
        AddCounterAlarmCommand.NotifyCanExecuteChanged();
        RemoveCounterAlarmCommand.NotifyCanExecuteChanged();
        ResetCounterAlarmValueCommand.NotifyCanExecuteChanged();
        ExportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
        ImportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostOtherPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(IDeviceManagerHost.IsPlcConnected))
            ResetCounterAlarmValueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>PLC 写入/读取类命令的可用性：选中设备且不在加载中且 PLC 在线。</summary>
    private bool CanExecutePlcWrite() => SelectedDevice != null && !_host.IsLoading && _host.IsPlcConnected;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void AddCounterAlarm()
    {
        if (SelectedDevice == null) return;
        var baseName = string.Format(Strings.F198, SelectedDevice.CounterAlarms.Count + 1);
        var newName = DeviceManagerViewModel.EnsureUniqueName(baseName, SelectedDevice.CounterAlarms.Select(c => c.Name));
        var alarm = new CounterAlarm { Name = newName };
        SelectedDevice.CounterAlarms.Add(alarm);
        // 不立即 SaveAll：统一由 Save 按钮校验（含报警地址唯一性）后持久化，避免绕过校验写入非法配置
        _host.MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveCounterAlarm(CounterAlarm alarm)
    {
        if (alarm == null) return;
        var confirm = _dialog.Show(
            string.Format(Strings.F503, alarm.Name),
            Strings.M118, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (SelectedCounterAlarm == alarm) SelectedCounterAlarm = null;
        SelectedDevice?.CounterAlarms.Remove(alarm);
        // 不立即 SaveAll：统一由 Save 按钮持久化，与 AddAlarm/RemoveAlarm/Defect 行为一致
        _host.MarkDirty();
    }

    /// <summary>
    /// 清空计数报警当前值：向 PLC 写 0 并同步复位 CurrentValue。
    /// IsLoading 由父级共享，PLC 写入期间驱动父级加载覆盖层。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecutePlcWrite))]
    private async Task ResetCounterAlarmValueAsync(CounterAlarm alarm)
    {
        if (!_host.IsPlcConnected) return;
        var confirm = _dialog.Show(
            string.Format(Strings.F177, alarm.Name),
            Strings.M175, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        _host.IsLoading = true;
        _host.ReportPlcOperationStarted();
        try
        {
            var result = await _plcCommands.ResetCounterAlarmValueAsync(alarm);
            _host.ReportPlcOperation(result);
            NotifyPlcResult(result);
        }
        finally
        {
            _host.IsLoading = false;
        }
    }

    /// <summary>
    /// 将 PLC 命令结果按 Status 映射到对应级别的通知（Success→Growl.Success / Info→Info /
    /// Warning→Warning / Error→Error）。Cancelled 由调用方过滤，不应传入此方法。
    /// </summary>
    private void NotifyPlcResult(PlcOpResult result)
    {
        switch (result.Status)
        {
            case PlcOpStatus.Success:
                _dialog.NotifySuccess(result.Message);
                break;
            case PlcOpStatus.Info:
                _dialog.NotifyInfo(result.Message);
                break;
            case PlcOpStatus.Warning:
                _dialog.NotifyWarning(result.Message);
                break;
            case PlcOpStatus.Error:
                _dialog.NotifyError(result.Message);
                break;
            case PlcOpStatus.Cancelled:
                // 调用方应已过滤；不弹通知
                break;
        }
    }

    /// <summary>
    /// 导出当前选中设备的全部计数报警到 CSV 文件。
    /// 异步执行避免大列表阻塞 UI；失败由 CounterAlarmCsvIOService 弹通知。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ExportCounterAlarmsCsvAsync()
    {
        if (SelectedDevice == null || IsBusyCounterAlarmsCsv) return;
        IsBusyCounterAlarmsCsv = true;
        ExportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
        try
        {
            var device = SelectedDevice;
            await Task.Run(() => _counterAlarmCsvIO.ExportCounterAlarms(device)).ConfigureAwait(true);
        }
        finally
        {
            IsBusyCounterAlarmsCsv = false;
            ExportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 从 CSV 文件导入计数报警到当前选中设备。
    /// 二次确认对话框让用户选择"追加"或"替换"。
    /// 部分行校验失败时仍导入有效行，并弹 Warning 通知含失败明细。
    /// 流程：UI 线程选文件 → 后台解析校验 → UI 线程二次确认 → UI 线程应用到设备。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ImportCounterAlarmsCsvAsync()
    {
        if (SelectedDevice == null || IsBusyCounterAlarmsCsv) return;
        IsBusyCounterAlarmsCsv = true;
        ExportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
        ImportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
        try
        {
            var device = SelectedDevice;

            // 阶段 1：UI 线程弹文件选择对话框
            var path = _counterAlarmCsvIO.PickImportPath();
            if (string.IsNullOrEmpty(path)) return; // 用户取消

            // 阶段 2：后台线程读取+解析+校验
            var result = await Task.Run(() => _counterAlarmCsvIO.ParseAndValidate(path)).ConfigureAwait(true);

            if (result.Imported.Count == 0)
            {
                _dialog.NotifyError(string.Format(Strings.F307, string.Join("\n  · ", result.Errors)));
                return;
            }

            // 阶段 3：UI 线程二次确认
            var existingCount = device.CounterAlarms.Count;
            var msg = result.HasErrors
                ? string.Format(Strings.F001, result.Imported.Count + result.Errors.Count, result.Errors.Count) +
                  string.Format(Strings.F306, result.Imported.Count, existingCount) +
                  Strings.F228
                : string.Format(Strings.F305, result.Imported.Count, existingCount) +
                  Strings.F228;

            var choice = _dialog.Show(msg, Strings.M120, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;

            var replace = choice == MessageBoxResult.Yes;

            // 阶段 4：UI 线程应用到设备
            _counterAlarmCsvIO.ApplyImportedCounterAlarms(device, result.Imported, replace);

            _host.MarkDirty();

            if (result.HasErrors)
            {
                _dialog.NotifyWarning(
                    string.Format(Strings.F308, result.Imported.Count, result.Errors.Count) +
                    string.Format(Strings.F086, string.Join("\n  · ", result.Errors.Take(5))) +
                    (result.Errors.Count > 5 ? string.Format(Strings.F020, result.Errors.Count) : ""));
            }
            else
            {
                _dialog.NotifySuccess(string.Format(Strings.F309, result.Imported.Count));
            }
        }
        finally
        {
            IsBusyCounterAlarmsCsv = false;
            ExportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
            ImportCounterAlarmsCsvCommand.NotifyCanExecuteChanged();
        }
    }
}
