using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 截图工具：附加到已运行的 MainAPP 进程，切换各页面并截图。
/// 运行前提：MainAPP.exe 已启动且连接 PlcSimulator。
/// 用法：dotnet test --filter ScreenshotCapture --logger "console;verbosity=detailed"
/// 截图保存到：解决方案根目录\screenshots\
///
/// 截图策略：
/// 1. 优先用 PrintWindow + PW_RENDERFULLCONTENT 捕获窗口本身（即使被遮挡/锁屏也能拿到内容）。
/// 2. 失败时回退到 BitBlt + CopyFromScreen（要求窗口在前台可见）。
/// 3. 每次截图前先 SetForegroundWindow，避免窗口处于最小化/后台导致黑图。
///
/// 注意：Process 对象在 .NET 10 中重复访问 MainWindowHandle 偶发 "No process is associated"
/// 异常，因此在构造函数中一次性捕获 hwnd 并保存为 IntPtr，后续所有 Win32 调用直接使用该句柄。
/// </summary>
[Collection("UIA")]
public class ScreenshotCaptureTests : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Application? _app;
    private readonly FlaUI.Core.AutomationElements.Window _mainWindow;
    private readonly string _saveDir;
    private readonly IntPtr _hwnd;

    // PrintWindow flags：PW_RENDERFULLCONTENT (0x00000002) 在 Win8.1+ 可捕获 DirectComposition / WPF 内容
    private const int PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SW_RESTORE = 9;

    public ScreenshotCaptureTests()
    {
        _automation = new UIA3Automation();
        _saveDir = Path.Combine(GetSolutionRoot(), "screenshots");
        Directory.CreateDirectory(_saveDir);

        // 附加到已运行的 MainAPP 进程。GetProcessesByName 返回的 Process 对象可能
        // 在后续访问 MainWindowHandle 时抛 "No process is associated"，所以这里
        // 一次性把句柄读出来存为 IntPtr，避免后续再访问 Process.MainWindowHandle。
        Process? proc = null;
        IntPtr hwnd = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName("MainAPP"))
        {
            try
            {
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    proc = p;
                    hwnd = h;
                    break;
                }
            }
            catch
            {
                // 忽略个别进程访问异常，继续找下一个
            }
        }
        if (proc == null || hwnd == IntPtr.Zero)
            throw new InvalidOperationException("MainAPP 未运行或无主窗口，请先启动 MainAPP.exe");
        _hwnd = hwnd;

        _app = Application.Attach(proc);
        _mainWindow = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(10))!;
        Assert.NotNull(_mainWindow);

        // 把窗口前置 + 还原，避免被锁屏/最小化遮挡
        EnsureForeground();
        Thread.Sleep(800);
    }

    [Fact]
    public void CaptureAllPages()
    {
        // 导航项的 AutomationProperties.Name（来自 NavItem.AccessibleName）
        var pages = new[]
        {
            ("01_home", "主页"),
            ("02_production_line", "产线总览"),
            ("03_alarm_center", "报警中心"),
            ("04_device_manager", "设备管理"),
            ("05_history_query", "历史查询"),
            ("06_overview", "生产复盘"),
            ("07_work_order", "工单管理"),
            ("08_settings", "设置"),
        };

        foreach (var (fileName, navName) in pages)
        {
            // 点击侧边栏导航项
            var navItem = _mainWindow.FindFirstDescendant(cf => cf.ByName(navName));
            if (navItem != null)
            {
                navItem.AsListBoxItem().Select();
                Console.WriteLine($"  -> 点击导航: {navName}");
            }
            else
            {
                Console.WriteLine($"  [WARN] 未找到导航项: {navName}，跳过");
                continue;
            }

            // 等待页面渲染（PageTransition 0.2s + 数据绑定刷新 + 缓冲）
            Thread.Sleep(1800);

            // 截图（先确保窗口前台）
            EnsureForeground();
            Thread.Sleep(150);
            CaptureWindow(fileName);
        }
    }

    private void EnsureForeground()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (IsIconic(_hwnd)) ShowWindowAsync(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
    }

    private void CaptureWindow(string name)
    {
        if (_hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"  [ERR] hwnd 为 0，跳过 {name}");
            return;
        }

        // 用 Win32 GetWindowRect 直接读窗口矩形（不依赖 Process.MainWindowHandle）
        GetWindowRect(_hwnd, out var rect);
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
        {
            // 回退到 FlaUI 的 BoundingRectangle
            var bounds = _mainWindow.BoundingRectangle;
            w = (int)bounds.Width;
            h = (int)bounds.Height;
            rect.Left = (int)bounds.X;
            rect.Top = (int)bounds.Y;
        }
        if (w <= 0 || h <= 0)
        {
            Console.WriteLine($"  [ERR] 窗口尺寸无效: {w} x {h}");
            return;
        }

        var path = Path.Combine(_saveDir, $"{name}.png");

        // 方案 1：PrintWindow 捕获窗口本身（即使被遮挡也能拿到内容）
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            var ok = false;
            try
            {
                // PW_RENDERFULLCONTENT 让 WPF/DirectComposition 内容也能被捕获
                ok = PrintWindow(_hwnd, hdc, PW_RENDERFULLCONTENT);
                if (!ok)
                {
                    // 老版本 Windows 不支持 PW_RENDERFULLCONTENT，回退到 0 标志
                    ok = PrintWindow(_hwnd, hdc, 0);
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }

            if (ok)
            {
                bmp.Save(path, ImageFormat.Png);
                Console.WriteLine($"  [OK] {name}.png ({w} x {h}) via PrintWindow");
                return;
            }
        }

        // 方案 2：回退到 CopyFromScreen（要求窗口前台可见）
        using (var bmp2 = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var g2 = Graphics.FromImage(bmp2))
        {
            g2.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(w, h));
            bmp2.Save(path, ImageFormat.Png);
            Console.WriteLine($"  [OK] {name}.png ({w} x {h}) via CopyFromScreen (fallback)");
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
        // Attach 模式不需要 Kill，只 Dispose FlaUI 资源
        _automation?.Dispose();
        _app?.Dispose();
    }
}
