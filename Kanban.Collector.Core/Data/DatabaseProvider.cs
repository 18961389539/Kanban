using Kanban.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Serilog;
using System.Data.Common;
using System.IO;

namespace Kanban.Core.Data;

/// <summary>
/// 数据库提供者：为各历史领域分别创建独立数据库的 DbContext。
/// 每次 Create 都会读取最新的 AppSettings.ConfigDirectory，保证配置变化后路径立即生效。
/// </summary>
public class DatabaseProvider(AppSettings appSettings)
{
    private const string EfProductVersion = "10.0.10";
    private const string ProductionLogInitialMigration = "20260731070824_InitialSchema";
    private const string AlarmEventInitialMigration = "20260731070831_InitialSchema";
    private const string StatusTransitionInitialMigration = "20260731070839_InitialSchema";
    private const string WorkOrderInitialMigration = "20260731070847_InitialSchema";

    private readonly AppSettings _appSettings = appSettings;
    private static readonly string[] HistoryDatabaseFiles =
    [
        "production_logs.db",
        "alarm_events.db",
        "status_transitions.db",
        "work_orders.db",
        "defect_history.db",
    ];

    public AppSettings AppSettings => _appSettings;

    public ProductionLogDbContext CreateProductionLogContext() => new(_appSettings);
    public AlarmEventDbContext CreateAlarmEventContext() => new(_appSettings);
    public StatusTransitionDbContext CreateStatusTransitionContext() => new(_appSettings);
    public WorkOrderDbContext CreateWorkOrderContext() => new(_appSettings);
    public DefectHistoryDbContext CreateDefectHistoryContext() => new(_appSettings);

    /// <summary>
    /// 启动期数据库 schema 初始化。
    /// 新数据库和已接入 EF 的数据库使用标准 Migrate；
    /// 旧版本通过 EnsureCreated 创建、但没有 EF 历史表的数据库先建立基线，避免重复建表。
    /// </summary>
    public void EnsureCreatedAll()
    {
        MigrateContext(
            CreateProductionLogContext(),
            "ProductionLogs",
            ProductionLogInitialMigration,
            ApplyProductionLogLegacyPatch);
        MigrateContext(
            CreateAlarmEventContext(),
            "AlarmEvents",
            AlarmEventInitialMigration,
            static (_, _) => { });
        MigrateContext(
            CreateStatusTransitionContext(),
            "StatusTransitions",
            StatusTransitionInitialMigration,
            static (_, _) => { });
        MigrateContext(
            CreateWorkOrderContext(),
            "WorkOrders",
            WorkOrderInitialMigration,
            ApplyWorkOrderLegacyPatch);
        // 缺陷历史为新增独立数据库，使用 EnsureCreated 兼容首次部署和已有配置目录。
        using (var defectContext = CreateDefectHistoryContext())
            defectContext.Database.EnsureCreated();
    }

    private void MigrateContext<TContext>(
        TContext context,
        string userTableName,
        string initialMigrationId,
        Action<SqliteConnection, SqliteTransaction> applyLegacyPatch)
        where TContext : DbContext
    {
        using (context)
        {
            var databasePath = context.Database.GetDbConnection().DataSource;
            if (!File.Exists(databasePath))
            {
                context.Database.Migrate();
                return;
            }

            using var connection = new SqliteConnection($"Data Source={databasePath};Cache=Shared");
            connection.Open();
            var hasMigrationHistory = TableExists(connection, "__EFMigrationsHistory");
            if (!hasMigrationHistory)
            {
                if (TableExists(connection, userTableName))
                {
                    BackupDatabase(databasePath);
                    using var transaction = connection.BeginTransaction();
                    applyLegacyPatch(connection, transaction);
                    if (!IsSchemaCompatible(context, connection, transaction, userTableName))
                    {
                        throw new InvalidOperationException(
                            $"数据库 {databasePath} 的旧版表 {userTableName} 结构不完整，无法建立 EF Core 迁移基线。" +
                            "请先备份数据库并执行数据迁移或恢复兼容版本。");
                    }
                    CreateMigrationHistory(connection, transaction);
                    InsertMigrationBaseline(connection, transaction, initialMigrationId);
                    transaction.Commit();
                    Log.Information("旧版数据库 {Database} 已建立 EF Core 迁移基线 {MigrationId}", databasePath, initialMigrationId);
                    return;
                }
            }

            connection.Close();
            if (context.Database.GetPendingMigrations().Any())
                BackupDatabase(databasePath);
            context.Database.Migrate();
        }
    }

