using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 诊断测试：attach MainAPP 并 dump 窗口结构 + 截图当前窗口（不依赖导航项匹配）。
/// 用于排查"首屏黑屏"等结构性问题：输出每个 NavigationPageHost 的 IsVisible、
/// 子元素数量，以及当前窗口截图到 screenshots/current.png。
/// </summary>
[Collection("UIA")]
public class DiagnosticsDumpTests : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly FlaUI.Core.Application? _app;
    private readonly FlaUI.Core.AutomationElements.Window _mainWindow;
    private readonly IntPtr _hwnd;
    private readonly string _saveDir;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SW_RESTORE = 9;

    public DiagnosticsDumpTests()
    {
        _automation = new UIA3Automation();
        _saveDir = Path.Combine(GetSolutionRoot(), "screenshots");
        Directory.CreateDirectory(_saveDir);

        Process? proc = null;
        IntPtr hwnd = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName("MainAPP"))
        {
            try
            {
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero) { proc = p; hwnd = h; break; }
            }
            catch { }
        }
        if (proc == null || hwnd == IntPtr.Zero)
            throw new InvalidOperationException("MainAPP 未运行");
        _hwnd = hwnd;

        _app = FlaUI.Core.Application.Attach(proc);
        _mainWindow = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(10))!;

        if (IsIconic(_hwnd)) ShowWindowAsync(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
        Thread.Sleep(800);
    }

    [Fact]
    public void DumpWindowTree_And_CaptureCurrent()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== MainWindow hwnd={_hwnd} ===");

        // 递归 dump 整棵树（限制深度避免爆炸）
        DumpElement(_mainWindow, 0, sb, 6);

        var reportPath = Path.Combine(_saveDir, "diagnostics_dump.txt");
        File.WriteAllText(reportPath, sb.ToString());
        Console.WriteLine(sb.ToString());

        // 同时 CopyFromScreen 抓当前窗口（即使 PrintWindow 失败也能拿到）
        GetWindowRect(_hwnd, out var rect);
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w > 0 && h > 0)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(w, h));
            bmp.Save(Path.Combine(_saveDir, "current.png"), ImageFormat.Png);
            Console.WriteLine($"[OK] current.png saved ({w}x{h})");
        }
    }

    private static void DumpElement(AutomationElement el, int depth, System.Text.StringBuilder sb, int maxDepth)
    {
        if (depth > maxDepth) return;
        try
        {
            var indent = new string(' ', depth * 2);
            var cls = el.ClassName ?? "";
            var name = el.Name ?? "";
            var type = el.ControlType;
            var offscreen = el.Properties.IsOffscreen.TryGetValue(out var v) ? v : (bool?)null;
            var rect = el.BoundingRectangle;
            sb.AppendLine($"{indent}{type} name='{name}' cls='{cls}' offscreen={offscreen} rect=({rect.X:F0},{rect.Y:F0},{rect.Width:F0}x{rect.Height:F0})");
            foreach (var child in el.FindAllChildren())
            {
                DumpElement(child, depth + 1, sb, maxDepth);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine(new string(' ', depth * 2) + "[ERR " + ex.GetType().Name + "] " + ex.Message);
        }
    }

    private static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Kanban.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new FileNotFoundException("未找到解决方案根目录");
    }

    public void Dispose()
    {
        _automation?.Dispose();
        _app?.Dispose();
    }
}
