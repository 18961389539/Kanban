using CommunityToolkit.Mvvm.ComponentModel;
using MainAPP.Resources;

namespace MainAPP.Services;

public enum ApplicationRuntimeState
{
    Starting,
    LoadingConfiguration,
    MigratingDatabase,
    Ready,
    StartingAcquisition,
    Running,
    Degraded,
    Failed
}

public interface IApplicationRuntime
{
    ApplicationRuntimeState State { get; }
    string StatusMessage { get; }
    bool IsDatabaseReady { get; }
    bool IsAcquisitionRunning { get; }
    string? StartupError { get; }
}

public partial class ApplicationRuntime : ObservableObject, IApplicationRuntime
{
    [ObservableProperty]
    private ApplicationRuntimeState _state = ApplicationRuntimeState.Starting;

    [ObservableProperty]
    private string _statusMessage = Strings.M133;

    [ObservableProperty]
    private bool _isDatabaseReady;

    [ObservableProperty]
    private bool _isAcquisitionRunning;

    [ObservableProperty]
    private string? _startupError;

    public void SetState(ApplicationRuntimeState state, string message)
    {
        State = state;
        StatusMessage = message;
    }

    public void SetFailure(Exception exception)
    {
        StartupError = exception.Message;
        SetState(ApplicationRuntimeState.Failed, Strings.M130);
    }
}
