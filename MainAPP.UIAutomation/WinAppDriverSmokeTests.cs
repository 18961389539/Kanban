using System.Diagnostics;
using System.IO;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Windows;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// WinAppDriver 冒烟测试。
///
/// WinAppDriver 需单独安装（WinAppDriverSetup.exe 或 choco install winappdriver）并启动服务
/// （默认监听 127.0.0.1:4723）。
///
/// 启用方式二选一：
///   1. 设置环境变量 KANBAN_RUN_WAD_TESTS=1 + 启动 WinAppDriver 服务（CI 推荐路径，见 ci/run-ui-automation.ps1 -WithWinAppDriver）
///   2. 直接修改下方 SkipWhenWadDisabled 的返回条件
///
/// 服务不可用时不报失败，避免本地无 WAD 环境变红。
/// </summary>
[Collection("UIA")]
public class WinAppDriverSmokeTests : IDisposable
{
    private const int WinAppDriverPort = 4723;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private WindowsDriver? _driver;

    /// <summary>
    /// WinAppDriver 冒烟测试：仅当环境变量 KANBAN_RUN_WAD_TESTS=1 且 WinAppDriver 服务可用时执行，
    /// 否则通过 Assert.Skip 跳过（xUnit 2.6+ 原生支持）。
    /// </summary>
    [Fact]
    public void WinAppDriver_AppLaunches_MainWindowVisible()
    {
        // 未声明启用 → 显式 Skip（计入 Skipped，杜绝"零断言静默绿"；审查修复 2026-09-03）
        if (!IsWinAppDriverEnabled())
            Assert.Skip("KANBAN_RUN_WAD_TESTS!=1：未启用 WinAppDriver 冒烟测试");

        var options = new AppiumOptions();
        options.App = KanbanAppFixture.LocateExeForPublicUse();
        options.AddAdditionalAppiumOption("platformName", "Windows");
        options.AddAdditionalAppiumOption("deviceName", "WindowsPC");

        var uri = new Uri($"http://127.0.0.1:{WinAppDriverPort}/wd/hub");
        _driver = new WindowsDriver(uri, options, StartupTimeout);
        Assert.NotNull(_driver);

        // MainAPP MainWindow.Title 含 "看板" 字样
        Assert.Contains("看板", _driver.Title ?? string.Empty);
    }

    /// <summary>
    /// 前置条件：
    ///   - 环境变量 KANBAN_RUN_WAD_TESTS=1（CI 脚本设置；本地默认不设）
    ///   - WinAppDriver 服务监听 127.0.0.1:4723
    /// 不满足时测试 Assert.Skip（计入 Skipped）；已声明启用但服务未运行则显式失败。
    /// </summary>
    private static bool IsWinAppDriverEnabled()
    {
        var flag = Environment.GetEnvironmentVariable("KANBAN_RUN_WAD_TESTS");
        if (!string.Equals(flag, "1", StringComparison.Ordinal))
        {
            return false;
        }
        if (!IsWinAppDriverRunning())
        {
            // 已声明启用但服务未运行 → 显式失败，便于 CI 排查
            throw new InvalidOperationException(
                "KANBAN_RUN_WAD_TESTS=1 但未检测到 WinAppDriver 服务（127.0.0.1:" + WinAppDriverPort + "）。" +
                "请先运行 ci/run-ui-automation.ps1 -WithWinAppDriver 或手动启动 WinAppDriver.exe。");
        }
        return true;
    }

    /// <summary>检测 WinAppDriver 服务是否在默认端口监听。</summary>
    private static bool IsWinAppDriverRunning()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect("127.0.0.1", WinAppDriverPort, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            return success && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _driver?.Quit(); } catch { /* ignore */ }
        _driver?.Dispose();
    }
}
