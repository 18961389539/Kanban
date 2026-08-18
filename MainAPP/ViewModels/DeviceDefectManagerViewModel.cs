using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Models;
using MainAPP.Services;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器「缺陷管理」Tab 的子 ViewModel。
/// 持有缺陷 CRUD 命令、缺陷 CSV 导入/导出与缺陷选中状态；
/// 宿主状态同步（SelectedDevice / IsLoading 封送）由 <see cref="DeviceChildManagerViewModel"/> 基类提供。
/// </summary>
public partial class DeviceDefectManagerViewModel : DeviceChildManagerViewModel
{
    private readonly DefectCsvIOService _defectCsvIO;

    [ObservableProperty]
    private Defect? _selectedDefect;

    /// <summary>
    /// 缺陷 CSV 导入/导出进行中标志：绑定到缺陷 Tab 工具条按钮 IsEnabled=false，
    /// 避免大文件 CSV 解析/写入期间用户重复点击。
    /// </summary>
    [ObservableProperty]
    private bool _isBusyDefectsCsv;

    public DeviceDefectManagerViewModel(
        IDialogService dialog,
        DefectCsvIOService defectCsvIO,
        IDeviceManagerHost host)
        : base(dialog, host)
    {
        _defectCsvIO = defectCsvIO;
    }

    protected override void OnHostSelectedDeviceChanged()
    {
        // 切换设备时清空缺陷选中，避免残留旧设备的引用
        SelectedDefect = null;
        AddDefectCommand.NotifyCanExecuteChanged();
        RemoveDefectCommand.NotifyCanExecuteChanged();
        ExportDefectsCsvCommand.NotifyCanExecuteChanged();
        ImportDefectsCsvCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostIsLoadingChanged()
    {
        AddDefectCommand.NotifyCanExecuteChanged();
        RemoveDefectCommand.NotifyCanExecuteChanged();
        ExportDefectsCsvCommand.NotifyCanExecuteChanged();
        ImportDefectsCsvCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void AddDefect()
    {
        if (SelectedDevice == null) return;
        // 缺陷名在所属设备内唯一
        var baseName = string.Format(Strings.F188, SelectedDevice.Defects.Count + 1);
        var newName = DeviceManagerViewModel.EnsureUniqueName(baseName, SelectedDevice.Defects.Select(d => d.Name));
        var defect = new Defect { DeviceId = SelectedDevice.Id, Name = newName };
        SelectedDevice.Defects.Add(defect);
        _host.MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RemoveDefect(Defect defect)
    {
        if (defect == null) return;
        var confirm = _dialog.Show(
            string.Format(Strings.F502, defect.Name),
            Strings.M118, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (SelectedDefect == defect) SelectedDefect = null;
        SelectedDevice?.Defects.Remove(defect);
        _host.MarkDirty();
    }

    /// <summary>
    /// 导出当前选中设备的全部缺陷到 CSV 文件。
    /// 异步执行避免大列表阻塞 UI；失败由 DefectCsvIOService 弹通知。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ExportDefectsCsvAsync()
    {
        if (SelectedDevice == null || IsBusyDefectsCsv) return;
        IsBusyDefectsCsv = true;
        ExportDefectsCsvCommand.NotifyCanExecuteChanged();
        try
        {
            var device = SelectedDevice;
            await Task.Run(() => _defectCsvIO.ExportDefects(device)).ConfigureAwait(true);
        }
        finally
        {
            IsBusyDefectsCsv = false;
            ExportDefectsCsvCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 从 CSV 文件导入缺陷到当前选中设备。
    /// 二次确认对话框让用户选择"追加"或"替换"。
    /// 部分行校验失败时仍导入有效行，并弹 Warning 通知含失败明细。
    /// 流程：UI 线程选文件 → 后台解析校验 → UI 线程二次确认 → UI 线程应用到设备。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ImportDefectsCsvAsync()
    {
        if (SelectedDevice == null || IsBusyDefectsCsv) return;
        IsBusyDefectsCsv = true;
        ExportDefectsCsvCommand.NotifyCanExecuteChanged();
        ImportDefectsCsvCommand.NotifyCanExecuteChanged();
        try
        {
            var device = SelectedDevice;

            // 阶段 1：UI 线程弹文件选择对话框
            var path = _defectCsvIO.PickImportPath();
            if (string.IsNullOrEmpty(path)) return; // 用户取消

            // 阶段 2：后台线程读取+解析+校验
            var result = await Task.Run(() => _defectCsvIO.ParseAndValidate(path)).ConfigureAwait(true);

            if (result.Imported.Count == 0)
            {
                _dialog.NotifyError(string.Format(Strings.F296, string.Join("\n  · ", result.Errors)));
                return;
            }

            // 阶段 3：UI 线程二次确认
            var existingCount = device.Defects.Count;
            var msg = result.HasErrors
                ? string.Format(Strings.F001, result.Imported.Count + result.Errors.Count, result.Errors.Count) +
                  string.Format(Strings.F295, result.Imported.Count, existingCount) +
                  Strings.F228
                : string.Format(Strings.F294, result.Imported.Count, existingCount) +
                  Strings.F228;

            var choice = _dialog.Show(msg, Strings.M120, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;

            var replace = choice == MessageBoxResult.Yes;

            // 阶段 4：UI 线程应用到设备
            _defectCsvIO.ApplyImportedDefects(device, result.Imported, replace);

            _host.MarkDirty();

            if (result.HasErrors)
            {
                _dialog.NotifyWarning(
                    string.Format(Strings.F297, result.Imported.Count, result.Errors.Count) +
                    string.Format(Strings.F086, string.Join("\n  · ", result.Errors.Take(5))) +
                    (result.Errors.Count > 5 ? string.Format(Strings.F020, result.Errors.Count) : ""));
            }
            else
            {
                _dialog.NotifySuccess(string.Format(Strings.F298, result.Imported.Count));
            }
        }
        finally
        {
            IsBusyDefectsCsv = false;
            ExportDefectsCsvCommand.NotifyCanExecuteChanged();
            ImportDefectsCsvCommand.NotifyCanExecuteChanged();
        }
    }
}
