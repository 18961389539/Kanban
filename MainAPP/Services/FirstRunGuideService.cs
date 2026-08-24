using System.Windows;
using Kanban.Collector.Core.Services;
using MainAPP.ViewModels;
using MainAPP.Views;

namespace MainAPP.Services;

public interface IFirstRunGuideService
{
    void TryShowAfterStartup(Window owner);
}

/// <summary>
/// 首次运行引导：欢迎说明 → PLC 连接 → 常用页面 → 帮助入口。
/// </summary>
public sealed class FirstRunGuideService(
    AppSettings appSettings,
    IUserHelpService userHelpService,
    MainWindowViewModel mainWindowViewModel) : IFirstRunGuideService
{
    public void TryShowAfterStartup(Window owner)
    {
        if (appSettings.HasCompletedFirstRunGuide || appSettings.RunMode == KanbanRunMode.Viewer)
            return;

        FirstRunGuideWindow? guideWindow = null;
        var viewModel = new FirstRunGuideViewModel(
            appSettings,
            openManual: () => userHelpService.OpenUserManual(owner),
            openSettings: () => mainWindowViewModel.Navigate("Settings"),
            complete: () => guideWindow?.Close());

        guideWindow = new FirstRunGuideWindow(viewModel)
        {
            Owner = owner
        };
        guideWindow.ShowDialog();
    }
}
