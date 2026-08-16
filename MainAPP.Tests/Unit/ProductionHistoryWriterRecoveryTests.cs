using System.IO;
using System.Text.Json;
using Kanban.Collector.Services;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 生产历史恢复文件可靠性测试（P1）：
/// - 恢复文件回放：JSONL → 落库，损坏行转存 .bad 且不阻塞其余行；
/// - EventId 幂等：重复回放（回放成功后文件未删/崩溃重试）不重复落库；
/// - 超大恢复文件停止回放（上限保护）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class ProductionHistoryWriterRecoveryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DatabaseProvider _db;
    private readonly string _recoveryPath;

    public ProductionHistoryWriterRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RecoveryTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_settings);
        _db.EnsureCreatedAll();
        _db.EnsureWalModeEnabled();
        _recoveryPath = Path.Combine(_tempDir, "production_logs.recovery.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ProductionLog MakeLog(string deviceId, Guid? eventId = null) => new()
    {
        DeviceId = deviceId,
        DeviceName = "注塑机-1",
        ShiftName = "白班",
        OkProduction = 10,
        NgProduction = 1,
        StatusWord = 1,
        Timestamp = DateTime.Now,
        EventId = eventId ?? Guid.NewGuid(),
    };

    private void WriteRecoveryLines(params string[] lines)
        => File.WriteAllLines(_recoveryPath, lines);

    private int CountRows()
    {
        using var context = _db.CreateProductionLogContext();
        return context.ProductionLogs.Count();
    }

    [Fact]
    public async Task Replay_InsertsAllValidLines_AndDeletesFile()
    {
        WriteRecoveryLines(
            JsonSerializer.Serialize(MakeLog("dev-1")),
            JsonSerializer.Serialize(MakeLog("dev-2")));

        var writer = new ProductionHistoryWriter(_db, _settings, NullLogger<ProductionHistoryWriter>.Instance);
        try
        {
            await writer.ReplayRecoveryForTestAsync();
            Assert.Equal(2, CountRows());
            Assert.False(File.Exists(_recoveryPath));
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Replay_BadLinesMovedToBadFile_ValidLinesStillInserted()
    {
        WriteRecoveryLines(
            JsonSerializer.Serialize(MakeLog("dev-1")),
            "这不是合法 JSON {{{",
            JsonSerializer.Serialize(MakeLog("dev-2")));

        var writer = new ProductionHistoryWriter(_db, _settings, NullLogger<ProductionHistoryWriter>.Instance);
        try
        {
            await writer.ReplayRecoveryForTestAsync();
            Assert.Equal(2, CountRows()); // 坏行不阻塞其余行
            Assert.True(File.Exists(_recoveryPath + ".bad"));
            Assert.Equal(1, File.ReadAllLines(_recoveryPath + ".bad").Length);
            Assert.False(File.Exists(_recoveryPath));
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Replay_Twice_IdempotentByEventId()
    {
        // 模拟"回放成功但文件未删/崩溃后重试"：同一恢复文件回放两次，
        // EventId 唯一索引 + 查询去重保证不重复落库
        var log1 = MakeLog("dev-1");
        var log2 = MakeLog("dev-2");
        WriteRecoveryLines(
            JsonSerializer.Serialize(log1),
            JsonSerializer.Serialize(log2));

        var writer = new ProductionHistoryWriter(_db, _settings, NullLogger<ProductionHistoryWriter>.Instance);
        try
        {
            await writer.ReplayRecoveryForTestAsync();
            await writer.ReplayRecoveryForTestAsync(); // 文件已删 → 第二次无操作（幂等）
            Assert.Equal(2, CountRows());
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Replay_AfterPartialDbWrite_SkipsAlreadyInsertedEventIds()
    {
        // 模拟：批写落库成功但恢复文件尚未删除（崩溃）→ 再次回放时按 EventId 去重
        var log = MakeLog("dev-1");
        WriteRecoveryLines(JsonSerializer.Serialize(log));
        // 先手工把同 EventId 数据插入库（模拟已落库）
        using (var context = _db.CreateProductionLogContext())
        {
            context.ProductionLogs.Add(log);
            context.SaveChanges();
        }

        var writer = new ProductionHistoryWriter(_db, _settings, NullLogger<ProductionHistoryWriter>.Instance);
        try
        {
            await writer.ReplayRecoveryForTestAsync();
            Assert.Equal(1, CountRows()); // 去重后不重复落库
            Assert.False(File.Exists(_recoveryPath));
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }

    // ───────────── 审查修复 2026-08-13：追加大小上限 + 回放失败退避 ─────────────

    [Fact]
    public async Task PersistRecoveryLogs_OverCap_StopsAppending()
    {
        // 追加前大小上限检查：DB 持续不可写时恢复文件不再无限增长（生产上限 200MB，测试覆盖小上限）
        var writer = new ProductionHistoryWriter(_db, _settings, NullLogger<ProductionHistoryWriter>.Instance);
        writer.MaxRecoveryFileBytesOverride = 400;
        try
        {
            // 首条落盘（低于上限）
            writer.PersistRecoveryLogsForTest(MakeLog("dev-1"));
            Assert.True(File.Exists(_recoveryPath));
            var firstLength = new FileInfo(_recoveryPath).Length;
            Assert.True(firstLength <= 400, $"首条写入即超上限：{firstLength}");

            // 继续追加：超过上限后停止（文件长度不再增长）
            for (var i = 0; i < 10; i++)
                writer.PersistRecoveryLogsForTest(MakeLog($"dev-{i + 2}"));
            var finalLength = new FileInfo(_recoveryPath).Length;
            Assert.True(finalLength <= 400, $"追加超过上限：{finalLength}");
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Replay_Failed_BacksOffForRetryDelay()
    {
        // 回放失败退避：DB 不可写（独占锁）→ 回放失败记录时刻；30s 退避窗口内再次触发应直接跳过
        WriteRecoveryLines(JsonSerializer.Serialize(MakeLog("dev-1")));

        var dbPath = Path.Combine(_tempDir, "production_logs.db");
        var writer = new ProductionHistoryWriter(_db, _settings, NullLogger<ProductionHistoryWriter>.Instance);
        try
        {
            // 仅清空本库连接池（EnsureCreatedAll/WAL 初始化留下的池化连接持有文件句柄，独占锁会被其阻塞）。
            // 不用 ClearAllPools：全局清池会与并行运行的 WAL/PRAGMA 测试互踩（-wal 文件随连接关闭被删）。
            using (var poolConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Cache=Shared"))
            {
                poolConn.Open();
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(poolConn);
            }
            using (new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // 第一次回放：打开数据库失败 → 记录失败时刻，文件保留
                await writer.ReplayRecoveryForTestAsync();
                Assert.True(File.Exists(_recoveryPath), "回放失败后恢复文件应保留");
                Assert.NotNull(writer.LastReplayFailureAtForTest);

                // 退避窗口内第二次回放：直接跳过（文件仍在、行数不变）
                await writer.ReplayRecoveryForTestAsync();
                Assert.True(File.Exists(_recoveryPath), "退避窗口内不应重试回放");
            }

            // 解锁后若已过退避窗口，回放可恢复——此处仅验证退避状态被记录（30s 窗口不便在单测内等待）
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }
}
