using System.Windows;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using MainAPP.Views;
using Serilog;

namespace MainAPP.Services;

public interface IUserHelpService
{
    void OpenUserManual(Window? owner, string? anchor = null);
}

/// <summary>
/// 打开内置使用手册（Markdown → HTML，随界面语言选择中/英版本）。
/// </summary>
public sealed class UserHelpService(AppSettings appSettings, IDialogService dialog) : IUserHelpService
{
    private HelpWindow? _openWindow;

    public void OpenUserManual(Window? owner, string? anchor = null)
    {
        var manualPath = ResolveManualPath();
        if (manualPath is null)
        {
            dialog.Show(
                string.Format(Strings.Ux_HelpManualMissing, GetExpectedManualFileName()),
                Strings.Ux_HelpWindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var html = MarkdownHelpRenderer.RenderFile(manualPath, AppContext.BaseDirectory);
            if (_openWindow is { IsVisible: true })
            {
                _openWindow.NavigateHtml(html, Strings.Ux_HelpWindowTitle, anchor);
                _openWindow.Activate();
                if (owner is not null && !_openWindow.IsActive)
                    _openWindow.Focus();
                return;
            }

            _openWindow = new HelpWindow(Strings.Ux_HelpWindowTitle, html, anchor)
            {
                Owner = owner
            };
            _openWindow.Closed += (_, _) => _openWindow = null;
            _openWindow.Show();
            _openWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开使用手册失败：{ManualPath}", manualPath);
            dialog.Show(
                ex.Message,
                Strings.Ux_HelpWindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string GetExpectedManualFileName() => UserManualLocator.ExpectedFileName(appSettings);

    private string? ResolveManualPath() => UserManualLocator.Resolve(appSettings);
}
