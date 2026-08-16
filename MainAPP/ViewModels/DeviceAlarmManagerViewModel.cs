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
/// 设备管理器「报警管理」Tab 的子 ViewModel。
/// 持有报警 CRUD 命令、报警 CSV 导入/导出与报警选中状态；
/// 宿主状态同步（SelectedDevice / IsLoading 封送）由 <see cref="DeviceChildManagerViewModel"/> 基类提供。
/// </summary>
public partial class DeviceAlarmManagerViewModel : DeviceChildManagerViewModel
{
    private readonly AlarmCsvIOService _alarmCsvIO;
    private readonly IPlcDataAcquisitionService _dataAcquisitionService;

    [ObservableProperty]
    private Alarm? _selectedAlarm;

    /// <summary>
    /// 报警 CSV 导入/导出进行中标志：绑定到报警 Tab 工具条按钮 IsEnabled=false + 显示 LoadingCircle，
    /// 避免大文件（数百条报警）CSV 解析/写入期间用户重复点击。
    /// </summary>
    [ObservableProperty]
    private bool _isBusyAlarmsCsv;

    public DeviceAlarmManagerViewModel(
        IDialogService dialog,
        AlarmCsvIOService alarmCsvIO,
        IPlcDataAcquisitionService dataAcquisitionService,
        IDeviceManagerHost host)
        : base(dialog, host)
    {
        _alarmCsvIO = alarmCsvIO;
        _dataAcquisitionService = dataAcquisitionService;
    }

    protected override void OnHostSelectedDeviceChanged()
    {
        // 切换设备时清空报警选中，避免残留旧设备的引用
        SelectedAlarm = null;
        AddAlarmCommand.NotifyCanExecuteChanged();
        RemoveAlarmCommand.NotifyCanExecuteChanged();
        ExportAlarmsCsvCommand.NotifyCanExecuteChanged();
        ImportAlarmsCsvCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostIsLoadingChanged()
    {
        AddAlarmCommand.NotifyCanExecuteChanged();
        RemoveAlarmCommand.NotifyCanExecuteChanged();
        ExportAlarmsCsvCommand.NotifyCanExecuteChanged();
        ImportAlarmsCsvCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void AddAlarm()
    {
        if (SelectedDevice == null) return;
        // 报警名在所属设备内唯一
        var baseName = string.Format(Strings.F127, SelectedDevice.Alarms.Count + 1);
        var newName = DeviceManagerViewModel.EnsureUniqueName(baseName, SelectedDevice.Alarms.Select(a => a.Name));
        // 设置 DeviceId 使 OnPlcAddressChanged 能生成确定性 Id（DeviceId_PlcAddress），
        // 删除后重新添加同地址报警可续接历史数据
        var alarm = new Alarm { Name = newName, DeviceId = SelectedDevice.Id };
        SelectedDevice.Alarms.Add(alarm);
        _host.MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveAlarm(Alarm alarm)
    {
        if (alarm == null) return;
        var confirm = _dialog.Show(
            string.Format(Strings.F501, alarm.Name),
            Strings.M118, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (SelectedAlarm == alarm) SelectedAlarm = null;
        // 清理采集服务中该报警的内存状态（_prevAlarmStates），避免内存泄漏
        _dataAcquisitionService.RemoveAlarmState(alarm.Id);
        SelectedDevice?.Alarms.Remove(alarm);
        _host.MarkDirty();
    }

    /// <summary>
    /// 导出当前选中设备的全部报警到 CSV 文件（基于 CsvHelper）。
    /// 仅导出用户可编辑字段（名称/PLC地址/级别/描述），Id/DeviceId 不暴露。
    /// 异步执行避免大列表阻塞 UI；失败由 AlarmCsvIOService 弹通知。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ExportAlarmsCsvAsync()
    {
        if (SelectedDevice == null || IsBusyAlarmsCsv) return;
        IsBusyAlarmsCsv = true;
        ExportAlarmsCsvCommand.NotifyCanExecuteChanged();
        try
        {
            var device = SelectedDevice;
            await Task.Run(() => _alarmCsvIO.ExportAlarms(device)).ConfigureAwait(true);
        }
        finally
        {
            IsBusyAlarmsCsv = false;
            ExportAlarmsCsvCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 从 CSV 文件导入报警到当前选中设备。
    /// 二次确认对话框让用户选择"追加"（保留现有、同 PlcAddress 覆盖）或"替换"（清空后导入）。
    /// 部分行校验失败时仍导入有效行，并弹 Warning 通知含失败明细。
    /// 流程：UI 线程选文件 → 后台解析校验 → UI 线程二次确认 → UI 线程应用到设备。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ImportAlarmsCsvAsync()
    {
        if (SelectedDevice == null || IsBusyAlarmsCsv) return;
        IsBusyAlarmsCsv = true;
        ExportAlarmsCsvCommand.NotifyCanExecuteChanged();
        ImportAlarmsCsvCommand.NotifyCanExecuteChanged();
        try
        {
            var device = SelectedDevice;

            // 阶段 1：UI 线程弹文件选择对话框
            var path = _alarmCsvIO.PickImportPath();
            if (string.IsNullOrEmpty(path)) return; // 用户取消

            // 阶段 2：后台线程读取+解析+校验（纯 IO/CPU，不依赖 UI）
            var result = await Task.Run(() => _alarmCsvIO.ParseAndValidate(path)).ConfigureAwait(true);

            if (result.Imported.Count == 0)
            {
                _dialog.NotifyError(string.Format(Strings.F143, string.Join("\n  · ", result.Errors)));
                return;
            }

            // 阶段 3：UI 线程二次确认（让用户选择追加或替换）
            var existingCount = device.Alarms.Count;
            var msg = result.HasErrors
                ? string.Format(Strings.F001, result.Imported.Count + result.Errors.Count, result.Errors.Count) +
                  string.Format(Strings.F141, result.Imported.Count, existingCount) +
                  Strings.F228
                : string.Format(Strings.F091, result.Imported.Count, existingCount) +
                  Strings.F228;

            var choice = _dialog.Show(msg, Strings.M120, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;

            var replace = choice == MessageBoxResult.Yes;

            // 阶段 4：UI 线程应用到设备（ObservableCollection 通知需 UI 线程）
            _alarmCsvIO.ApplyImportedAlarms(device, result.Imported, replace);

            _host.MarkDirty();

            if (result.HasErrors)
            {
                _dialog.NotifyWarning(
                    string.Format(Strings.F102, result.Imported.Count, result.Errors.Count) +
                    string.Format(Strings.F086, string.Join("\n  · ", result.Errors.Take(5))) +
                    (result.Errors.Count > 5 ? string.Format(Strings.F020, result.Errors.Count) : ""));
            }
            else
            {
                _dialog.NotifySuccess(string.Format(Strings.F103, result.Imported.Count));
            }
        }
        finally
        {
            IsBusyAlarmsCsv = false;
            ExportAlarmsCsvCommand.NotifyCanExecuteChanged();
            ImportAlarmsCsvCommand.NotifyCanExecuteChanged();
        }
    }
}