    private static void BackupDatabase(string databasePath)
    {
        var backupPath = $"{databasePath}.pre-migration-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak";
        using var source = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Cache=Shared");
        using var destination = new SqliteConnection($"Data Source={backupPath}");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        Log.Information("数据库迁移前备份完成 {Database} -> {Backup}", databasePath, backupPath);
    }

    private static void ApplyProductionLogLegacyPatch(SqliteConnection connection, SqliteTransaction transaction)
    {
        EnsureColumn(connection, transaction, "ProductionLogs", "WorkOrderId", "INTEGER");
        EnsureIndex(connection, transaction, "IX_ProductionLogs_WorkOrderId", "ProductionLogs", "WorkOrderId");
    }

    private static void ApplyWorkOrderLegacyPatch(SqliteConnection connection, SqliteTransaction transaction)
    {
        EnsureColumn(connection, transaction, "WorkOrders", "CompletedOkCount", "INTEGER");
        EnsureColumn(connection, transaction, "WorkOrders", "CompletedNgCount", "INTEGER");
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() != null;
    }

    private static void CreateMigrationHistory(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    private static void InsertMigrationBaseline(SqliteConnection connection, SqliteTransaction transaction, string migrationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ($id, $version)";
        command.Parameters.AddWithValue("$id", migrationId);
        command.Parameters.AddWithValue("$version", EfProductVersion);
        command.ExecuteNonQuery();
    }

    private static bool IsSchemaCompatible(
        DbContext context,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        var entityType = context.Model.GetEntityTypes().Single();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema: null);
        var expectedColumns = entityType.GetProperties()
            .Select(property => property.GetColumnName(storeObject))
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            actualColumns.Add(reader.GetString(1));

        if (!expectedColumns.IsSubsetOf(actualColumns))
            return false;

        var actualIndexes = new List<string[]>();
        using (var indexList = connection.CreateCommand())
        {
            indexList.Transaction = transaction;
            indexList.CommandText = $"PRAGMA index_list(\"{tableName}\")";
            using var indexReader = indexList.ExecuteReader();
            while (indexReader.Read())
            {
                var indexName = indexReader.GetString(1);
                using var indexInfo = connection.CreateCommand();
                indexInfo.Transaction = transaction;
                indexInfo.CommandText = $"PRAGMA index_info(\"{indexName.Replace("\"", "\"\"")}\")";
                using var infoReader = indexInfo.ExecuteReader();
                var columns = new List<(int Order, string Name)>();
                while (infoReader.Read())
                    columns.Add((infoReader.GetInt32(0), infoReader.GetString(2)));
                actualIndexes.Add(columns.OrderBy(column => column.Order).Select(column => column.Name).ToArray());
            }
        }

        var expectedIndexes = entityType.GetIndexes()
            .Select(index => index.Properties
                .Select(property => property.GetColumnName(storeObject))
                .Where(column => !string.IsNullOrWhiteSpace(column))
                .ToArray())
            .ToList();

        return expectedIndexes.All(expected => actualIndexes.Any(actual => actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)));
    }

    private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction, string tableName, string columnName, string columnType)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                return;
        }

        using var add = connection.CreateCommand();
        add.Transaction = transaction;
        add.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType}";
        add.ExecuteNonQuery();
    }

    private static void EnsureIndex(SqliteConnection connection, SqliteTransaction transaction, string indexName, string tableName, string columnName)
    {
        using var index = connection.CreateCommand();
        index.Transaction = transaction;
        index.CommandText = $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{tableName}\" (\"{columnName}\")";
        index.ExecuteNonQuery();
    }

    /// <summary>
    /// 对所有 SQLite 数据库文件执行启动期初始化：
    /// - journal_mode=WAL：数据库级别的持久化设置（写入 db 头部），只需执行一次。
    ///   PLC 轮询线程写入 + UI 线程查询并发时，WAL 允许读不阻塞写、写不阻塞读，
    ///   默认 journal_mode=delete 下写事务会独占数据库，并发读会抛 SQLITE_BUSY。
    /// - temp_store=MEMORY：将临时表和索引存于内存而非磁盘，避免高频 IO。
    ///   虽然 SqlitePragmaInterceptor 每次连接也会设置，但启动期设置一次可让数据库文件头部
    ///   记录此偏好（部分 SQLite 构建会持久化此设置）。
    /// - busy_timeout=5000：等待锁最多 5 秒，避免 checkpoint 时因残留连接立即失败。
    /// - wal_checkpoint(TRUNCATE)：强制将 WAL 帧写回主库并截断 WAL 文件。
    ///   上次进程若被强制终止（如 Task Manager / 崩溃），WAL 可能停留在最大尺寸（~4MB），
    ///   新进程启动后无法写入（WAL 已满且 checkpoint 无法推进），导致所有数据库写入静默失败。
    ///   启动期强制 checkpoint+TRUNCATE 可从此状态恢复。
    /// 应在 EnsureCreated 之后调用。
    /// </summary>
    public void EnsureWalModeEnabled()
    {
        _appSettings.EnsureDirectory();
        foreach (var dbFile in HistoryDatabaseFiles)
        {
            var path = _appSettings.GetFilePath(dbFile);
            // Cache=Shared 与 DbContext OnConfiguring 保持一致，确保启动期初始化连接
            // 与运行时连接共享相同的缓存策略，避免重复加载页
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // 多条 PRAGMA 用分号分隔，一次执行减少 IO 往返
            cmd.CommandText =
                "PRAGMA journal_mode=WAL;" +
                "PRAGMA temp_store=MEMORY;" +
                "PRAGMA busy_timeout=5000;" +
                "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 运行期定期 WAL checkpoint：将四个数据库的 WAL 数据合并回主 .db 文件并截断 WAL。
    /// SQLite 默认 auto-checkpoint 在 1000 页（约 4MB）时触发 PASSIVE checkpoint，
    /// 但 Cache=Shared + 连接池模式下可能无法正常完成，导致 WAL 满后写入静默停止。
    /// HistoryService 每 5 分钟调用此方法，防止长时间运行后数据丢失。
    /// 使用 PASSIVE 模式：不阻塞读写操作，只合并已完成的 WAL 帧。
    /// </summary>
    public void CheckpointAll()
    {
        _appSettings.EnsureDirectory();
        foreach (var dbFile in HistoryDatabaseFiles)
        {
            var path = _appSettings.GetFilePath(dbFile);
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // PASSIVE：不阻塞并发读写，只合并已完成的 WAL 帧；TRUNCATE 在 PASSIVE 后截断 WAL 文件
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>
/// SQLite PRAGMA 拦截器：每次连接打开时设置以下 PRAGMA：
/// - synchronous=NORMAL：WAL 模式下足够安全且性能更好（默认 FULL 每次提交都 fsync）
/// - busy_timeout=5000：防止并发写时立即抛 SQLITE_BUSY，改为等待最多 5 秒
/// - temp_store=MEMORY：临时表和索引存于内存，避免磁盘 IO（默认 FILE）
/// - mmap_size=268435456：启用 256MB 内存映射 IO，提升读取性能
/// 这些 PRAGMA 是连接级别（非持久化），每次新连接都需要设置，故用拦截器自动处理。
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public static readonly SqlitePragmaInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "PRAGMA synchronous=NORMAL;" +
            "PRAGMA busy_timeout=5000;" +
            "PRAGMA temp_store=MEMORY;" +
            "PRAGMA mmap_size=268435456;";
        cmd.ExecuteNonQuery();
    }

    // 复用同步实现：PRAGMA 必须在连接打开后立即执行，不能省略
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => Task.Run(() => ConnectionOpened(connection, eventData), cancellationToken);
}

/// <summary>
/// 各 DbContext 的公共基类：统一处理 OnConfiguring（EnsureDirectory + SQLite + PRAGMA 拦截器）。
/// 子类只需声明对应的数据库文件名和 DbSet，消除 OnConfiguring 重复代码。
/// 各 DbContext 保持独立（各自对应不同的 .db 文件），仅共享配置逻辑。
/// </summary>
public abstract class KanbanDbContextBase : DbContext
{
    private readonly AppSettings _appSettings;
    private readonly string _dbFileName;

    /// <param name="appSettings">应用配置，提供配置目录</param>
    /// <param name="dbFileName">数据库文件名（如 "production_logs.db"）</param>
    protected KanbanDbContextBase(AppSettings appSettings, string dbFileName)
    {
        _appSettings = appSettings;
        _dbFileName = dbFileName;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        _appSettings.EnsureDirectory();
        var dbPath = _appSettings.GetFilePath(_dbFileName);
        // Cache=Shared 启用共享缓存模式：同一进程内多个连接共享页缓存，
        // 减少重复 IO 读取，并允许 WAL 模式下读连接复用缓存页。
        optionsBuilder.UseSqlite($"Data Source={dbPath};Cache=Shared")
            .AddInterceptors(SqlitePragmaInterceptor.Instance);
    }
}
