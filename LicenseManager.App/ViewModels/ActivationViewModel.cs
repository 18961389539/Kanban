using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LicenseManager.Crypto;
using LicenseManager.Models;
using LicenseManager.Services;

namespace LicenseManager.ViewModels;

/// <summary>
/// 激活对话框 ViewModel：处理激活码输入、格式化、验证。
/// </summary>
/// <remarks>
/// Q-1 重构：基于 CommunityToolkit.Mvvm 的 ObservableObject + RelayCommand 源生成器，
/// 与 MainAPP 的 SettingsViewModel/MainWindowViewModel 等保持一致的 MVVM 风格。
/// ProductKey 属性由于需要自动格式化输入（每 5 字符插入分隔符 + 转大写），
/// 手动实现 setter 以保持原有的实时格式化交互。
/// </remarks>
public partial class ActivationViewModel : ObservableObject
{
    private readonly LicenseGate _gate;
    private string _productKey = string.Empty;

    public ActivationViewModel(LicenseGate gate)
    {
        _gate = gate;
    }

    /// <summary>当前机器码（8 字符）</summary>
    public string MachineCode => _gate.MachineCode;

    /// <summary>当前授权状态</summary>
    public LicenseStatus Status => _gate.CurrentStatus;

    /// <summary>试用剩余天数（试用期内显示）</summary>
    public int? RemainingTrialDays => _gate.RemainingTrialDays;

    /// <summary>用户输入的激活码（自动格式化，兼容旧 HMAC 和新 ECDSA 格式）</summary>
    public string ProductKey
    {
        get => _productKey;
        set
        {
            // 自动格式化：移除分隔符后重新插入，并转大写
            var formatted = ProductKeyCodec.NormalizeForDisplay(value);
            if (!SetProperty(ref _productKey, formatted))
            {
                // 字段值未变化（例如已经是 "ABCDE"，用户再次输入 "abcde" 时格式化后仍为 "ABCDE"）。
                // 但若 UI 显示的原始输入与格式化结果不同，仍需触发 PropertyChanged 让 TextBox 刷新显示格式化后的文本。
                if (!string.Equals(formatted, value, StringComparison.Ordinal))
                {
                    RaisePropertyChanged();
                }
                // CanActivate 结果依赖 _productKey 字段值，字段未变则无需通知
                return;
            }
            // 字段值变化 → 通知 ActivateCommand 重新评估 CanExecute
            ActivateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>错误消息（激活失败时显示）</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>状态提示消息</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>是否正在激活中（防止重复提交）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    private bool _isActivating;

    /// <summary>激活成功事件（对话框据此关闭）</summary>
    public event Action? ActivationSucceeded;

    /// <summary>激活命令：可执行条件为非激活中且激活码非空。</summary>
    [RelayCommand(CanExecute = nameof(CanActivate))]
    private void Activate()
    {
        IsActivating = true;
        ErrorMessage = string.Empty;

        try
        {
            if (_gate.TryActivate(ProductKey, out var error))
            {
                StatusMessage = "激活成功！";
                ActivationSucceeded?.Invoke();
            }
            else
            {
                ErrorMessage = error;
            }
        }
        finally
        {
            IsActivating = false;
        }
    }

    private bool CanActivate() => !IsActivating && !string.IsNullOrWhiteSpace(ProductKey);

    /// <summary>复制机器码到剪贴板。</summary>
    [RelayCommand]
    private void CopyMachineCode()
    {
        try
        {
            System.Windows.Clipboard.SetText(MachineCode);
            StatusMessage = "机器码已复制到剪贴板";
        }
        catch
        {
            ErrorMessage = "复制失败，请手动记录机器码";
        }
    }

    /// <summary>手动触发 ProductKey 属性变更通知（用于 SetProperty 返回 false 但仍需通知 UI 的场景）。</summary>
    private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        => OnPropertyChanged(propertyName);
}
