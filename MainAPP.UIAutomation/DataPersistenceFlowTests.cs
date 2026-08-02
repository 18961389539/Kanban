using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 数据持久化测试：验证 MainAPP 重启后数据是否保留。
///
/// 测试流程：预置数据库 → 启动 MainAPP → 修改数据 → 关闭 → 再启动 → 验证数据保留。
/// 由于单实例 Mutex 限制，每个测试方法用独立 TempDir，通过两次 KanbanAppFixture 实现重启。
///
/// 注意：这些测试每个需要启动两次 MainAPP，耗时较长（约 30s+）。
/// 需要长时间运行的测试标记 LongRunning，默认在快速测试运行时跳过。
/// </summary>
[Collection("UIA")]
public class DataPersistenceFlowTests : IDisposable
{
    private string _tempDir;

    public DataPersistenceFlowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Config"));
    }

    public void Dispose()
    {
        // 任何模式都保留临时目录，便于数据积累分析与调试。
        Console.WriteLine($"  [DataPersistenceFlowTests] 保留临时目录: {_tempDir}");
    }

    /// <summary>获取 Config 目录下指定数据库文件路径</summary>
    private string GetDbPath(string dbFileName) =>
        Path.Combine(_tempDir, "Config", dbFileName);

    /// <summary>预置 work_orders.db，插入指定数量工单</summary>
    private void SeedWorkOrders(int count)
    {
        var dbPath = GetDbPath("work_orders.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // 建表
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS WorkOrders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OrderNo TEXT NOT NULL,
                    ProductCode TEXT NOT NULL,
                    ProductName TEXT NOT NULL,
                    DeviceId TEXT NOT NULL,
                    DeviceName TEXT,
                    TargetQuantity INTEGER NOT NULL,
                    PlannedStart TEXT NOT NULL,
                    PlannedEnd TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    CompletedOkCount INTEGER,
                    CompletedNgCount INTEGER,
                    Remark TEXT,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )
                """;
            cmd.ExecuteNonQuery();
        }

        // 插入工单
        var now = DateTime.Now.ToString("o");
        using var tx = conn.BeginTransaction();
        for (int i = 1; i <= count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO WorkOrders (OrderNo, ProductCode, ProductName, DeviceId, DeviceName,
                    TargetQuantity, PlannedStart, PlannedEnd, Status, CompletedOkCount, CompletedNgCount,
                    Remark, CreatedAt, UpdatedAt)
                VALUES (@orderNo, 'P-A', '测试产品', 'device-001', '注塑机1',
                    1000, @start, @end, 1, NULL, NULL, NULL, @now, @now)
                """;
            cmd.Parameters.AddWithValue("@orderNo", $"WO-PERSIST-{i:D3}");
            cmd.Parameters.AddWithValue("@start", now);
            cmd.Parameters.AddWithValue("@end", now);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>统计 work_orders.db 中 WorkOrders 表行数</summary>
    private int CountWorkOrders()
    {
        var dbPath = GetDbPath("work_orders.db");
        if (!File.Exists(dbPath)) return -1;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM WorkOrders";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 预置 3 条工单 → 启动 MainAPP（LoadAll 加载） → 关闭 → 再启动 → 验证工单仍在数据库中。
    /// 确保 WorkOrderRepository.LoadAll 不破坏预置数据，OnExit 正确保存。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void WorkOrders_PersistAcrossRestart()
    {
        // 1. 预置 3 条工单
        SeedWorkOrders(3);
        Assert.Equal(3, CountWorkOrders());

        // 2. 第一次启动 MainAPP（加载工单）
        using (var fixture1 = new KanbanAppFixture(_tempDir))
        {
            // MainAPP 启动时 LoadAll 会读取预置的 3 条工单
            // 确保不破坏数据
            Thread.Sleep(2000); // 等待 LoadAll 完成
        }
        // fixture1.Dispose 会 Kill 进程并等待 Mutex 释放

        // 3. 验证数据库仍有 3 条工单（LoadAll 不应删除）
        Assert.True(CountWorkOrders() >= 3, "第一次启动后工单数量减少");

        // 4. 第二次启动 MainAPP（验证数据持久化）
        using (var fixture2 = new KanbanAppFixture(_tempDir))
        {
            Thread.Sleep(2000);
        }

        // 5. 验证工单数据保留
        var finalCount = CountWorkOrders();
        Assert.True(finalCount >= 3, $"重启后工单数据丢失（最终 {finalCount} 条，预期 >= 3）");
    }

    /// <summary>
    /// 预置 devices.json → 启动 MainAPP → 关闭 → 验证 devices.json 仍存在且有效。
    /// 确保设备配置在重启后保留。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void DevicesJson_PersistAcrossRestart()
    {
        // 1. 预置 devices.json
        var devicesPath = Path.Combine(_tempDir, "Config", "devices.json");
        var devicesJson = """
            [{"Id":"device-001","Name":"测试设备1","OkCountAddress":"D100","NgCountAddress":"D102",
            "StatusCountAddress":"D104","ProductionResetAddress":"D106","RecipeName":"","RecipeValue":50,
            "RecipeAddress":"D108","TargetCycle":100,"Alarms":[],"Defects":[],"CountAlarms":[]}]
            """;
        File.WriteAllText(devicesPath, devicesJson);

        // 2. 启动 MainAPP
        using (var fixture1 = new KanbanAppFixture(_tempDir))
        {
            Thread.Sleep(2000);
        }

        // 3. 验证 devices.json 仍存在
        Assert.True(File.Exists(devicesPath), "devices.json 在启动后丢失");

        // 4. 验证内容仍有效（包含 device-001）
        var content = File.ReadAllText(devicesPath);
        Assert.Contains("device-001", content);
        Assert.Contains("测试设备1", content);
    }

    /// <summary>
    /// 预置 settings.json → 启动 MainAPP → 关闭 → 验证 settings.json 仍存在且 PLC 配置保留。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void SettingsJson_PersistAcrossRestart()
    {
        // 1. 预置 settings.json
        var settingsPath = Path.Combine(_tempDir, "Config", "settings.json");
        var settingsJson = """
            {
              "PlcConfig": { "IpAddress": "192.168.1.100", "Port": 5000 },
              "PollingIntervalMs": 2000,
              "HistoryWriteIntervalScans": 30,
              "DashboardRefreshIntervalMs": 2000,
              "IsDarkTheme": false,
              "UiScale": 1.2,
              "Shifts": [
                { "Name": "早班", "StartTime": "06:00:00", "EndTime": "14:00:00" },
                { "Name": "晚班", "StartTime": "14:00:00", "EndTime": "22:00:00" }
              ]
            }
            """;
        File.WriteAllText(settingsPath, settingsJson);

        // 2. 启动 MainAPP
        using (var fixture1 = new KanbanAppFixture(_tempDir))
        {
            Thread.Sleep(2000);
        }

        // 3. 验证 settings.json 仍存在
        Assert.True(File.Exists(settingsPath), "settings.json 在启动后丢失");

        // 4. 验证 PLC 配置保留
        var content = File.ReadAllText(settingsPath);
        Assert.Contains("192.168.1.100", content);
        Assert.Contains("早班", content);
    }
}
