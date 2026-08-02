using System.IO;
using MainAPP.Data;
using MainAPP.Entities;
using MainAPP.Models;
using MainAPP.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// WAL checkpoint 验证测试：确保 DatabaseProvider 正确配置 WAL 模式并支持定期 checkpoint。
///
/// 覆盖范围：
/// - EnsureWalModeEnabled：设置 journal_mode=WAL 并执行 TRUNCATE checkpoint
/// - CheckpointAll：运行期 PASSIVE checkpoint 不抛异常
/// - WAL 模式持久化：EnsureWalModeEnabled 后查询 journal_mode 返回 'wal'
/// - 写入后 checkpoint：写入数据后执行 checkpoint，数据仍可读
/// - WAL 文件控制：写入大量数据后 WAL 文件不会无限增长
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","Database")]
public class WalCheckpointTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _dbProvider;

    public WalCheckpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WalTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _dbProvider = new DatabaseProvider(_appSettings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ──────────── EnsureWalModeEnabled ────────────

    [Fact]
    public void EnsureWalModeEnabled_DoesNotThrow()
    {
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();
        // 不抛异常即通过
    }

    [Fact]
    public void EnsureWalModeEnabled_SetsJournalModeToWal()
    {
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();

        // 验证 production_logs.db 的 journal_mode
        var dbPath = _appSettings.GetFilePath("production_logs.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = cmd.ExecuteScalar()?.ToString();
        // SQLite 可能返回 'wal'（小写）
        Assert.Equal("wal", mode?.ToLowerInvariant());
    }

    [Fact]
    public void EnsureWalModeEnabled_SetsAllFourDatabases()
    {
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();

        var dbFiles = new[] { "production_logs.db", "alarm_events.db", "status_transitions.db", "work_orders.db" };
        foreach (var dbFile in dbFiles)
        {
            var dbPath = _appSettings.GetFilePath(dbFile);
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = cmd.ExecuteScalar()?.ToString();
            Assert.Equal("wal", mode?.ToLowerInvariant());
        }
    }

    // ──────────── CheckpointAll ────────────

    [Fact]
    public void CheckpointAll_DoesNotThrow()
    {
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();
        _dbProvider.CheckpointAll();
    }

    [Fact]
    public void CheckpointAll_AfterWrites_PreservesData()
    {
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();

        // 写入数据
        using (var ctx = _dbProvider.CreateWorkOrderContext())
        {
            ctx.WorkOrders.Add(new WorkOrder { OrderNo = "WO-1", ProductName = "产品A", DeviceId = "d1" });
            ctx.WorkOrders.Add(new WorkOrder { OrderNo = "WO-2", ProductName = "产品B", DeviceId = "d1" });
            ctx.SaveChanges();
        }

        // 执行 checkpoint
        _dbProvider.CheckpointAll();

        // 验证数据仍可读
        using (var ctx = _dbProvider.CreateWorkOrderContext())
        {
            var count = ctx.WorkOrders.Count();
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public void CheckpointAll_MultipleCalls_DoesNotThrow()
    {
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();

        // 多次调用 checkpoint 不应抛异常（模拟运行期每 5 分钟调用一次）
        _dbProvider.CheckpointAll();
        _dbProvider.CheckpointAll();
        _dbProvider.CheckpointAll();
    }

    // ──────────── WAL 文件控制 ────────────

    [Fact]
    public void EnsureWalModeEnabled_AfterCrashLikeState_CanStillWrite()
    {
        // 模拟崩溃后重启：EnsureCreated + EnsureWalMode + 写入
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();

        // 即使之前有未 checkpoint 的 WAL 帧，EnsureWalModeEnabled 的 TRUNCATE 应清理
        using (var ctx = _dbProvider.CreateWorkOrderContext())
        {
            ctx.WorkOrders.Add(new WorkOrder { OrderNo = "WO-AFTER-CRASH", DeviceId = "d1" });
            ctx.SaveChanges();  // 不抛异常即说明 WAL 可写
        }

        using (var ctx = _dbProvider.CreateWorkOrderContext())
        {
            Assert.Single(ctx.WorkOrders);
        }
    }

    [Fact]
    public void WalFile_DoesNotGrowUnbounded()
    {
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();

        // 写入大量数据
        using (var ctx = _dbProvider.CreateWorkOrderContext())
        {
            for (var i = 0; i < 100; i++)
            {
                ctx.WorkOrders.Add(new WorkOrder
                {
                    OrderNo = $"WO-BULK-{i:D4}",
                    ProductName = $"产品{i}",
                    DeviceId = "d1",
                });
            }
            ctx.SaveChanges();
        }

        // 执行 checkpoint
        _dbProvider.CheckpointAll();

        // WAL 文件应被截断（或不存在）
        var walPath = _appSettings.GetFilePath("work_orders.db-wal");
        var walExists = File.Exists(walPath);
        if (walExists)
        {
            var walInfo = new FileInfo(walPath);
            // WAL 文件应很小（checkpoint 后截断）
            Assert.True(walInfo.Length < 1024 * 1024, $"WAL 文件过大: {walInfo.Length} 字节");
        }
    }

    // ──────────── 并发读写不阻塞 ────────────

    [Fact]
    public async Task WalMode_ConcurrentReadDuringWrite_DoesNotThrow()
    {
        _dbProvider.EnsureCreatedAll();
        _dbProvider.EnsureWalModeEnabled();

        // 先写入初始数据
        using (var ctx = _dbProvider.CreateWorkOrderContext())
        {
            ctx.WorkOrders.Add(new WorkOrder { OrderNo = "WO-INIT", DeviceId = "d1" });
            ctx.SaveChanges();
        }

        // 并发：一个线程写入，另一个线程读取
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var writeTask = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var ctx = _dbProvider.CreateWorkOrderContext();
                for (var i = 0; i < 20; i++)
                {
                    ctx.WorkOrders.Add(new WorkOrder { OrderNo = $"WO-CONC-{i}", DeviceId = "d1" });
                    ctx.SaveChanges();
                }
            }
            catch (Exception ex) { exceptions.Enqueue(ex); }
        });

        var readTask = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    using var ctx = _dbProvider.CreateWorkOrderContext();
                    // WAL 模式下读不阻塞写
                    var _ = ctx.WorkOrders.AsNoTracking().Count();
                }
            }
            catch (Exception ex) { exceptions.Enqueue(ex); }
        });

        await System.Threading.Tasks.Task.WhenAll(writeTask, readTask);

        Assert.Empty(exceptions);
    }
}
