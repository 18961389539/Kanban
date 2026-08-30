using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Data.Sqlite;
using System.IO;
using Xunit;

namespace MainAPP.Tests.Integration;

[CollectionDefinition("Database environment", DisableParallelization = true)]
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","Database")]
public sealed class DatabaseEnvironmentCollection;

[Collection("Database environment")]
public sealed class DatabaseMigrationCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kanban_migration_" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings;
    private readonly string? _previousDataRoot;

    public DatabaseMigrationCompatibilityTests()
    {
        Directory.CreateDirectory(_directory);
        _previousDataRoot = Environment.GetEnvironmentVariable("KANBAN_DATA_DIR");
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _directory);
        _settings = new AppSettings();
    }

    [Fact]
    public void EnsureCreatedAll_LegacyProductionDatabase_CreatesBaselineAndKeepsData()
    {
        _settings.EnsureDirectory();
        var path = _settings.GetFilePath("production_logs.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ProductionLogs (Id INTEGER PRIMARY KEY, DeviceId TEXT NOT NULL, DeviceName TEXT NOT NULL, ShiftName TEXT NOT NULL, OkProduction INTEGER NOT NULL, NgProduction INTEGER NOT NULL, StatusWord INTEGER NOT NULL, Timestamp TEXT NOT NULL); CREATE INDEX IX_ProductionLogs_DeviceId ON ProductionLogs (DeviceId); CREATE INDEX IX_ProductionLogs_DeviceId_Timestamp ON ProductionLogs (DeviceId, Timestamp); CREATE INDEX IX_ProductionLogs_Timestamp ON ProductionLogs (Timestamp); INSERT INTO ProductionLogs (Id, DeviceId, DeviceName, ShiftName, OkProduction, NgProduction, StatusWord, Timestamp) VALUES (7, 'D1', '设备1', '白班', 9, 1, 0, '2026-07-31 08:00:00');";
            command.ExecuteNonQuery();
        }

        new DatabaseProvider(_settings).EnsureCreatedAll();

        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM ProductionLogs WHERE Id = 7";
        Assert.Equal(1L, query.ExecuteScalar());

        query.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = '20260731070824_InitialSchema'";
        Assert.Equal(1L, query.ExecuteScalar());
        Assert.Single(Directory.GetFiles(
            Path.GetDirectoryName(path)!,
            Path.GetFileName(path) + ".pre-migration-*.bak"));
    }

    [Fact]
    public void EnsureCreatedAll_LegacyDataSourceSnapshotDatabase_AddsIdentityAndTimingColumns()
    {
        _settings.EnsureDirectory();
        var path = _settings.GetFilePath("datasource_snapshots.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE DataSourceSnapshots (Id INTEGER PRIMARY KEY AUTOINCREMENT, DeviceId TEXT NOT NULL, DeviceName TEXT NOT NULL, SourceId TEXT NOT NULL, SourceName TEXT NOT NULL, SourceType TEXT NOT NULL, Unit TEXT NOT NULL, Value INTEGER NOT NULL, IsValid INTEGER NOT NULL, ShiftName TEXT NOT NULL, Timestamp TEXT NOT NULL); CREATE INDEX IX_DataSourceSnapshots_DeviceId ON DataSourceSnapshots (DeviceId); CREATE INDEX IX_DataSourceSnapshots_DeviceId_Timestamp ON DataSourceSnapshots (DeviceId, Timestamp); CREATE INDEX IX_DataSourceSnapshots_SourceId_Timestamp ON DataSourceSnapshots (SourceId, Timestamp); CREATE INDEX IX_DataSourceSnapshots_Timestamp ON DataSourceSnapshots (Timestamp); INSERT INTO DataSourceSnapshots (DeviceId, DeviceName, SourceId, SourceName, SourceType, Unit, Value, IsValid, ShiftName, Timestamp) VALUES ('D1', '设备1', 'legacy-value-1', '温度', 'PLC', '℃', 42, 1, '白班', '2026-08-17 08:00:00');";
            command.ExecuteNonQuery();
        }

        new DatabaseProvider(_settings).EnsureCreatedAll();

        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT ValueId, Timestamp, PersistedAt FROM DataSourceSnapshots WHERE Id = 1";
        using (var row = query.ExecuteReader())
        {
            Assert.True(row.Read());
            Assert.Equal(string.Empty, row.GetString(0));
            Assert.Equal(row.GetString(1), row.GetString(2));
        }

        query.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = '20260818110000_AddValueIdentityAndTiming'";
        Assert.Equal(1L, query.ExecuteScalar());
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_DataSourceSnapshots_DeviceId_SourceId_ValueId_Timestamp'";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact]
    public void EnsureCreatedAll_NewDatabases_CreatesEfMigrationHistoryForAllDatabases()
    {
        _settings.EnsureDirectory();
        new DatabaseProvider(_settings).EnsureCreatedAll();

        foreach (var databaseName in new[]
        {
            "production_logs.db",
            "alarm_events.db",
            "status_transitions.db",
        })
        {
            var path = _settings.GetFilePath(databaseName);
            Assert.True(File.Exists(path), $"数据库文件未创建: {databaseName}");
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
            Assert.Equal(1L, query.ExecuteScalar());
        }

        // work_orders.db 现含两次迁移（InitialSchema + AddStatusTimestamps），新库应完整应用
        var woPath = _settings.GetFilePath("work_orders.db");
        Assert.True(File.Exists(woPath), "数据库文件未创建: work_orders.db");
        using (var connection = new SqliteConnection($"Data Source={woPath}"))
        {
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
            Assert.Equal(2L, query.ExecuteScalar());
        }
    }

    /// <summary>老版 work_orders.db（只有初始列）升级后应补上状态时间戳列并保留数据（#7 时间线数据基础）。</summary>
    [Fact]
    public void EnsureCreatedAll_LegacyWorkOrderDatabase_AddsStatusTimestampColumns()
    {
        _settings.EnsureDirectory();
        var path = _settings.GetFilePath("work_orders.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE WorkOrders (Id INTEGER PRIMARY KEY AUTOINCREMENT, OrderNo TEXT NOT NULL, ProductCode TEXT NOT NULL, ProductName TEXT NOT NULL, DeviceId TEXT NOT NULL, DeviceName TEXT NOT NULL, TargetQuantity INTEGER NOT NULL, PlannedStart TEXT NOT NULL, PlannedEnd TEXT NOT NULL, Status INTEGER NOT NULL, CompletedOkCount INTEGER, CompletedNgCount INTEGER, Remark TEXT, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL); CREATE INDEX IX_WorkOrders_CreatedAt ON WorkOrders (CreatedAt); CREATE INDEX IX_WorkOrders_Status ON WorkOrders (Status); CREATE INDEX IX_WorkOrders_DeviceId_Status ON WorkOrders (DeviceId, Status); INSERT INTO WorkOrders (OrderNo, ProductCode, ProductName, DeviceId, DeviceName, TargetQuantity, PlannedStart, PlannedEnd, Status, CreatedAt, UpdatedAt) VALUES ('LEGACY-1', 'P1', '老工单', 'D1', '设备1', 100, '2026-08-01 08:00:00', '2026-08-01 18:00:00', 0, '2026-08-01 07:00:00', '2026-08-01 07:00:00');";
            command.ExecuteNonQuery();
        }

        new DatabaseProvider(_settings).EnsureCreatedAll();

        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var query = verify.CreateCommand();
        // 老数据保留
        query.CommandText = "SELECT COUNT(*) FROM WorkOrders WHERE OrderNo = 'LEGACY-1'";
        Assert.Equal(1L, query.ExecuteScalar());
        // 时间戳列补齐（老数据为 null）
        query.CommandText = "SELECT StartedAt, CompletedAt FROM WorkOrders WHERE OrderNo = 'LEGACY-1'";
        using (var reader = query.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
        }
        // 迁移基线已建立（老库走 legacy patch 补列，不一定记录新迁移 ID——由 ApplyWorkOrderLegacyPatch 幂等补列）
        query.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = '20260731070847_InitialSchema'";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact]
    public void EnsureCreatedAll_IncompleteLegacyDatabase_RefusesToCreateBaseline()
    {
        _settings.EnsureDirectory();
        var path = _settings.GetFilePath("alarm_events.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE AlarmEvents (Id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => new DatabaseProvider(_settings).EnsureCreatedAll());

        Assert.Contains("结构不完整", exception.Message);
        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _previousDataRoot);
        try { Directory.Delete(_directory, true); } catch { }
    }
}
