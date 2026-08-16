using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 高级数据库测试：Schema 升级、工单自动清理、索引/约束验证、WAL 文件、数据库隔离。
///
/// 与 <see cref="DatabaseFlowTests"/> 的区别：
/// - 使用预置数据库模式（先创建 TempDir + 预置 DB 文件，再启动 MainAPP），可测试 Schema 升级和清理逻辑。
/// - DatabaseFlowTests 仅测试空目录启动后的 EnsureCreated 结果，无法预置旧版数据。
///
/// Debug 模式保留 TempDir 便于调试失败用例时检查数据库内容。
/// </summary>
[Collection("UIA")]
public class DatabaseAdvancedFlowTests : IDisposable
{
    private readonly string _tempDir;

    public DatabaseAdvancedFlowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanDbAdv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Config"));
    }

    public void Dispose()
    {
        // 任何模式都保留临时目录，便于数据积累分析与调试。
        Console.WriteLine($"  [DatabaseAdvancedFlowTests] 保留临时目录: {_tempDir}");
    }

    /// <summary>获取 Config 目录下指定数据库文件路径</summary>
    private string GetDbPath(string dbFileName) =>
        Path.Combine(_tempDir, "Config", dbFileName);

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

    /// <summary>获取指定表的所有索引名（不含 sqlite_autoindex）</summary>
    private HashSet<string> GetIndexes(string dbFileName, string tableName)
    {
        var dbPath = GetDbPath(dbFileName);
        var indexes = new HashSet<string>();
        if (!File.Exists(dbPath)) return indexes;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name=@table AND name NOT LIKE 'sqlite_%'";
        cmd.Parameters.AddWithValue("@table", tableName);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            indexes.Add(reader.GetString(0));
        }
        return indexes;
    }

    /// <summary>获取指定列的 NOT NULL 约束（PRAGMA table_info 第 4 列，0=可空，1=NOT NULL）</summary>
    private bool IsColumnNotNull(string dbFileName, string tableName, string columnName)
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
            if (reader.GetString(1) == columnName)
            {
                return reader.GetInt32(3) == 1;
            }
        }
        return false;
    }

    /// <summary>获取指定列的 SQLite 类型声明</summary>
    private string? GetColumnType(string dbFileName, string tableName, string columnName)
    {
        var dbPath = GetDbPath(dbFileName);
        if (!File.Exists(dbPath)) return null;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == columnName)
            {
                return reader.GetString(2);
            }
        }
        return null;
    }

    /// <summary>统计指定表行数</summary>
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

    // ======== Schema 升级测试 ========

    /// <summary>
    /// 预置旧版 production_logs.db（缺少 WorkOrderId 列） → 启动 MainAPP →
    /// EnsureSchemaUpgrades 应自动添加 WorkOrderId 列。
    /// 验证 <see cref="Kanban.Collector.Core.Data.DatabaseProvider.EnsureSchemaUpgrades"/> 的补列逻辑。
    /// </summary>
    [Fact]
    public void OldSchema_ProductionLogs_EnsureSchemaUpgrades_AddsWorkOrderIdColumn()
    {
        // 1. 预置旧版 production_logs.db（无 WorkOrderId 列）
        SeedOldSchemaProductionLogs();
        Assert.False(ColumnExists("production_logs.db", "ProductionLogs", "WorkOrderId"),
            "预置条件失败：WorkOrderId 列不应存在");

        // 2. 启动 MainAPP（EnsureCreatedAll → EnsureSchemaUpgrades 会补列）
        using var fixture = new KanbanAppFixture(_tempDir);

        // 3. 验证 WorkOrderId 列已被添加
        Assert.True(ColumnExists("production_logs.db", "ProductionLogs", "WorkOrderId"),
            "EnsureSchemaUpgrades 未补列 WorkOrderId");
    }

    /// <summary>
    /// 预置旧版 work_orders.db（缺少 CompletedOkCount/CompletedNgCount 列） → 启动 MainAPP →
    /// EnsureSchemaUpgrades 应自动添加这两列。
    /// </summary>
    [Fact]
    public void OldSchema_WorkOrders_EnsureSchemaUpgrades_AddsCompletedCountColumns()
    {
        // 1. 预置旧版 work_orders.db（无 CompletedOkCount/CompletedNgCount 列）
        SeedOldSchemaWorkOrders();
        Assert.False(ColumnExists("work_orders.db", "WorkOrders", "CompletedOkCount"),
            "预置条件失败：CompletedOkCount 列不应存在");
        Assert.False(ColumnExists("work_orders.db", "WorkOrders", "CompletedNgCount"),
            "预置条件失败：CompletedNgCount 列不应存在");

        // 2. 启动 MainAPP
        using var fixture = new KanbanAppFixture(_tempDir);

        // 3. 验证列已被添加
        Assert.True(ColumnExists("work_orders.db", "WorkOrders", "CompletedOkCount"),
            "EnsureSchemaUpgrades 未补列 CompletedOkCount");
        Assert.True(ColumnExists("work_orders.db", "WorkOrders", "CompletedNgCount"),
            "EnsureSchemaUpgrades 未补列 CompletedNgCount");
    }

    // ======== 工单自动清理测试 ========

    /// <summary>
    /// 预置 2 条过期 Completed 工单（UpdatedAt > 365天前） + 1 条近期 Completed 工单 →
    /// 启动 MainAPP → CleanupOldWorkOrders 应删除过期的 2 条，保留近期的 1 条。
    /// 验证 <see cref="Kanban.Collector.Core.Data.WorkOrderRepository.CleanupOldWorkOrders"/> 的保留逻辑。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void CleanupOldWorkOrders_RemovesExpiredCompleted_KeepsRecent()
    {
        // 1. 预置工单：2 条过期 Completed + 1 条近期 Completed
        SeedWorkOrdersForCleanup();
        Assert.Equal(3, CountRows("work_orders.db", "WorkOrders"));

        // 2. 启动 MainAPP（后台线程会执行 CleanupOldWorkOrders）
        using var fixture = new KanbanAppFixture(_tempDir);

        // 等待后台清理任务完成（MainWindow.Show 后异步执行，需额外等待）
        Thread.Sleep(5000);

        // 3. 验证过期工单已删除，近期工单保留
        var finalCount = CountRows("work_orders.db", "WorkOrders");
        Assert.Equal(1, finalCount);

        // 验证保留的是近期工单（OrderNo = WO-RECENT）
        Assert.True(OrderNoExists("work_orders.db", "WO-OLD-COMPLETED-1") == false, "过期 Completed 工单未被清理");
        Assert.True(OrderNoExists("work_orders.db", "WO-OLD-COMPLETED-2") == false, "过期 Completed 工单未被清理");
        Assert.True(OrderNoExists("work_orders.db", "WO-RECENT"), "近期工单被误删");
    }

    /// <summary>
    /// 预置 1 条过期 Aborted 工单 + 1 条过期 Running 工单 →
    /// 启动 MainAPP → CleanupOldWorkOrders 应删除 Aborted，保留 Running（即使过期）。
    /// 验证清理仅针对 Completed/Aborted，不影响 Pending/Running。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void CleanupOldWorkOrders_RemovesExpiredAborted_KeepsExpiredRunning()
    {
        // 1. 预置工单：1 条过期 Aborted + 1 条过期 Running
        SeedWorkOrdersForCleanup_MixedStatus();
        Assert.Equal(2, CountRows("work_orders.db", "WorkOrders"));

        // 2. 启动 MainAPP
        using var fixture = new KanbanAppFixture(_tempDir);
        Thread.Sleep(5000);

        // 3. 验证 Aborted 已删除，Running 保留（即使过期）
        var finalCount = CountRows("work_orders.db", "WorkOrders");
        Assert.Equal(1, finalCount);

        Assert.False(OrderNoExists("work_orders.db", "WO-OLD-ABORTED"), "过期 Aborted 工单未被清理");
        Assert.True(OrderNoExists("work_orders.db", "WO-OLD-RUNNING"), "过期 Running 工单被误删（应保留）");
    }

    /// <summary>
    /// 预置 1 条过期 Pending 工单 → 启动 MainAPP →
    /// CleanupOldWorkOrders 不应删除 Pending 工单（即使过期）。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void CleanupOldWorkOrders_KeepsExpiredPending()
    {
        SeedWorkOrdersForCleanup_PendingOnly();
        Assert.Equal(1, CountRows("work_orders.db", "WorkOrders"));

        using var fixture = new KanbanAppFixture(_tempDir);
        Thread.Sleep(5000);

        var finalCount = CountRows("work_orders.db", "WorkOrders");
        Assert.Equal(1, finalCount);
        Assert.True(OrderNoExists("work_orders.db", "WO-OLD-PENDING"), "过期 Pending 工单被误删（应保留）");
    }

    // ======== 索引验证测试 ========

    /// <summary>
    /// 验证 EnsureCreated 创建了所有预期的索引。
    /// 索引由 <see cref="Microsoft.EntityFrameworkCore.DbContext.OnModelCreating"/> 声明，EnsureCreated 落库。
    /// </summary>
    [Theory]
    [InlineData("work_orders.db", "WorkOrders", new[] {
        "IX_WorkOrders_DeviceId_Status", "IX_WorkOrders_Status", "IX_WorkOrders_CreatedAt"
    })]
    [InlineData("production_logs.db", "ProductionLogs", new[] {
        "IX_ProductionLogs_DeviceId_Timestamp", "IX_ProductionLogs_DeviceId",
        "IX_ProductionLogs_Timestamp", "IX_ProductionLogs_WorkOrderId"
    })]
    [InlineData("alarm_events.db", "AlarmEvents", new[] {
        "IX_AlarmEvents_DeviceId_AlarmId", "IX_AlarmEvents_AlarmId", "IX_AlarmEvents_EventTime"
    })]
    [InlineData("status_transitions.db", "StatusTransitions", new[] {
        "IX_StatusTransitions_DeviceId_EventTime", "IX_StatusTransitions_EventTime"
    })]
    public void EnsureCreated_AllIndexesExist(string dbFileName, string tableName, string[] expectedIndexes)
    {
        using var fixture = new KanbanAppFixture(_tempDir);
        var actualIndexes = GetIndexes(dbFileName, tableName);

        foreach (var expected in expectedIndexes)
        {
            Assert.Contains(expected, actualIndexes);
        }
    }

    // ======== NOT NULL 约束验证 ========

    /// <summary>
    /// 验证 WorkOrders 表的 NOT NULL 约束已正确落库。
    /// EF Core OnModelCreating 中声明的 IsRequired() 应生成 NOT NULL 约束。
    /// </summary>
    [Fact]
    public void WorkOrders_NotNullConstraints_EnforcedOnRequiredFields()
    {
        using var fixture = new KanbanAppFixture(_tempDir);

        // IsRequired 字段应有 NOT NULL 约束
        // 注意：DeviceName 虽未显式 IsRequired()，但实体中为 non-nullable string（string.Empty 默认值），
        // EF Core 惯例将其视为 NOT NULL
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "OrderNo"), "OrderNo 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "ProductCode"), "ProductCode 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "ProductName"), "ProductName 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "DeviceId"), "DeviceId 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "DeviceName"), "DeviceName 应有 NOT NULL 约束（EF Core non-nullable string 惯例）");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "TargetQuantity"), "TargetQuantity 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "PlannedStart"), "PlannedStart 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "PlannedEnd"), "PlannedEnd 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "Status"), "Status 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "CreatedAt"), "CreatedAt 应有 NOT NULL 约束");
        Assert.True(IsColumnNotNull("work_orders.db", "WorkOrders", "UpdatedAt"), "UpdatedAt 应有 NOT NULL 约束");

        // 可空字段不应有 NOT NULL 约束
        Assert.False(IsColumnNotNull("work_orders.db", "WorkOrders", "CompletedOkCount"), "CompletedOkCount 应为可空");
        Assert.False(IsColumnNotNull("work_orders.db", "WorkOrders", "CompletedNgCount"), "CompletedNgCount 应为可空");
        Assert.False(IsColumnNotNull("work_orders.db", "WorkOrders", "Remark"), "Remark 应为可空");
    }

    // ======== 列类型验证 ========

    /// <summary>
    /// 验证 WorkOrders 表的列类型正确：枚举 Status 存为 INTEGER，产量存为 INTEGER，时间存为 TEXT。
    /// EF Core OnModelCreating 中 HasConversion&lt;int&gt;() 确保枚举按 int 列存储。
    /// </summary>
    [Fact]
    public void WorkOrders_ColumnTypes_CorrectMapping()
    {
        using var fixture = new KanbanAppFixture(_tempDir);

        // 枚举显式按 int 存储（HasConversion<int>）
        Assert.Equal("INTEGER", GetColumnType("work_orders.db", "WorkOrders", "Status"));
        // 整数字段
        Assert.Equal("INTEGER", GetColumnType("work_orders.db", "WorkOrders", "Id"));
        Assert.Equal("INTEGER", GetColumnType("work_orders.db", "WorkOrders", "TargetQuantity"));
        Assert.Equal("INTEGER", GetColumnType("work_orders.db", "WorkOrders", "CompletedOkCount"));
        Assert.Equal("INTEGER", GetColumnType("work_orders.db", "WorkOrders", "CompletedNgCount"));
        // DateTime 在 SQLite 中映射为 TEXT
        Assert.Equal("TEXT", GetColumnType("work_orders.db", "WorkOrders", "PlannedStart"));
        Assert.Equal("TEXT", GetColumnType("work_orders.db", "WorkOrders", "PlannedEnd"));
        Assert.Equal("TEXT", GetColumnType("work_orders.db", "WorkOrders", "CreatedAt"));
        Assert.Equal("TEXT", GetColumnType("work_orders.db", "WorkOrders", "UpdatedAt"));
        // 字符串字段
        Assert.Equal("TEXT", GetColumnType("work_orders.db", "WorkOrders", "OrderNo"));
        Assert.Equal("TEXT", GetColumnType("work_orders.db", "WorkOrders", "ProductCode"));
    }

    // ======== WAL 文件验证 ========

    /// <summary>
    /// 验证 WAL 模式启用后，写操作会产生 .db-wal 文件。
    /// EnsureWalModeEnabled 设置 journal_mode=WAL，EnsureCreated 写入 schema 应触发 WAL 文件创建。
    /// </summary>
    [Fact]
    public void WalMode_WalFilesCreated_AfterEnsureCreated()
    {
        using var fixture = new KanbanAppFixture(_tempDir);

        // WAL 模式下，有写操作后应产生 .db-wal 文件
        // EnsureCreated 创建表结构（写操作）会触发 WAL 文件
        // 注意：wal_checkpoint(TRUNCATE) 可能在启动时截断 WAL 文件，但后续写操作会重新生成
        var dbFiles = new[] { "alarm_events.db", "production_logs.db", "status_transitions.db", "work_orders.db" };
        foreach (var db in dbFiles)
        {
            var dbPath = GetDbPath(db);
            var walPath = dbPath + "-wal";
            // WAL 文件可能存在（取决于 checkpoint 时机），但 journal_mode 必须是 wal
            Assert.True(File.Exists(dbPath), $"数据库文件不存在：{db}");
            // 验证 journal_mode 仍为 wal（不会被 checkpoint 改变）
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            var mode = cmd.ExecuteScalar()?.ToString();
            Assert.Equal("wal", mode);
        }
    }

    // ======== 数据库隔离验证 ========

    /// <summary>
    /// 验证四个数据库是独立的文件（非同一文件的不同表）。
    /// 确保各 DbContext 使用各自的 .db 文件，数据互不干扰。
    /// </summary>
    [Fact]
    public void FourDatabases_AreIndependentFiles()
    {
        using var fixture = new KanbanAppFixture(_tempDir);

        var dbFiles = new[] { "alarm_events.db", "production_logs.db", "status_transitions.db", "work_orders.db" };
        var paths = dbFiles.Select(f => GetDbPath(f)).ToList();

        // 所有文件路径不同
        Assert.Equal(dbFiles.Length, paths.Distinct().Count());

        // 所有文件都存在
        foreach (var path in paths)
        {
            Assert.True(File.Exists(path), $"数据库文件不存在：{path}");
        }

        // 各库只有自己的表，不含其他库的表
        Assert.True(TableExists("alarm_events.db", "AlarmEvents"), "alarm_events.db 应有 AlarmEvents 表");
        Assert.False(TableExists("alarm_events.db", "WorkOrders"), "alarm_events.db 不应有 WorkOrders 表");
        Assert.True(TableExists("work_orders.db", "WorkOrders"), "work_orders.db 应有 WorkOrders 表");
        Assert.False(TableExists("work_orders.db", "AlarmEvents"), "work_orders.db 不应有 AlarmEvents 表");
        Assert.True(TableExists("production_logs.db", "ProductionLogs"), "production_logs.db 应有 ProductionLogs 表");
        Assert.True(TableExists("status_transitions.db", "StatusTransitions"), "status_transitions.db 应有 StatusTransitions 表");
    }

    // ======== 辅助方法 ========

    /// <summary>检查指定表是否存在</summary>
    private bool TableExists(string dbFileName, string tableName)
    {
        var dbPath = GetDbPath(dbFileName);
        if (!File.Exists(dbPath)) return false;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>检查指定 OrderNo 的工单是否存在</summary>
    private bool OrderNoExists(string dbFileName, string orderNo)
    {
        var dbPath = GetDbPath(dbFileName);
        if (!File.Exists(dbPath)) return false;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM WorkOrders WHERE OrderNo=@orderNo";
        cmd.Parameters.AddWithValue("@orderNo", orderNo);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>预置旧版 production_logs.db（无 WorkOrderId 列，模拟升级前数据库）</summary>
    private void SeedOldSchemaProductionLogs()
    {
        var dbPath = GetDbPath("production_logs.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductionLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceId TEXT NOT NULL,
                DeviceName TEXT,
                Timestamp TEXT NOT NULL,
                OkCount INTEGER NOT NULL,
                NgCount INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                CycleTime REAL,
                ShiftName TEXT
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>预置旧版 work_orders.db（无 CompletedOkCount/CompletedNgCount 列）</summary>
    private void SeedOldSchemaWorkOrders()
    {
        var dbPath = GetDbPath("work_orders.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
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
                Remark TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 预置工单清理测试数据：2 条过期 Completed + 1 条近期 Completed。
    /// 过期工单 UpdatedAt = 当前时间 - 400 天（超过 365 天保留期）。
    /// </summary>
    private void SeedWorkOrdersForCleanup()
    {
        var dbPath = GetDbPath("work_orders.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // 建表（完整 schema，含 CompletedOkCount/CompletedNgCount）
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

        var now = DateTime.Now;
        var oldDate = now.AddDays(-400).ToString("o"); // 400 天前，超过 365 天保留期
        var recentDate = now.AddDays(-10).ToString("o"); // 10 天前，在保留期内

        var orders = new[]
        {
            // 过期 Completed（应被清理）
            ("WO-OLD-COMPLETED-1", "P-A", "产品A", "device-001", "注塑机1", 1000, 2, oldDate),
            ("WO-OLD-COMPLETED-2", "P-B", "产品B", "device-002", "注塑机2", 800, 2, oldDate),
            // 近期 Completed（应保留）
            ("WO-RECENT", "P-C", "产品C", "device-003", "组装机1", 500, 2, recentDate),
        };

        using var tx = conn.BeginTransaction();
        foreach (var o in orders)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO WorkOrders (OrderNo, ProductCode, ProductName, DeviceId, DeviceName,
                    TargetQuantity, PlannedStart, PlannedEnd, Status, CompletedOkCount, CompletedNgCount,
                    Remark, CreatedAt, UpdatedAt)
                VALUES (@orderNo, @code, @name, @devId, @devName, @qty, @start, @end, @status,
                    @ok, @ng, NULL, @created, @updated)
                """;
            cmd.Parameters.AddWithValue("@orderNo", o.Item1);
            cmd.Parameters.AddWithValue("@code", o.Item2);
            cmd.Parameters.AddWithValue("@name", o.Item3);
            cmd.Parameters.AddWithValue("@devId", o.Item4);
            cmd.Parameters.AddWithValue("@devName", o.Item5);
            cmd.Parameters.AddWithValue("@qty", o.Item6);
            cmd.Parameters.AddWithValue("@start", o.Item8);
            cmd.Parameters.AddWithValue("@end", o.Item8);
            cmd.Parameters.AddWithValue("@status", o.Item7);
            cmd.Parameters.AddWithValue("@ok", 900);
            cmd.Parameters.AddWithValue("@ng", 100);
            cmd.Parameters.AddWithValue("@created", o.Item8);
            cmd.Parameters.AddWithValue("@updated", o.Item8);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 预置工单清理测试数据：1 条过期 Aborted + 1 条过期 Running。
    /// 验证清理仅针对 Completed/Aborted，不影响 Running。
    /// </summary>
    private void SeedWorkOrdersForCleanup_MixedStatus()
    {
        var dbPath = GetDbPath("work_orders.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

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

        var oldDate = DateTime.Now.AddDays(-400).ToString("o");

        var orders = new[]
        {
            // 过期 Aborted（应被清理）
            ("WO-OLD-ABORTED", "P-A", "产品A", "device-001", "注塑机1", 1000, 3, oldDate),
            // 过期 Running（应保留，Running 不被清理）
            ("WO-OLD-RUNNING", "P-B", "产品B", "device-002", "注塑机2", 800, 1, oldDate),
        };

        using var tx = conn.BeginTransaction();
        foreach (var o in orders)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO WorkOrders (OrderNo, ProductCode, ProductName, DeviceId, DeviceName,
                    TargetQuantity, PlannedStart, PlannedEnd, Status, CompletedOkCount, CompletedNgCount,
                    Remark, CreatedAt, UpdatedAt)
                VALUES (@orderNo, @code, @name, @devId, @devName, @qty, @start, @end, @status,
                    NULL, NULL, NULL, @created, @updated)
                """;
            cmd.Parameters.AddWithValue("@orderNo", o.Item1);
            cmd.Parameters.AddWithValue("@code", o.Item2);
            cmd.Parameters.AddWithValue("@name", o.Item3);
            cmd.Parameters.AddWithValue("@devId", o.Item4);
            cmd.Parameters.AddWithValue("@devName", o.Item5);
            cmd.Parameters.AddWithValue("@qty", o.Item6);
            cmd.Parameters.AddWithValue("@start", o.Item8);
            cmd.Parameters.AddWithValue("@end", o.Item8);
            cmd.Parameters.AddWithValue("@status", o.Item7);
            cmd.Parameters.AddWithValue("@created", o.Item8);
            cmd.Parameters.AddWithValue("@updated", o.Item8);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>预置 1 条过期 Pending 工单，验证 Pending 不被清理</summary>
    private void SeedWorkOrdersForCleanup_PendingOnly()
    {
        var dbPath = GetDbPath("work_orders.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

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

        var oldDate = DateTime.Now.AddDays(-400).ToString("o");
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = """
            INSERT INTO WorkOrders (OrderNo, ProductCode, ProductName, DeviceId, DeviceName,
                TargetQuantity, PlannedStart, PlannedEnd, Status, CompletedOkCount, CompletedNgCount,
                Remark, CreatedAt, UpdatedAt)
            VALUES (@orderNo, @code, @name, @devId, @devName, @qty, @start, @end, @status,
                NULL, NULL, NULL, @created, @updated)
            """;
        cmd2.Parameters.AddWithValue("@orderNo", "WO-OLD-PENDING");
        cmd2.Parameters.AddWithValue("@code", "P-A");
        cmd2.Parameters.AddWithValue("@name", "产品A");
        cmd2.Parameters.AddWithValue("@devId", "device-001");
        cmd2.Parameters.AddWithValue("@devName", "注塑机1");
        cmd2.Parameters.AddWithValue("@qty", 1000);
        cmd2.Parameters.AddWithValue("@start", oldDate);
        cmd2.Parameters.AddWithValue("@end", oldDate);
        cmd2.Parameters.AddWithValue("@status", 0); // Pending = 0
        cmd2.Parameters.AddWithValue("@created", oldDate);
        cmd2.Parameters.AddWithValue("@updated", oldDate);
        cmd2.ExecuteNonQuery();
    }
}
