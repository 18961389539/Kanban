using System;
using System.IO;
using Kanban.Core.Data;
using Kanban.Core.Services;
using MainAPP.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// DatabaseProvider 启动期 PRAGMA 与 SqlitePragmaInterceptor 连接期 PRAGMA 的集成测试：
/// - EnsureWalModeEnabled：对所有 .db 文件设置 journal_mode=WAL + temp_store=MEMORY（启动期一次性）
/// - SqlitePragmaInterceptor：每次连接打开时设置 synchronous=NORMAL + busy_timeout=5000 + temp_store=MEMORY + mmap_size=268435456
/// EnsureWalModeEnabled 验证用原始 SqliteConnection（避免 Interceptor 干扰），Interceptor 验证通过 DbContext（由 EF Core 触发）。
/// 共用一个临时目录隔离测试，避免污染真实 %APPDATA%/Kanban。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","Database")]
public class DatabasePragmaTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _db;

    public DatabasePragmaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanDbPragma_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_appSettings);
    }

    public void Dispose()
    {
        try
        {
            // 先释放 DbContext（关闭连接），再删除目录，避免文件被占用
            using (_db.CreateProductionLogContext()) { }
            using (_db.CreateAlarmEventContext()) { }
            using (_db.CreateStatusTransitionContext()) { }
            Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    /// <summary>用原始 SqliteConnection 查询 PRAGMA 字符串值（不触发 Interceptor）。</summary>
    private static string QueryPragmaString(string dbPath, string pragma)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        return cmd.ExecuteScalar()!.ToString()!;
    }

    /// <summary>用原始 SqliteConnection 查询 PRAGMA 整数值（不触发 Interceptor）。</summary>
    private static long QueryPragmaLong(string dbPath, string pragma)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>通过 DbContext 查询 PRAGMA 整数值（会触发 SqlitePragmaInterceptor）。</summary>
    private static long QueryPragmaLongViaCtx(DbContext ctx, string pragma)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            ctx.Database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---------- EnsureWalModeEnabled 验证 ----------

    [Fact]
    public void EnsureWalModeEnabled_SetsJournalModeToWal()
    {
        // 先建表（EnsureCreated 创建 schema），再设置 WAL（与 DatabaseProvider 注释要求一致）
        using (var ctx = _db.CreateProductionLogContext())
            ctx.Database.EnsureCreated();
        _db.EnsureWalModeEnabled();

        // 用原始 SqliteConnection 查询，避免 Interceptor 干扰
        var dbPath = _appSettings.GetFilePath("production_logs.db");
        var mode = QueryPragmaString(dbPath, "PRAGMA journal_mode;");
        Assert.Equal("wal", mode.ToLower());
    }

    [Fact]
    public void EnsureWalModeEnabled_SetsTempStoreToMemory()
    {
        using (var ctx = _db.CreateProductionLogContext())
            ctx.Database.EnsureCreated();
        _db.EnsureWalModeEnabled();

        var dbPath = _appSettings.GetFilePath("production_logs.db");
        // PRAGMA temp_store 返回整数：0=DEFAULT, 1=FILE, 2=MEMORY
        var tempStore = QueryPragmaLong(dbPath, "PRAGMA temp_store;");
        Assert.Equal(2L, tempStore); // MEMORY=2
    }

    [Fact]
    public void EnsureWalModeEnabled_CreatesWalFiles()
    {
        using (var ctx = _db.CreateProductionLogContext())
            ctx.Database.EnsureCreated();
        _db.EnsureWalModeEnabled();

        // WAL 模式启用后应存在 .db-wal sidecar 文件
        var walPath = _appSettings.GetFilePath("production_logs.db-wal");
        Assert.True(File.Exists(walPath), $"WAL sidecar 文件应存在: {walPath}");
    }

    [Fact]
    public void EnsureWalModeEnabled_Idempotent_CalledMultipleTimesNoError()
    {
        using (var ctx = _db.CreateProductionLogContext())
            ctx.Database.EnsureCreated();

        // 多次调用不应抛异常（WAL 模式已持久化，重复设置是幂等的）
        var ex = Record.Exception(() =>
        {
            _db.EnsureWalModeEnabled();
            _db.EnsureWalModeEnabled();
            _db.EnsureWalModeEnabled();
        });
        Assert.Null(ex);
    }

    // ---------- SqlitePragmaInterceptor 验证（通过 DbContext 查询）----------

    [Fact]
    public void DbContext_Query_AfterInterceptor_SynchronousIsNormal()
    {
        // 创建 DbContext 并触发 EnsureCreated 会打开连接，触发 SqlitePragmaInterceptor.ConnectionOpened
        using var ctx = _db.CreateProductionLogContext();
        ctx.Database.EnsureCreated();

        // PRAGMA synchronous 返回整数：0=OFF, 1=NORMAL, 2=FULL, 3=EXTRA
        var syncMode = QueryPragmaLongViaCtx(ctx, "PRAGMA synchronous;");
        Assert.Equal(1L, syncMode); // NORMAL=1
    }

    [Fact]
    public void DbContext_Query_AfterInterceptor_BusyTimeoutIs5000()
    {
        using var ctx = _db.CreateProductionLogContext();
        ctx.Database.EnsureCreated();

        // PRAGMA busy_timeout 返回毫秒
        var timeout = QueryPragmaLongViaCtx(ctx, "PRAGMA busy_timeout;");
        Assert.Equal(5000L, timeout);
    }

    [Fact]
    public void DbContext_Query_AfterInterceptor_TempStoreIsMemory()
    {
        using var ctx = _db.CreateProductionLogContext();
        ctx.Database.EnsureCreated();

        // PRAGMA temp_store 返回整数：0=DEFAULT, 1=FILE, 2=MEMORY
        var tempStore = QueryPragmaLongViaCtx(ctx, "PRAGMA temp_store;");
        Assert.Equal(2L, tempStore); // MEMORY=2
    }

    [Fact]
    public void DbContext_Query_AfterInterceptor_MmapSizeIs268435456()
    {
        using var ctx = _db.CreateProductionLogContext();
        ctx.Database.EnsureCreated();

        // PRAGMA mmap_size 返回字节
        var mmapSize = QueryPragmaLongViaCtx(ctx, "PRAGMA mmap_size;");
        Assert.Equal(268435456L, mmapSize);
    }
}
