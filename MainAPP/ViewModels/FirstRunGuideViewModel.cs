using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

public sealed partial class FirstRunGuideViewModel : ObservableObject
{
    private readonly AppSettings _appSettings;
    private readonly Action _openManual;
    private readonly Action _openSettings;
    private readonly Action _complete;

    [ObservableProperty]
    private int _stepIndex;

    public FirstRunGuideViewModel(
        AppSettings appSettings,
        Action openManual,
        Action openSettings,
        Action complete)
    {
        _appSettings = appSettings;
        _openManual = openManual;
        _openSettings = openSettings;
        _complete = complete;
    }

    public string WindowTitle => Strings.Ux_FirstRunTitle;

    public string StepTitle => StepIndex switch
    {
        0 => Strings.Ux_FirstRunWelcomeTitle,
        1 => Strings.Ux_FirstRunPlcTitle,
        2 => Strings.Ux_FirstRunNavTitle,
        _ => Strings.Ux_FirstRunFinishTitle,
    };

    public string StepBody => StepIndex switch
    {
        0 => Strings.Ux_FirstRunWelcomeBody,
        1 => Strings.Ux_FirstRunPlcBody,
        2 => Strings.Ux_FirstRunNavBody,
        _ => Strings.Ux_FirstRunFinishBody,
    };

    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == 3;
    public bool ShowPlcActions => StepIndex == 1;
    public string StepProgressLabel => $"{StepIndex + 1} / 4";

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepBody));
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(ShowPlcActions));
        OnPropertyChanged(nameof(StepProgressLabel));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => StepIndex = Math.Max(0, StepIndex - 1);

    private bool CanGoBack() => StepIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => StepIndex = Math.Min(3, StepIndex + 1);

    private bool CanGoNext() => StepIndex < 3;

    [RelayCommand]
    private void Skip() => CompleteGuide();

    [RelayCommand(CanExecute = nameof(IsLastStep))]
    private void Finish() => CompleteGuide();

    [RelayCommand]
    private void OpenManual()
    {
        _openManual();
        CompleteGuide();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _openSettings();
        CompleteGuide();
    }

    private void CompleteGuide()
    {
        if (!_appSettings.HasCompletedFirstRunGuide)
        {
            _appSettings.HasCompletedFirstRunGuide = true;
            try { _appSettings.Save(); }
            catch { /* 引导完成标记失败不阻断关闭 */ }
        }

        _complete();
    }
}
