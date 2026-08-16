using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 操作审计服务单元测试：覆盖追加落库、过滤查询、分页、保留期清理，以及静态门面的初始化/Noop 行为。
/// 与 HubAuditRoutingTests 共享串行集合：AuditLog 静态门面是进程级状态，禁止并行 Reset。
/// </summary>
[Collection("AuditLogState")]
public class AuditServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DatabaseProvider _db;

    public AuditServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AuditTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_settings);
        _db.EnsureCreatedAll();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private AuditService CreateService() => new(_db, NullLogger<AuditService>.Instance);

    /// <summary>构造一个容量极小且消费者暂停的服务，用于稳定验证队列饱和统计。</summary>
    private AuditService CreateSaturatedService(int queueLength)
        => new(_db, NullLogger<AuditService>.Instance, queueLength, startWorker: false);

    /// <summary>轮询等待异步批量落库完成（FlushIntervalMs=2000，最多等 5 秒）。</summary>
    private static void WaitFlushed(AuditService service, int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.FlushedCount < expectedCount && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.True(service.FlushedCount >= expectedCount,
            $"预期落库 {expectedCount} 条，实际 {service.FlushedCount}（丢弃 {service.DroppedCount}）");
    }

    [Fact]
    public void Record_ThenQuery_ReturnsEntryWithOperatorAndAction()
    {
        using var service = CreateService();
        service.Record("Device.Update", "Device", "device-001", succeeded: true, detail: "保存 3 台", operatorName: "admin");

        WaitFlushed(service, 1);

        var (items, total) = service.QueryPaged(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, null, null, null, 1, 100);
        Assert.Equal(1, total);
        var entry = Assert.Single(items);
        Assert.Equal("admin", entry.Operator);
        Assert.Equal("Device.Update", entry.Action);
        Assert.Equal("device-001", entry.TargetId);
        Assert.True(entry.Succeeded);
    }

    [Fact]
    public void Record_BeforeAndAfterJson_Persisted()
    {
        using var service = CreateService();
        service.Record("WorkOrder.Start", "WorkOrder", "WO-9",
            operatorName: "admin", beforeJson: "{\"status\":\"Pending\"}", afterJson: "{\"status\":\"Running\"}");
        WaitFlushed(service, 1);

        var (items, _) = service.QueryPaged(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, null, null, null, 1, 100);
        var entry = Assert.Single(items);
        Assert.Equal("{\"status\":\"Pending\"}", entry.BeforeJson);
        Assert.Equal("{\"status\":\"Running\"}", entry.AfterJson);
    }

    [Fact]
    public void QueryAll_ReturnsAllMatchesBeyondPageSize()
    {
        using var service = CreateService();
        for (var i = 0; i < 5; i++)
            service.Record($"Action.{i}", "Test", $"id-{i}", operatorName: "op");
        WaitFlushed(service, 5);

        var (items, total) = service.QueryAll(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, null, null, null);
        Assert.Equal(5, total);
        Assert.Equal(5, items.Count);
    }

    [Fact]
    public void QueryAll_RespectsMaxResults()
    {
        using var service = CreateService();
        for (var i = 0; i < 5; i++)
            service.Record($"Action.{i}", "Test", $"id-{i}", operatorName: "op");
        WaitFlushed(service, 5);

        var (items, total) = service.QueryAll(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, null, null, null, maxResults: 2);
        Assert.Equal(5, total);   // 总数不截断
        Assert.Equal(2, items.Count); // 导出条数截断
    }

    [Fact]
    public void Query_OperatorFilter_MatchesSubstring()
    {
        using var service = CreateService();
        service.Record("Auth.Login", "User", "admin", operatorName: "admin");
        service.Record("Auth.Login", "User", "engineer", operatorName: "engineer");
        WaitFlushed(service, 2);

        var (items, total) = service.QueryPaged(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            "admin", null, null, null, 1, 100);
        Assert.Equal(1, total);
        Assert.Equal("admin", items[0].Operator);
    }

    [Fact]
    public void Query_ActionAndResultFilter_Work()
    {
        using var service = CreateService();
        service.Record("WorkOrder.Start", "WorkOrder", "WO-1", operatorName: "a");
        service.Record("WorkOrder.Abort", "WorkOrder", "WO-2", operatorName: "a");
        service.Record("WorkOrder.Start", "WorkOrder", "WO-3", succeeded: false, detail: "非法状态", operatorName: "a");
        WaitFlushed(service, 3);

        var (started, _) = service.QueryPaged(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, "Start", null, true, 1, 100);
        Assert.Single(started);

        var (failed, _) = service.QueryPaged(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, null, null, false, 1, 100);
        Assert.Single(failed);
    }

    /// <summary>
    /// 回归（审查修复 2026-08-13）：页码无上界时 (page-1)*pageSize 整数溢出为负，
    /// EF Skip(负数) 抛 ArgumentOutOfRangeException → 查询接口 500。现按 HistoryPagination 钳制，
    /// 超大页码应正常返回空页。
    /// </summary>
    [Fact]
    public void QueryPaged_HugePageNumber_DoesNotThrow_AndReturnsEmpty()
    {
        using var service = CreateService();
        service.Record("Device.Update", "Device", "device-001", operatorName: "admin");
        WaitFlushed(service, 1);

        var (items, total) = service.QueryPaged(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, null, null, null, int.MaxValue, 20);

        Assert.Equal(1, total);      // Total 不受页码影响
        Assert.Empty(items);         // 超界页返回空，而非抛异常
    }

    [Fact]
    public void Query_Paging_ReturnsCorrectSlice()
    {
        using var service = CreateService();
        for (var i = 0; i < 5; i++)
            service.Record($"Action.{i}", "Test", $"id-{i}", operatorName: "op");
        WaitFlushed(service, 5);

        var (page1, total) = service.QueryPaged(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, null, null, null, 1, 2);
        Assert.Equal(5, total);
        Assert.Equal(2, page1.Count);

        var (page3, _) = service.QueryPaged(
            DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1),
            null, null, null, null, 3, 2);
        Assert.Single(page3);

        // 最新在前：第一页的第一条应为最后写入的 Action.4
        Assert.Equal("Action.4", page1[0].Action);
    }

    [Fact]
    public void CleanupOldEntries_RemovesOnlyExpired()
    {
        using var service = CreateService();
        service.Record("Old.Action", "Test", "old", operatorName: "op");
        service.Record("New.Action", "Test", "new", operatorName: "op");
        WaitFlushed(service, 2);

        // 手工把第一条时间改到 40 天前（绕过只追加接口，直接操作库模拟历史数据）
        using (var context = _db.CreateAuditContext())
        {
            var old = context.AuditEntries.OrderBy(e => e.Id).First();
            old.Timestamp = DateTime.Now.AddDays(-40);
            context.SaveChanges();
        }

        var deleted = service.CleanupOldEntries(retentionDays: 30);
        Assert.Equal(1, deleted);

        var (items, total) = service.QueryPaged(
            DateTime.Now.AddDays(-90), DateTime.Now.AddDays(1),
            null, null, null, null, 1, 100);
        Assert.Equal(1, total);
        Assert.Equal("new", items[0].TargetId);
    }

    [Fact]
    public void EnsureCreatedAll_UpgradesLegacyAuditDb_AddsBeforeAfterColumns()
    {
        // 模拟 MVP 版本已存在的审计库（无 BeforeJson/AfterJson 列），
        // 验证 EnsureCreatedAll 的幂等补丁能补列且不破坏既有数据。
        // 独立临时目录构造，避免与类级临时库的文件锁冲突。
        var legacyDir = Path.Combine(Path.GetTempPath(), $"AuditLegacy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyDir);
        try
        {
            var legacySettings = new AppSettings { ConfigDirectory = legacyDir };
            var legacyDb = new DatabaseProvider(legacySettings);
            var dbFile = legacyDb.CreateAuditContext().Database.GetDbConnection().DataSource;
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}"))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "AuditEntries" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_AuditEntries" PRIMARY KEY AUTOINCREMENT,
                        "Timestamp" TEXT NOT NULL,
                        "Operator" TEXT NOT NULL,
                        "Action" TEXT NOT NULL,
                        "TargetType" TEXT NOT NULL,
                        "TargetId" TEXT NULL,
                        "Succeeded" INTEGER NOT NULL,
                        "Detail" TEXT NULL);
                    INSERT INTO "AuditEntries" ("Timestamp","Operator","Action","TargetType","TargetId","Succeeded","Detail")
                    VALUES ('2026-08-01 08:00:00','admin','Legacy.Action','Test','id-1',1,NULL);
                    """;
                create.ExecuteNonQuery();
            }

            legacyDb.EnsureCreatedAll(); // 幂等：不应破坏既有表/数据，且补上缺失列

            using (var context = legacyDb.CreateAuditContext())
            {
                var columns = context.Database.SqlQueryRaw<string>(
                    "SELECT name FROM pragma_table_info('AuditEntries')").ToList();
                Assert.Contains("BeforeJson", columns);
                Assert.Contains("AfterJson", columns);

                var legacy = Assert.Single(context.AuditEntries.ToList());
                Assert.Equal("Legacy.Action", legacy.Action);
                Assert.Equal("admin", legacy.Operator);
            }
        }
        finally
        {
            try { Directory.Delete(legacyDir, recursive: true); } catch { }
        }
    }

    // ──────────── 静态门面 ────────────

    [Fact]
    public void AuditLog_Uninitialized_IsNoop()
    {
        AuditLog.ResetForTest();
        // 不应抛异常
        AuditLog.Record("Any.Action", "Test", "id");
        AuditLog.ResetForTest();
    }

    [Fact]
    public void AuditLog_Initialized_ForwardsWithOperatorProvider()
    {
        AuditLog.ResetForTest();
        var service = Substitute.For<IAuditService>();
        AuditLog.Initialize(service, () => "当前用户");

        AuditLog.Record("Auth.Login", "User", "admin");

        service.Received(1).Record("Auth.Login", "User", "admin", true, null, "当前用户", null, null);
        AuditLog.ResetForTest();
    }

    [Fact]
    public void AuditLog_BeforeAfterObjects_SerializedAndForwarded()
    {
        AuditLog.ResetForTest();
        var service = Substitute.For<IAuditService>();
        AuditLog.Initialize(service, () => "op");

        AuditLog.Record("WorkOrder.Start", "WorkOrder", "WO-1",
            before: new { Status = "Pending" }, after: new { Status = "Running" });

        // 直接检查被记录调用的参数（避免 NSubstitute 对 null 常量+Arg.Is 组合的规格错位）
        var call = Assert.Single(service.ReceivedCalls());
        var args = call.GetArguments();
        Assert.Equal("WorkOrder.Start", args[0]);
        Assert.Equal("WorkOrder", args[1]);
        Assert.Equal("WO-1", args[2]);
        Assert.Equal(true, args[3]);
        Assert.Null(args[4]); // detail
        Assert.Equal("op", args[5]); // operatorName
        Assert.Contains("Pending", (string)args[6]!); // beforeJson
        Assert.Contains("Running", (string)args[7]!); // afterJson
        AuditLog.ResetForTest();
    }

    [Fact]
    public void Record_WhenQueueFull_DropWriteCountsDroppedEntries()
    {
        // DropWrite 语义下 TryWrite 满时仍返回 true，只有 itemDropped 回调能统计真实丢弃。
        using var service = CreateSaturatedService(queueLength: 2);
        service.Record("A", "Test", "1");
        service.Record("B", "Test", "2");
        service.Record("C", "Test", "3"); // 队列已满 → 必须计入丢弃

        Assert.Equal(0, service.FlushedCount); // 消费者暂停，无落库
        Assert.Equal(1, service.QueueDroppedCount);
        Assert.Equal(1, service.DroppedCount);
    }

    [Fact]
    public void AuditLog_TruncatedJson_RemainsParseable()
    {
        AuditLog.ResetForTest();
        var service = Substitute.For<IAuditService>();
        AuditLog.Initialize(service, () => "op");

        // 构造远超 4000 字符上限的对象，验证截断改写为固定结构而非非法 JSON
        // Status 放在前面，保证出现在 4000 字符预览内
        var big = new { Status = "Running", Text = new string('x', 6000) };
        AuditLog.Record("WorkOrder.Start", "WorkOrder", "WO-1", before: big, after: big);

        var call = Assert.Single(service.ReceivedCalls());
        var args = call.GetArguments();
        using var doc = System.Text.Json.JsonDocument.Parse((string)args[6]!);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(64, doc.RootElement.GetProperty("sha256").GetString()!.Length);
        Assert.Contains("Running", doc.RootElement.GetProperty("preview").GetString());
        AuditLog.ResetForTest();
    }

    [Fact]
    public void AuditLog_FailureRecord_ForwardsSucceededFalse()
    {
        AuditLog.ResetForTest();
        var service = Substitute.For<IAuditService>();
        AuditLog.Initialize(service, () => "op");

        AuditLog.Record("Auth.Login", "User", "bad", succeeded: false, detail: "密码错误");

        service.Received(1).Record("Auth.Login", "User", "bad", false, "密码错误", "op");
        AuditLog.ResetForTest();
    }
}
