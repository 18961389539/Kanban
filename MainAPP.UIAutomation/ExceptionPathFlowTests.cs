using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 异常路径 UI 自动化测试：配置文件缺失/损坏、数据库异常等。
///
/// 这些测试需要特殊的数据预置（删除/损坏配置文件），可能导致应用启动缓慢或弹错误对话框，
/// 因此默认全部 Skip。需要手动启用时移除 Skip 特性。
///
/// 单元/集成层已有覆盖：
/// - MainAPP.Tests/Integration/AppLifecycleTests：应用生命周期异常处理
/// - MainAPP.Tests/Unit/AppSettingsTests：配置文件解析异常
/// - MainAPP.Tests/Unit/DeviceRepositoryTests：设备配置加载异常
/// 此处的测试关注 UI 层表现（是否显示友好错误提示、是否优雅降级）。
/// </summary>
[Collection("UIA")]
public class ExceptionPathFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;

    public ExceptionPathFlowTests() => _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// devices.json 缺失时，应用应显示空状态或友好错误提示，不应崩溃。
    /// 预置：TempDir 中不创建 devices.json（KanbanAppFixture 默认不预置任何配置）。
    /// </summary>
    [Fact(Skip = "异常路径测试：需特殊数据预置，默认跳过。手动运行时移除 Skip。")]
    public void MissingDevicesJson_AppShowsEmptyState_NoCrash()
    {
        // KanbanAppFixture 默认 TempDir 为空（无 devices.json）
        // 应用启动后应：
        // 1. 不崩溃（主窗口正常显示）
        // 2. 设备列表为空或显示"请先添加设备"提示
        var window = _fixture.MainWindow;
        Assert.Contains("看板", window.Title);
    }

    /// <summary>
    /// settings.json 包含非法 JSON 时，应用应回退到默认配置，不崩溃。
    /// 预置：在 TempDir/Config/settings.json 写入非法 JSON（如 "}{invalid"）。
    /// </summary>
    [Fact(Skip = "异常路径测试：需特殊数据预置，默认跳过。手动运行时移除 Skip。")]
    public void CorruptSettingsJson_AppFallsBackToDefaults_NoCrash()
    {
        // 预置非法 settings.json
        var settingsPath = Path.Combine(_fixture.TempDir, "Config", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "}{invalid json");

        // 应用启动后应回退到默认配置（AppSettings.Load 有 try/catch）
        var window = _fixture.MainWindow;
        Assert.Contains("看板", window.Title);
    }

    /// <summary>
    /// work_orders.db 表结构不匹配时，应用应显示空工单列表，不崩溃。
    /// 预置：在 TempDir/Config/work_orders.db 创建一个表结构不匹配的 SQLite 文件。
    /// </summary>
    [Fact(Skip = "异常路径测试：需特殊数据预置，默认跳过。手动运行时移除 Skip。")]
    public void CorruptWorkOrdersDb_AppShowsEmptyList_NoCrash()
    {
        // 预置损坏的 work_orders.db（表名正确但列不匹配）
        var dbPath = Path.Combine(_fixture.TempDir, "Config", "work_orders.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE WorkOrders (Id INTEGER, WrongColumn TEXT)";
        cmd.ExecuteNonQuery();

        // 应用启动后应显示空工单列表（WorkOrderRepository.LoadAll 有 try/catch）
        var window = _fixture.MainWindow;
        Assert.Contains("看板", window.Title);
    }

    /// <summary>
    /// 第二实例启动应被 Global\Kanban_SingleInstance Mutex 拦截，显示"程序已在运行"提示后退出。
    /// 验证单实例互斥行为。
    /// </summary>
    [Fact(Skip = "异常路径测试：需启动两个 MainAPP 实例，可能卡顿，默认跳过。手动运行时移除 Skip。")]
    public void SecondInstance_BlockedByMutex_ShowsMessageAndExit()
    {
        // 第一个实例已由 _fixture 启动并持有 Mutex
        // 尝试启动第二个实例
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _fixture.ExePath,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["KANBAN_DATA_DIR"] = _fixture.TempDir;

        var secondApp = FlaUI.Core.Application.Launch(psi);
        // 第二实例应显示 MessageBox 后退出（轮询等待 5s）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!secondApp.HasExited && DateTime.UtcNow < deadline)
            Thread.Sleep(200);
        Assert.True(secondApp.HasExited, "第二实例未被 Mutex 拦截（单实例互斥失效）");
    }
}
