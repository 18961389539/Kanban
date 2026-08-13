using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>临时诊断：附加运行中的 MainAPP，导航复盘页点击刷新并截图。</summary>
[Collection("UIA")]
public class RefreshOverviewDiagTests : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly FlaUI.Core.AutomationElements.Window _mainWindow;

    public RefreshOverviewDiagTests()
    {
        _automation = new UIA3Automation();
        Process? proc = null;
        foreach (var p in Process.GetProcessesByName("MainAPP"))
        {
            if (p.MainWindowHandle != IntPtr.Zero) { proc = p; break; }
        }
        if (proc == null) throw new InvalidOperationException("MainAPP 未运行");
        _mainWindow = Application.Attach(proc).GetMainWindow(_automation, TimeSpan.FromSeconds(10))!;
    }

    [Fact]
    public void RefreshOverviewAndScreenshot()
    {
        var saveDir = Path.Combine(FindRepoRoot(), "screenshots");
        Directory.CreateDirectory(saveDir);

        // 导航到生产复盘
        var nav = _mainWindow.FindFirstDescendant(cf => cf.ByName("生产复盘"));
        Assert.NotNull(nav);
        nav.AsListBoxItem().Select();
        Thread.Sleep(2000);

        // 点击刷新
        var refresh = _mainWindow.FindFirstDescendant(cf => cf.ByName("刷新复盘数据"));
        if (refresh != null)
        {
            refresh.AsButton().Invoke();
            Console.WriteLine("[DIAG] 刷新已点击");
        }
        else Console.WriteLine("[DIAG] 未找到刷新按钮");
        Thread.Sleep(3000);

        // 截图
        _mainWindow.CaptureToFile(Path.Combine(saveDir, "06_overview_refreshed.png"));
        Console.WriteLine($"[DIAG] 已截图: {Path.Combine(saveDir, "06_overview_refreshed.png")}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Kanban.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new FileNotFoundException("未找到 Kanban.slnx");
    }

    public void Dispose() => _automation.Dispose();
}
