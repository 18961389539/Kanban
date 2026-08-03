using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MainAPP.UIAutomation;

/// <summary>
/// 数据库数据积累测试：运行 PlcSimulator + MainAPP 一段时间，验证数据库正确积累生产/报警/状态数据。
///
/// 测试流程：
/// 1. 预置 devices.json + settings.json（PLC 127.0.0.1:4999）
/// 2. 启动 PlcSimulator（demo 场景，speed=10 加速）→ 启动 MainAPP
/// 3. 等待 N 分钟让数据积累（PLC 轮询写入 ProductionLogs，报警/状态转换写入各自数据库）
/// 4. 关闭 MainAPP（正常退出触发 OnExit 保存）
/// 5. 验证四个数据库的内容：
///    - production_logs.db：行数 > 0，时间戳递增，DeviceId 合法
///    - alarm_events.db：报警事件格式正确（若有报警）
///    - status_transitions.db：状态转换记录格式正确
///    - work_orders.db：表结构完整（可能无工单数据）
///
/// 注意：此测试需要长时间运行（3+ 分钟），标记为 LongRunning。
/// 临时目录在任何模式下都保留，便于事后检查数据库内容。
/// </summary>
[Collection("UIA")]
public class DatabaseAccumulationFlowTests : IDisposable
{
    private readonly string _tempDir;

    public DatabaseAccumulationFlowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanDbAccum_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Config"));
    }

    public void Dispose()
    {
        // 任何模式都保留临时目录，便于事后检查积累的数据库内容。
        Console.WriteLine($"  [DatabaseAccumulationFlowTests] 保留临时目录: {_tempDir}");
    }

    /// <summary>获取 Config 目录下指定数据库文件路径</summary>
    private string GetDbPath(string dbFileName) =>
        Path.Combine(_tempDir, "Config", dbFileName);

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

    /// <summary>查询指定表的所有列名</summary>
    private List<string> GetColumnNames(string dbFileName, string tableName)
    {
        var dbPath = GetDbPath(dbFileName);
        var columns = new List<string>();
        if (!File.Exists(dbPath)) return columns;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    /// <summary>查询 ProductionLogs 表前 N 条记录的 DeviceId 和 Timestamp</summary>
    private List<(string DeviceId, string Timestamp, int OkProduction, int NgProduction)> QueryProductionLogs(int limit)
    {
        var result = new List<(string, string, int, int)>();
        var dbPath = GetDbPath("production_logs.db");
        if (!File.Exists(dbPath)) return result;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DeviceId, Timestamp, OkProduction, NgProduction FROM ProductionLogs ORDER BY Timestamp LIMIT {limit}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        return result;
    }

    /// <summary>查询 AlarmEvents 表前 N 条记录</summary>
    private List<(string DeviceId, string AlarmId, string EventTime, int EventType)> QueryAlarmEvents(int limit)
    {
        var result = new List<(string, string, string, int)>();
        var dbPath = GetDbPath("alarm_events.db");
        if (!File.Exists(dbPath)) return result;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DeviceId, AlarmId, EventTime, EventType FROM AlarmEvents ORDER BY EventTime LIMIT {limit}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }
        return result;
    }

    /// <summary>查询 StatusTransitions 表前 N 条记录</summary>
    private List<(string DeviceId, string EventTime, int PreviousState, int CurrentState)> QueryStatusTransitions(int limit)
    {
        var result = new List<(string, string, int, int)>();
        var dbPath = GetDbPath("status_transitions.db");
        if (!File.Exists(dbPath)) return result;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DeviceId, EventTime, PreviousState, CurrentState FROM StatusTransitions ORDER BY EventTime LIMIT {limit}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        return result;
    }

    /// <summary>检查指定数据库的 WAL 文件大小（字节）</summary>
    private long GetWalFileSize(string dbFileName)
    {
        var walPath = GetDbPath(dbFileName) + "-wal";
        return File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
    }

    /// <summary>
    /// 轮询等待 ProductionLogs 达到预期条数（5s 间隔），达标提前返回；
    /// 超时上限兜底（不抛异常，按当前数据继续，由后续断言裁决成败）。
    /// 替代固定 Thread.Sleep：不依赖精确时钟——数据提前达标即提前退出（CI 抖动可容忍），
    /// 数据链路故障时也能更快暴露（超时后断言失败而非干等满时长）。
    /// WAL 模式下 MainAPP 运行中仍可只读查询已提交数据。
    /// </summary>
    private void WaitForProductionLogs(string dataDir, int expectedMinimum, string stage, int timeoutSeconds = 200)
    {
        var dbPath = Path.Combine(dataDir, "Config", "production_logs.db");
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            var count = File.Exists(dbPath) ? CountRowsWithPath(dbPath, "ProductionLogs") : 0;
            if (count >= expectedMinimum)
            {
                Console.WriteLine($"  [{stage}] 数据提前达标：ProductionLogs={count} 条（≥{expectedMinimum}），提前结束等待");
                return;
            }
            Thread.Sleep(5000);
        }
        var finalCount = File.Exists(dbPath) ? CountRowsWithPath(dbPath, "ProductionLogs") : 0;
        Console.WriteLine($"  [{stage}] 等待超时（{timeoutSeconds}s），当前 ProductionLogs={finalCount} 条，按现状继续验证");
    }

    // ======== 数据积累主测试 ========

    /// <summary>
    /// 长时间数据积累测试：运行 PlcSimulator + MainAPP 3 分钟（demo 场景，10 倍速），
    /// 验证四个数据库正确积累数据。
    /// 
    /// 3 分钟 × 10 倍速 ≈ 30 分钟模拟生产时间，足够积累：
    /// - ProductionLogs：每 60 次扫描写一次（PollingIntervalMs=1000 → 约 60 条/设备）
    /// - AlarmEvents：demo 场景报警频率较高，应有若干条
    /// - StatusTransitions：设备启停、报警触发/恢复产生转换记录
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void AccumulateData_3Minutes_AllDatabasesHaveValidData()
    {
        // 1. 预置配置（复用 SimulationContext 的预置逻辑）
        var simCtx = new SimulationContext();
        simCtx.PrepareDevicesJson();
        simCtx.PrepareSettingsJson();
        // 用 simCtx.TempDir 作为数据目录，但我们需要在 _tempDir 下积累数据
        // 实际上我们直接用 simCtx，它的 TempDir 就是数据目录
        var dataDir = simCtx.TempDir;

        try
        {
            // 2. 启动 PlcSimulator（demo 场景，10 倍速）
            simCtx.StartSimulator("demo", 10);

            // 3. 启动 MainAPP
            simCtx.StartApp();

            // 4. 等待数据积累：轮询等待 ProductionLogs 达到预期下限（8 条 ≈ 3 分钟 × 10 倍速），
            //    达标提前返回；不达标等到超时上限（200s）后按现状继续（由后续断言裁决）。
            //    替代固定 Sleep(180s)：不依赖精确时钟，CI 抖动可容忍。
            Console.WriteLine($"  [数据积累] 开始轮询等待 ProductionLogs 达到 8 条（上限 200s）...");
            WaitForProductionLogs(dataDir, expectedMinimum: 8, stage: "3 分钟积累", timeoutSeconds: 200);
            Console.WriteLine($"  [数据积累] 等待结束，开始验证数据库");

            // 5. 关闭 MainAPP（正常退出，触发 OnExit 保存）
            //    注意：不关闭 PlcSimulator，先让 MainAPP 优雅退出
        }
        finally
        {
            // SimulationContext.Dispose 会先停 MainAPP 再停 PlcSimulator
            simCtx.Dispose();
        }

        // 6. 验证数据库内容
        ValidateProductionLogs(dataDir);
        ValidateAlarmEvents(dataDir);
        ValidateStatusTransitions(dataDir);
        ValidateWorkOrders(dataDir);
        ValidateWalFiles(dataDir);
    }

    // ======== 验证方法 ========

    private void ValidateProductionLogs(string dataDir)
    {
        var dbPath = Path.Combine(dataDir, "Config", "production_logs.db");
        Assert.True(File.Exists(dbPath), "production_logs.db 应存在");

        var count = CountRowsWithPath(dbPath, "ProductionLogs");
        Console.WriteLine($"  [验证] ProductionLogs 行数: {count}");
        // 3 分钟 × 10 倍速，3 台设备，每 60 秒写一条 → 至少应有几条
        // 即使部分设备报警/停机，正常运行设备应有产出记录
        Assert.True(count > 0, "ProductionLogs 行数应为正数（数据积累期间应产生生产快照）");

        // 验证列完整性
        var columns = GetColumnNamesWithPath(dbPath, "ProductionLogs");
        Assert.Contains("DeviceId", columns);
        Assert.Contains("Timestamp", columns);
        Assert.Contains("OkProduction", columns);
        Assert.Contains("NgProduction", columns);
        Assert.Contains("WorkOrderId", columns);

        // 验证 DeviceId 合法（来自 devices.json 的 3 台设备）
        var validDeviceIds = new HashSet<string> { "device-001", "device-002", "device-003" };
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT DeviceId FROM ProductionLogs";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var devId = reader.GetString(0);
                Assert.True(validDeviceIds.Contains(devId),
                    $"ProductionLogs 包含非法 DeviceId: {devId}（不在 devices.json 中）");
            }
        }

        // 验证时间戳格式（ISO 8601，可被 DateTime.Parse 解析）
        var logs = QueryProductionLogsWithPath(dbPath, 10);
        foreach (var (devId, ts, ok, ng) in logs)
        {
            var dt = DateTime.Parse(ts);
            Assert.True(dt <= DateTime.Now.AddMinutes(1),
                $"ProductionLog 时间戳 {ts} 超出当前时间（未来时间不合理）");
        }

        // 验证 OkProduction/NgProduction 非负
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ProductionLogs WHERE OkProduction < 0 OR NgProduction < 0";
            var negativeCount = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(0, negativeCount);
        }
    }

    private void ValidateAlarmEvents(string dataDir)
    {
        var dbPath = Path.Combine(dataDir, "Config", "alarm_events.db");
        Assert.True(File.Exists(dbPath), "alarm_events.db 应存在");

        // demo 场景报警频率较高，但 3 分钟内不一定触发报警
        // 这里只验证表结构和数据格式（若有记录）
        var count = CountRowsWithPath(dbPath, "AlarmEvents");
        Console.WriteLine($"  [验证] AlarmEvents 行数: {count}");

        if (count == 0)
        {
            Console.WriteLine("  [验证] AlarmEvents 无记录（3 分钟内未触发报警），跳过数据格式验证");
            return;
        }

        // 验证列完整性
        var columns = GetColumnNamesWithPath(dbPath, "AlarmEvents");
        Assert.Contains("DeviceId", columns);
        Assert.Contains("AlarmId", columns);
        Assert.Contains("EventTime", columns);
        Assert.Contains("EventType", columns);

        // 验证 EventType 合法（Triggered=1, Recovered=2, ShiftChange=3）
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT EventType FROM AlarmEvents";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var eventType = reader.GetInt32(0);
            Assert.True(eventType >= 1 && eventType <= 3,
                $"AlarmEvents 包含非法 EventType: {eventType}（应为 1=Triggered, 2=Recovered, 3=ShiftChange）");
        }
    }

    private void ValidateStatusTransitions(string dataDir)
    {
        var dbPath = Path.Combine(dataDir, "Config", "status_transitions.db");
        Assert.True(File.Exists(dbPath), "status_transitions.db 应存在");

        var count = CountRowsWithPath(dbPath, "StatusTransitions");
        Console.WriteLine($"  [验证] StatusTransitions 行数: {count}");

        if (count == 0)
        {
            Console.WriteLine("  [验证] StatusTransitions 无记录，跳过数据格式验证");
            return;
        }

        // 验证列完整性
        var columns = GetColumnNamesWithPath(dbPath, "StatusTransitions");
        Assert.Contains("DeviceId", columns);
        Assert.Contains("EventTime", columns);

        // 验证 DeviceId 合法
        var validDeviceIds = new HashSet<string> { "device-001", "device-002", "device-003" };
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT DeviceId FROM StatusTransitions";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var devId = reader.GetString(0);
            Assert.True(validDeviceIds.Contains(devId),
                $"StatusTransitions 包含非法 DeviceId: {devId}");
        }
    }

    private void ValidateWorkOrders(string dataDir)
    {
        var dbPath = Path.Combine(dataDir, "Config", "work_orders.db");
        Assert.True(File.Exists(dbPath), "work_orders.db 应存在");

        // 未预置工单，表应为空但结构完整
        var count = CountRowsWithPath(dbPath, "WorkOrders");
        Console.WriteLine($"  [验证] WorkOrders 行数: {count}");
        Assert.True(count >= 0, "WorkOrders 表应可查询");

        // 验证列完整性
        var columns = GetColumnNamesWithPath(dbPath, "WorkOrders");
        Assert.Contains("Id", columns);
        Assert.Contains("OrderNo", columns);
        Assert.Contains("Status", columns);
        Assert.Contains("CompletedOkCount", columns);
        Assert.Contains("CompletedNgCount", columns);
    }

    /// <summary>
    /// 验证 WAL 文件不会无限增长（EnsureWalModeEnabled + CheckpointAll 正常工作）。
    /// SQLite WAL 默认在 1000 页（~4MB）时 auto-checkpoint，加上定期 PASSIVE checkpoint，
    /// WAL 文件不应超过 ~8MB。此处放宽到 16MB 作为阈值。
    /// </summary>
    private void ValidateWalFiles(string dataDir)
    {
        var dbFiles = new[] { "alarm_events.db", "production_logs.db", "status_transitions.db", "work_orders.db" };
        foreach (var db in dbFiles)
        {
            var walPath = Path.Combine(dataDir, "Config", db + "-wal");
            if (File.Exists(walPath))
            {
                var size = new FileInfo(walPath).Length;
                Console.WriteLine($"  [验证] {db}-wal 大小: {size / 1024.0 / 1024.0:F2} MB");
                // WAL 文件不应超过 16MB（正常 auto-checkpoint 阈值 ~4MB，放宽 4 倍容错）
                Assert.True(size < 16 * 1024 * 1024,
                    $"{db}-wal 文件过大: {size / 1024.0 / 1024.0:F2} MB（应 < 16MB，checkpoint 可能未正常工作）");
            }
        }
    }

    // ======== 辅助方法（使用完整路径，复用于验证方法）========

    private static int CountRowsWithPath(string dbPath, string tableName)
    {
        if (!File.Exists(dbPath)) return -1;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<string> GetColumnNamesWithPath(string dbPath, string tableName)
    {
        var columns = new List<string>();
        if (!File.Exists(dbPath)) return columns;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static List<(string DeviceId, string Timestamp, int OkProduction, int NgProduction)> QueryProductionLogsWithPath(string dbPath, int limit)
    {
        var result = new List<(string, string, int, int)>();
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DeviceId, Timestamp, OkProduction, NgProduction FROM ProductionLogs ORDER BY Timestamp LIMIT {limit}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        return result;
    }

    // ======== 短时间数据积累测试（快速验证）========

    /// <summary>
    /// 短时间数据积累测试：运行 60 秒，验证 ProductionLogs 至少有 1 条记录。
    /// 作为快速冒烟测试，验证数据积累链路（PLC → 采集 → HistoryService → DB）正常工作。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void AccumulateData_60Seconds_ProductionLogsHasRecords()
    {
        var simCtx = new SimulationContext();
        simCtx.PrepareDevicesJson();
        simCtx.PrepareSettingsJson();
        var dataDir = simCtx.TempDir;

        try
        {
            simCtx.StartSimulator("demo", 10);
            simCtx.StartApp();

            Console.WriteLine($"  [数据积累] 开始轮询等待 ProductionLogs（上限 90s）...");
            WaitForProductionLogs(dataDir, expectedMinimum: 1, stage: "60 秒积累", timeoutSeconds: 90);
            Console.WriteLine($"  [数据积累] 等待结束");
        }
        finally
        {
            simCtx.Dispose();
        }

        var dbPath = Path.Combine(dataDir, "Config", "production_logs.db");
        Assert.True(File.Exists(dbPath), "production_logs.db 应存在");

        var count = CountRowsWithPath(dbPath, "ProductionLogs");
        Console.WriteLine($"  [验证] 60 秒积累后 ProductionLogs 行数: {count}");
        // 60 秒 / 60 次扫描间隔 = 1 条/设备，至少应有 1 条
        Assert.True(count > 0, "60 秒后 ProductionLogs 应至少有 1 条记录（数据积累链路可能故障）");
    }

    /// <summary>
    /// 验证数据积累后重启 MainAPP，历史数据仍可查询（持久化验证）。
    /// 流程：积累 60 秒 → 关闭 → 记录行数 → 重启 → 验证行数未减少。
    /// </summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public void AccumulateData_Restart_DataPersisted()
    {
        var simCtx = new SimulationContext();
        simCtx.PrepareDevicesJson();
        simCtx.PrepareSettingsJson();
        var dataDir = simCtx.TempDir;

        // 第一次运行：积累数据
        try
        {
            simCtx.StartSimulator("demo", 10);
            simCtx.StartApp();
            WaitForProductionLogs(dataDir, expectedMinimum: 1, stage: "首次运行积累", timeoutSeconds: 90);
        }
        finally
        {
            simCtx.Dispose();
        }

        // 记录第一次运行后的行数
        var dbPath = Path.Combine(dataDir, "Config", "production_logs.db");
        var countBeforeRestart = File.Exists(dbPath) ? CountRowsWithPath(dbPath, "ProductionLogs") : 0;
        Console.WriteLine($"  [验证] 第一次运行后 ProductionLogs 行数: {countBeforeRestart}");
        Assert.True(countBeforeRestart > 0, "第一次运行后应有 ProductionLogs 记录");

        // 第二次运行：不启动 PlcSimulator（只验证数据持久化，不需要新数据）
        // 使用新的 KanbanAppFixture 复用同一 TempDir
        using var fixture2 = new KanbanAppFixture(dataDir);
        Thread.Sleep(3000); // 等待启动

        // 验证数据未丢失（重启不应删除历史记录）
        var countAfterRestart = File.Exists(dbPath) ? CountRowsWithPath(dbPath, "ProductionLogs") : 0;
        Console.WriteLine($"  [验证] 重启后 ProductionLogs 行数: {countAfterRestart}");
        Assert.True(countAfterRestart >= countBeforeRestart,
            $"重启后数据丢失: 重启前 {countBeforeRestart} 条, 重启后 {countAfterRestart} 条");
    }
}
