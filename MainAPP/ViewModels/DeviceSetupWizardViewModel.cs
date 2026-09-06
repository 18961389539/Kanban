using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
using MainAPP.Resources;
using MainAPP.Services;

namespace MainAPP.ViewModels;

/// <summary>
/// 新设备配置向导的纯交互状态（两步：基本信息 → PLC 地址）。
/// 名称、目标产能与四个 PLC 地址均为必填。
/// </summary>
public partial class DeviceSetupWizardViewModel : ObservableObject
{
    private const int LastStep = 1;
    private readonly IReadOnlyList<Device> _existingDevices;
    private readonly IPlcAddressCodec _addressCodec;

    public DeviceSetupWizardViewModel(
        IReadOnlyList<Device> existingDevices,
        IPlcAddressCodec addressCodec)
    {
        _existingDevices = existingDevices;
        _addressCodec = addressCodec;
        Name = DeviceManagerViewModel.EnsureUniqueName(
            string.Format(Strings.F135, existingDevices.Count + 1),
            existingDevices.Select(device => device.Name));
        TargetCycle = 600;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    [NotifyPropertyChangedFor(nameof(StepProgress))]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    private int _currentStep;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _targetCycle;

    [ObservableProperty]
    private string _okCountAddress = string.Empty;

    [ObservableProperty]
    private string _ngCountAddress = string.Empty;

    [ObservableProperty]
    private string _statusCountAddress = string.Empty;

    [ObservableProperty]
    private string _productionResetAddress = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string _validationMessage = string.Empty;

    public bool IsFirstStep => CurrentStep == 0;
    public bool IsLastStep => CurrentStep == LastStep;

    public string StepTitle => CurrentStep switch
    {
        0 => Strings.Ux_DeviceWizardStepBasic,
        _ => Strings.Ux_DeviceWizardStepAddresses,
    };

    public string StepProgress => $"{CurrentStep + 1} / {LastStep + 1} · {StepTitle}";

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public Device? Result { get; private set; }

    public event Action? Completed;
    public event Action? Cancelled;

    [RelayCommand]
    private void Next()
    {
        if (!ValidateCurrentStep()) return;
        CurrentStep = Math.Min(CurrentStep + 1, LastStep);
        ValidationMessage = string.Empty;
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep == 0) return;
        CurrentStep--;
        ValidationMessage = string.Empty;
    }

    [RelayCommand]
    private void Finish()
    {
        if (!ValidateCurrentStep() || !ValidateCandidate()) return;
        Result = BuildDevice();
        Completed?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    private bool ValidateCurrentStep()
    {
        if (CurrentStep == 0)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                ValidationMessage = Strings.Ux_DeviceWizardNameRequired;
                return false;
            }

            if (_existingDevices.Any(device =>
                    string.Equals(device.Name?.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                ValidationMessage = Strings.Ux_DeviceWizardNameDuplicate;
                return false;
            }

            if (TargetCycle <= 0)
            {
                ValidationMessage = Strings.Ux_DeviceWizardTargetCycleInvalid;
                return false;
            }
        }

        if (CurrentStep == LastStep)
            return ValidateCandidate();

        return true;
    }

    private bool ValidateCandidate()
    {
        if (TargetCycle <= 0)
        {
            ValidationMessage = Strings.Ux_DeviceWizardTargetCycleInvalid;
            return false;
        }

        if (string.IsNullOrWhiteSpace(OkCountAddress)
            || string.IsNullOrWhiteSpace(NgCountAddress)
            || string.IsNullOrWhiteSpace(StatusCountAddress)
            || string.IsNullOrWhiteSpace(ProductionResetAddress))
        {
            ValidationMessage = Strings.Ux_DeviceWizardAddressRequired;
            return false;
        }

        var candidate = BuildDevice();
        var errors = DeviceConfigValidator
            .CollectValidationErrors(_existingDevices.Append(candidate), _addressCodec)
            .Where(error => ReferenceEquals(error.Device, candidate))
            .Select(error => error.Message)
            .Distinct()
            .ToArray();
        if (errors.Length > 0)
        {
            ValidationMessage = string.Join(Environment.NewLine, errors);
            return false;
        }

        return true;
    }

    private Device BuildDevice() => new()
    {
        Name = Name.Trim(),
        TargetCycle = TargetCycle,
        OkCountAddress = OkCountAddress.Trim(),
        NgCountAddress = NgCountAddress.Trim(),
        StatusCountAddress = StatusCountAddress.Trim(),
        ProductionResetAddress = ProductionResetAddress.Trim(),
    };
}
