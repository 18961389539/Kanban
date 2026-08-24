using System.IO;
using System.Windows;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using MainAPP.Views;
using Serilog;

namespace MainAPP.Services;

public interface IUserHelpService
{
    void OpenUserManual(Window? owner);
}

/// <summary>
/// 打开内置使用手册（Markdown → HTML，随界面语言选择中/英版本）。
/// </summary>
public sealed class UserHelpService(AppSettings appSettings) : IUserHelpService
{
    private HelpWindow? _openWindow;

    public void OpenUserManual(Window? owner)
    {
        var manualPath = ResolveManualPath();
        if (manualPath is null)
        {
            HandyControl.Controls.MessageBox.Show(
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
                _openWindow.NavigateHtml(html, Strings.Ux_HelpWindowTitle);
                _openWindow.Activate();
                if (owner is not null && !_openWindow.IsActive)
                    _openWindow.Focus();
                return;
            }

            _openWindow = new HelpWindow(Strings.Ux_HelpWindowTitle, html)
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
            HandyControl.Controls.MessageBox.Show(
                ex.Message,
                Strings.Ux_HelpWindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string GetExpectedManualFileName()
        => IsEnglishManual() ? "MainAPP_User_Manual_EN.md" : "MainAPP用户使用手册.md";

    private bool IsEnglishManual()
        => LocalizationCatalog.Normalize(appSettings.EffectiveLanguageCode)
            .StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private string? ResolveManualPath()
    {
        var fileName = GetExpectedManualFileName();
        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { "手册", "Help", "Manual" })
        {
            var candidate = Path.Combine(baseDir, dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        // 开发环境：从输出目录向上查找 MainAPP/手册
        var current = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            var devCandidate = Path.Combine(current.FullName, "MainAPP", "手册", fileName);
            if (File.Exists(devCandidate))
                return devCandidate;
            current = current.Parent;
        }

        return null;
    }
}
