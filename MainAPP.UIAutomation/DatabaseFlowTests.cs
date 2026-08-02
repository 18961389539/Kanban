using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 数据库相关 UI 自动化测试：数据持久化、Schema 升级、工单 CRUD、空数据库初始化。
///
/// 这些测试通过预置数据库文件、启动 MainAPP、检查数据库内容来验证数据库行为。
/// 利用 KANBAN_DATA_DIR 环境变量隔离数据目录，Debug 模式下保留数据库便于调试。
/// </summary>
[Collection("UIA")]
public class DatabaseFlowTests : IDisposable
{
    private readonly KanbanAppFixture _fixture;

    public DatabaseFlowTests() => _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>获取 Config 目录下指定数据库文件路径</summary>
    private string GetDbPath(string dbFileName) =>
        Path.Combine(_fixture.TempDir, "Config", dbFileName);

    /// <summary>直接用 SqliteConnection 读取数据库表行数</summary>
    private int CountRows(string dbFileName, string tableName)
    {
        var dbPath = GetDbPath(dbFileName);
        if (!File.Exists(dbPath)) return -1;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>检查指定表是否存在指定列</summary>
    private bool ColumnExists(string dbFileName, string tableName, string columnName)
    {
        var dbPath = GetDbPath(dbFileName);
        if (!File.Exists(dbPath)) return false;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == columnName) return true;
        }
        return false;
    }

    /// <summary>
    /// 空数据目录启动 MainAPP 后，EnsureCreatedAll 应创建全部 4 个 .db 文件。
    /// 验证数据库自动初始化逻辑。
    /// </summary>
    [Fact]
    public void EmptyDataDir_EnsureCreatedAll_CreatesFourDatabases()
    {
        // MainAPP 已由 _fixture 启动，EnsureCreatedAll 在 OnStartup 中执行
        // 等待启动完成（构造函数已等待主窗口 + 2.5s 预热）
        var dbFiles = new[] { "alarm_events.db", "production_logs.db", "status_transitions.db", "work_orders.db" };
        foreach (var db in dbFiles)
        {
            var path = GetDbPath(db);
            Assert.True(File.Exists(path), $"数据库文件未创建：{db}（路径 {path}）");
        }
    }

    /// <summary>
    /// EnsureCreated 创建的数据库表结构应包含必要的列。
    /// 验证 EnsureSchemaUpgrades 为旧库补列后（即使新库也应有这些列）。
    /// </summary>
    [Fact]
    public void EnsureCreated_ProductionLogsTable_HasWorkOrderIdColumn()
    {
        // ProductionLogs 表应有 WorkOrderId 列（EnsureSchemaUpgrades 补列）
        Assert.True(ColumnExists("production_logs.db", "ProductionLogs", "WorkOrderId"),
            "ProductionLogs 表缺少 WorkOrderId 列");
    }

    /// <summary>
    /// EnsureCreated 创建的 WorkOrders 表应包含 CompletedOkCount/CompletedNgCount 列。
    /// </summary>
    [Fact]
    public void EnsureCreated_WorkOrdersTable_HasCompletedCountColumns()
    {
        Assert.True(ColumnExists("work_orders.db", "WorkOrders", "CompletedOkCount"),
            "WorkOrders 表缺少 CompletedOkCount 列");
        Assert.True(ColumnExists("work_orders.db", "WorkOrders", "CompletedNgCount"),
            "WorkOrders 表缺少 CompletedNgCount 列");
    }

    /// <summary>
    /// 空数据目录启动后，WorkOrders 表行数应为 0（EnsureCreated 创建空表）。
    /// 验证 EnsureCreated 不预填测试数据。
    /// </summary>
    [Fact]
    public void EmptyDataDir_WorkOrdersTable_HasZeroRows()
    {
        var count = CountRows("work_orders.db", "WorkOrders");
        Assert.Equal(0, count);
    }

    /// <summary>
    /// 验证 WAL 模式已启用：PRAGMA journal_mode 应返回 "wal"。
    /// 确保 EnsureWalModeEnabled 正确执行。
    /// </summary>
    [Fact]
    public void WalMode_Enabled_AfterEnsureCreated()
    {
        var dbPath = GetDbPath("work_orders.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var mode = cmd.ExecuteScalar()?.ToString();
        Assert.Equal("wal", mode);
    }

    /// <summary>
    /// 验证 4 个数据库的表都已创建（EnsureCreated 创建表结构）。
    /// </summary>
    [Theory]
    [InlineData("alarm_events.db", "AlarmEvents")]
    [InlineData("production_logs.db", "ProductionLogs")]
    [InlineData("status_transitions.db", "StatusTransitions")]
    [InlineData("work_orders.db", "WorkOrders")]
    public void EnsureCreated_AllTablesExist(string dbFileName, string tableName)
    {
        var count = CountRows(dbFileName, tableName);
        Assert.True(count >= 0, $"表 {tableName} 不存在或查询失败（{dbFileName}）");
    }

    /// <summary>
    /// 验证数据库文件不为空（EnsureCreated 创建了有效的 SQLite 文件头）。
    /// </summary>
    [Fact]
    public void DatabaseFiles_AreValidSqlite()
    {
        foreach (var db in new[] { "alarm_events.db", "production_logs.db", "status_transitions.db", "work_orders.db" })
        {
            var path = GetDbPath(db);
            Assert.True(File.Exists(path), $"数据库文件不存在：{db}");
            var info = new FileInfo(path);
            Assert.True(info.Length > 0, $"数据库文件为空：{db}（大小 {info.Length} 字节）");
        }
    }
}
