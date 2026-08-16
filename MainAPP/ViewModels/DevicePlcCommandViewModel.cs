using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Resources;
using Serilog;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器「设备参数」Tab 的 PLC 命令子 ViewModel。
/// 持有写配方 / OEE 清零 / 读 PLC 地址三个命令及 PLC 操作状态栏；
/// 宿主状态同步（SelectedDevice / IsLoading / IsPlcConnected）由 <see cref="DeviceChildManagerViewModel"/> 基类提供。
/// </summary>
public partial class DevicePlcCommandViewModel : DeviceChildManagerViewModel
{
    private readonly DevicePlcCommandHandler _plcCommands;

    [ObservableProperty]
    private string _recipeStatus = string.Empty;

    [ObservableProperty]
    private string _plcOperationStatus = string.Empty;

    [ObservableProperty]
    private string _plcOperationStatusType = "None";

    public DevicePlcCommandViewModel(
        IDialogService dialog,
        IDeviceManagerHost host,
        DevicePlcCommandHandler plcCommands)
        : base(dialog, host)
    {
        _plcCommands = plcCommands;
    }

    protected override void OnHostSelectedDeviceChanged()
    {
        WriteRecipeCommand.NotifyCanExecuteChanged();
        ResetProductionCommand.NotifyCanExecuteChanged();
        ReadPlcValueCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostIsLoadingChanged()
    {
        WriteRecipeCommand.NotifyCanExecuteChanged();
        ResetProductionCommand.NotifyCanExecuteChanged();
        ReadPlcValueCommand.NotifyCanExecuteChanged();
    }

    protected override void OnHostOtherPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(IDeviceManagerHost.IsPlcConnected))
        {
            WriteRecipeCommand.NotifyCanExecuteChanged();
            ResetProductionCommand.NotifyCanExecuteChanged();
            ReadPlcValueCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanExecutePlcWrite() => SelectedDevice != null
        && !_host.IsLoading
        && _host.IsPlcConnected;

    [RelayCommand(CanExecute = nameof(CanExecutePlcWrite))]
    private async Task WriteRecipeAsync()
    {
        if (SelectedDevice == null) return;

        _host.IsLoading = true;
        ReportPlcOperationStarted();
        try
        {
            var result = await _plcCommands.WriteRecipeAsync(SelectedDevice);
            RecipeStatus = result.Status switch
            {
                PlcOpStatus.Success => string.Format(Strings.F072, System.DateTime.Now),
                PlcOpStatus.Info => result.Message,
                PlcOpStatus.Warning => string.Format(Strings.F197, result.Message),
                PlcOpStatus.Error => string.Format(Strings.F237, result.Message),
                _ => result.Message,
            };
            SetPlcOperationStatus(result);

            if (result.Status == PlcOpStatus.Warning || result.Status == PlcOpStatus.Error)
                NotifyPlcResult(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "写配方命令异常");
            PlcOperationStatus = string.Format(Strings.F237, ex.Message);
            PlcOperationStatusType = "Error";
            _dialog.NotifyError(string.Format(Strings.F237, ex.Message));
        }
        finally
        {
            _host.IsLoading = false;
        }
    }

    /// <summary>
    /// 手动触发选中设备的 OEE 清零：触发 PLC 清零 + 同步软件侧 OEE 累计清零。危险写操作，需二次确认。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecutePlcWrite))]
    private async Task ResetProductionAsync()
    {
        if (SelectedDevice == null) return;

        _host.IsLoading = true;
        ReportPlcOperationStarted();
        try
        {
            var result = await _plcCommands.ResetProductionAsync(
                SelectedDevice,
                device => _dialog.Show(
                    string.Format(Strings.F176, device.Name),
                    Strings.M171, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

            if (result.Status != PlcOpStatus.Cancelled)
            {
                SetPlcOperationStatus(result);
                NotifyPlcResult(result);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OEE 清零命令异常");
            PlcOperationStatus = string.Format(Strings.F237, ex.Message);
            PlcOperationStatusType = "Error";
            _dialog.NotifyError(string.Format(Strings.F237, ex.Message));
        }
        finally
        {
            _host.IsLoading = false;
        }
    }

    /// <summary>
    /// 从 PLC 读取指定地址的当前值（D 字地址），用于调试/验证地址配置是否正确。
    /// CommandParameter 为 PLC 地址字符串。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecutePlcWrite))]
    private async Task ReadPlcValueAsync(string? address)
    {
        _host.IsLoading = true;
        ReportPlcOperationStarted();
        try
        {
            var result = await _plcCommands.ReadPlcValueAsync(address);
            SetPlcOperationStatus(result);
            NotifyPlcResult(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读 PLC 值命令异常");
            PlcOperationStatus = string.Format(Strings.F237, ex.Message);
            PlcOperationStatusType = "Error";
            _dialog.NotifyError(string.Format(Strings.F237, ex.Message));
        }
        finally
        {
            _host.IsLoading = false;
        }
    }

    /// <summary>标记 PLC 操作开始：状态栏显示"正在执行 PLC 操作..."（仅真实 PLC 操作调用，保存等非 PLC 流程不触发）。</summary>
    public void ReportPlcOperationStarted()
    {
        PlcOperationStatus = Strings.M170;
        PlcOperationStatusType = "Progress";
    }

    /// <summary>供子 Tab（如计数报警清零）把 PLC 结果汇总到本状态栏。</summary>
    public void ReportPlcOperation(PlcOpResult result) => SetPlcOperationStatus(result);

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
                break;
        }
    }

    private void SetPlcOperationStatus(PlcOpResult result)
    {
        PlcOperationStatus = result.Status switch
        {
            PlcOpStatus.Success => string.Format(Strings.F125, result.Message),
            PlcOpStatus.Info => result.Message,
            PlcOpStatus.Warning => string.Format(Strings.F197, result.Message),
            PlcOpStatus.Error => string.Format(Strings.F087, result.Message),
            PlcOpStatus.Cancelled => Strings.M172,
            _ => result.Message,
        };
        PlcOperationStatusType = result.Status switch
        {
            PlcOpStatus.Success => "Success",
            PlcOpStatus.Warning => "Warning",
            PlcOpStatus.Error => "Error",
            PlcOpStatus.Cancelled => "None",
            _ => "Info",
        };
    }
}
