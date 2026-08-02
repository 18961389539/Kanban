using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.UIA3;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 启动真实 MainAPP.exe 进程的测试夹具。
/// 关键设计：
/// 1. 通过环境变量 KANBAN_DATA_DIR 重定向 %APPDATA%/Kanban 到临时目录，避免污染真实数据。
/// 2. 每个测试方法创建独立 TempDir，保证 Mutex 唯一性与文件隔离。
/// 3. 夹具 Dispose 时强杀进程并清理目录，防止测试失败遗留僵尸进程。
/// 4. 不作为 ICollectionFixture 共享：每测试新建实例可彻底隔离状态，避免窗口/UI 树污染。
/// 5. 构造函数内置 3 次重试：上一个测试的 Global\Kanban_SingleInstance Mutex 释放有延迟，
///    首次启动可能因 Mutex 被持有而立即退出，重试可自愈。
/// </summary>
public sealed class KanbanAppFixture : IDisposable
{
    public string TempDir { get; }
    public string ExePath { get; }
    public Application App { get; }
    public UIA3Automation Automation { get; }
    public FlaUI.Core.AutomationElements.Window MainWindow { get; }

    public KanbanAppFixture(string? tempDir = null)
    {
        TempDir = tempDir ?? Path.Combine(Path.GetTempPath(), "KanbanUIA_" + Guid.NewGuid().ToString("N"));
        if (tempDir == null) Directory.CreateDirectory(TempDir);

        ExePath = LocateExe();

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["KANBAN_DATA_DIR"] = TempDir;

        Automation = new UIA3Automation();

        // 最多重试 3 次：上一个测试的 MainAPP 被 Kill 后，OS 释放 Global\Kanban_SingleInstance
        // Mutex 可能有数秒延迟。首次启动可能因 Mutex 被持有而立即退出，等待 3 秒后重试可自愈。
        Application? app = null;
        FlaUI.Core.AutomationElements.Window? mainWindow = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                app = Application.Launch(psi);
                // 主窗口 + 后台 PLC 采集启动有延迟（OnStartup 异步），用 30s 等待避免 CI 慢机超时
                mainWindow = app.GetMainWindow(Automation, TimeSpan.FromSeconds(30))!;
                Assert.NotNull(mainWindow);
                // 等待 OnMainWindowLoaded 中的预热导航完成（依次切到 1→2→3→4→0），否则元素可能未渲染
                Thread.Sleep(2500);
                break; // 成功
            }
            catch (Exception ex)
            {
                // 清理本次失败的进程，避免泄漏
                try { app?.Kill(); } catch { }
                var exited = false;
                if (app != null)
                {
                    var sw = Stopwatch.StartNew();
                    while (!app.HasExited && sw.ElapsedMilliseconds < 5000)
                        Thread.Sleep(100);
                    exited = app.HasExited;
                    try { app.Dispose(); } catch { }
                }
                app = null;

                if (attempt < 2)
                {
                    // 等待 OS 释放 Mutex（Kill 后句柄关闭有延迟）
                    Console.WriteLine($"  [KanbanAppFixture] 启动失败（尝试 {attempt + 1}/3，进程退出={exited}），等待 3 秒后重试: {ex.Message.Split('\n')[0]}");
                    Thread.Sleep(3000);
                }
                else
                {
                    Automation.Dispose();
                    throw;
                }
            }
        }

        App = app!;
        MainWindow = mainWindow!;
    }

    /// <summary>定位 MainAPP.exe：从测试 bin 目录向上查找 sln 根，再拼 MainAPP/bin/...。</summary>
    private static string LocateExe()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        // 向上查找直至发现 Kanban.slnx
        var slnRoot = dir;
        while (slnRoot != null && !File.Exists(Path.Combine(slnRoot.FullName, "Kanban.slnx")))
            slnRoot = slnRoot.Parent;
        if (slnRoot == null)
            throw new FileNotFoundException("未找到 Kanban.slnx 所在的解决方案根目录");
        var candidates = new[]
        {
            Path.Combine(slnRoot.FullName, "MainAPP", "bin", "Debug", "net10.0-windows", "MainAPP.exe"),
            Path.Combine(slnRoot.FullName, "MainAPP", "bin", "Release", "net10.0-windows", "MainAPP.exe"),
        };
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe == null)
            throw new FileNotFoundException(
                $"未找到 MainAPP.exe，请先 dotnet build MainAPP/MainAPP.csproj。已搜索：{string.Join(" / ", candidates)}");
        return exe;
    }

    /// <summary>
    /// 公开 LocateExe：供 WinAppDriver 测试用例查找待启动的 MainAPP.exe 路径。
    /// 与 KanbanAppFixture 共享同一查找逻辑，避免重复实现。
    /// </summary>
    public static string LocateExeForPublicUse() => LocateExe();

    public void Dispose()
    {
        try
        {
            // 先 Kill 再 Close：Close 会触发应用 OnExit 异步保存（含 PLC StopAsync），
            // 若应用卡住无法退出，Kill 强制结束避免后续测试无法获得 Mutex
            if (!App.HasExited) App.Kill();
            // 等待进程完全退出，确保 Global\Kanban_SingleInstance Mutex 被 OS 释放。
            // 否则下一个测试启动 MainAPP 时会误判为"已有实例在运行"而无法显示主窗口。
            var sw = Stopwatch.StartNew();
            while (!App.HasExited && sw.ElapsedMilliseconds < 5000)
                Thread.Sleep(100);
            App.Dispose();
            // 额外等待 2 秒，确保 OS 完全释放 Global\Kanban_SingleInstance Mutex。
            // TerminateProcess 后 Mutex 句柄关闭有延迟，不等待会导致下一个测试启动失败。
            Thread.Sleep(2000);
        }
        catch { /* ignore */ }
        Automation.Dispose();
        // 任何模式都保留 TempDir（含 .db 数据库文件），便于调试失败测试与数据积累分析。
        // 不删除临时目录，避免误删测试运行期间积累的数据库内容。
        Console.WriteLine($"  [KanbanAppFixture] 保留临时目录: {TempDir}");
    }
}
