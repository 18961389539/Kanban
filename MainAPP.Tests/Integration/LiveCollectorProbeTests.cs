using System.IO;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 临时诊断测试：直连真实 Collector（127.0.0.1:5129）验证单查/批量历史查询行为。
/// 定位"复盘页/查询页查询无数据"：消息大小限制、服务端全量语义、序列化兼容。
/// 门控（审查修复 2026-08-13）：Requires=LiveCollector 标注用途；运行时双闸——
/// ①环境变量 RUN_LIVE_COLLECTOR_PROBES=1 显式启用（探针会写真实采集设置，防本地误触发）；
/// ②Collector 5129 不可达时 Assert.Skip。CI 无 Collector、不设环境变量，安全跳过。
/// 注意：xunit.v3 MTP 当前 runner 的 trait 查询过滤不生效，勿依赖 --filter 排除。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","LiveCollector")]
public class LiveCollectorProbeTests
{
    private const string HubUrl = "http://127.0.0.1:5129/hubs/kanban";

    /// <summary>探针前置：未显式启用或 Collector 不可达时跳过（探针会写真实采集设置，须双重确认）。</summary>
    private static void RequireCollector()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_COLLECTOR_PROBES") != "1")
            Assert.Skip("未设置 RUN_LIVE_COLLECTOR_PROBES=1：探针测试会写真实 Collector 设置，需显式启用");

        using var probe = new System.Net.Sockets.TcpClient();
        try
        {
            probe.Connect("127.0.0.1", 5129);
        }
        catch (System.Net.Sockets.SocketException)
        {
            Assert.Skip("Collector 未运行（127.0.0.1:5129 不可达）：探针测试需真实 Collector");
        }
    }

    [Fact]
    public async Task Probe_SinglePagedQuery()
    {
        RequireCollector();
        var client = new KanbanDataClient(HubUrl, NullLogger<KanbanDataClient>.Instance);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            var from = DateTime.Now.AddDays(-1);
            var to = DateTime.Now;
            var request = new HistoryQueryRequest
            {
                QueryType = HistoryQueryType.ProductionLog,
                From = from,
                To = to,
                Page = 1,
                PageSize = 500,
            };
            var response = await client.QueryHistoryAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HistoryErrorCode.None, response.ErrorCode);
            Assert.True(response.ProductionLogs.Count > 0, $"单查 0 条, Total={response.Total}, Error={response.Error}");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task Probe_BatchQuery()
    {
        RequireCollector();
        var client = new KanbanDataClient(HubUrl, NullLogger<KanbanDataClient>.Instance);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            var from = DateTime.Now.AddDays(-1);
            var to = DateTime.Now;
            var request = new BatchHistoryQueryRequest
            {
                Queries =
                [
                    new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = from, To = to, DeviceId = "device-001", Page = 1, PageSize = 500 },
                    new HistoryQueryRequest { QueryType = HistoryQueryType.AlarmEvent, From = from, To = to, DeviceId = "device-001", Page = 1, PageSize = 500 },
                    new HistoryQueryRequest { QueryType = HistoryQueryType.StatusTransition, From = from, To = to, DeviceId = "device-001", Page = 1, PageSize = 500 },
                ],
            };
            var response = await client.QueryHistoryBatchAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(3, response.Results.Count);
            Assert.True(response.Results.All(r => r.ErrorCode == HistoryErrorCode.None),
                $"批量子查询失败: {string.Join(" | ", response.Results.Select(r => r.Error))}");
            Assert.True(response.Results[0].ProductionLogs.Count > 0, $"批量生产 0 条, Total={response.Results[0].Total}");

            // 7 天窗口（复盘页默认范围）：数据量大，验证大响应不受 32KB 限制
            var weekFrom = DateTime.Now.AddDays(-7);
            var weekRequest = new BatchHistoryQueryRequest
            {
                Queries =
                [
                    new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = weekFrom, To = to, DeviceId = "device-001", Page = 1, PageSize = 500 },
                    new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = weekFrom, To = to, DeviceId = "device-002", Page = 1, PageSize = 500 },
                    new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = weekFrom, To = to, DeviceId = "device-003", Page = 1, PageSize = 500 },
                ],
            };
            var weekResponse = await client.QueryHistoryBatchAsync(weekRequest, TestContext.Current.CancellationToken);
            Assert.True(weekResponse.Results.All(r => r.ErrorCode == HistoryErrorCode.None),
                $"7 天批量失败: {string.Join(" | ", weekResponse.Results.Select(r => r.Error))}");
            Assert.True(weekResponse.Results.Sum(r => r.ProductionLogs.Count) > 1000,
                $"7 天批量数据异常少: {string.Join(",", weekResponse.Results.Select(r => r.ProductionLogs.Count))} 条");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>审计路由探针：真实连接调用写接口（幂等保存当前设置），验证 Remote 审计落库。</summary>
    [Fact]
    public async Task Probe_SaveSettings_RecordsAudit()
    {
        RequireCollector(); // Collector 不可达 → Skip（探针需真实 Collector 运行在 5129）
        var dataDir = RequireDataDir(); // 无 KANBAN_DATA_DIR → Skip（探针需明确数据目录定位审计库）
        var client = new KanbanAdminClient(HubUrl, NullLogger<KanbanDataClient>.Instance);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            await client.SaveCollectorSettingsAsync(new CollectorSettingsDto
            {
                PollingIntervalMs = 200,
                HistoryWriteIntervalScans = 25,
                PlcBrand = 1, // Mitsubishi（demo 当前品牌）
            }, TestContext.Current.CancellationToken);
            // 等待异步审计 flush 落库后直接查库验证
            await Task.Delay(2000, TestContext.Current.CancellationToken);
            var auditCount = CountAuditEntries(dataDir, "CollectorSettings.Update");
            Assert.True(auditCount > 0, $"审计表无 CollectorSettings.Update 记录（审计路由未生效）");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>失败路径审计探针：非法 PLC 品牌触发保存失败，应落 Succeeded=0 审计（而非误记成功）。</summary>
    [Fact]
    public async Task Probe_SaveSettings_Failure_RecordsAuditFailed()
    {
        RequireCollector(); // Collector 不可达 → Skip（探针需真实 Collector 运行在 5129）
        var dataDir = RequireDataDir(); // 无 KANBAN_DATA_DIR → Skip（探针需明确数据目录定位审计库）
        var client = new KanbanAdminClient(HubUrl, NullLogger<KanbanDataClient>.Instance);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            var before = CountAuditEntries(dataDir, "CollectorSettings.Update", succeeded: false);
            await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
                client.SaveCollectorSettingsAsync(new CollectorSettingsDto
                {
                    PollingIntervalMs = 200,
                    HistoryWriteIntervalScans = 25,
                    PlcBrand = 0, // 非法品牌 → 服务端校验抛异常
                }, TestContext.Current.CancellationToken));
            await Task.Delay(2000, TestContext.Current.CancellationToken);
            var failedCount = CountAuditEntries(dataDir, "CollectorSettings.Update", succeeded: false);
            Assert.True(failedCount > before,
                $"失败调用未落 Succeeded=0 审计（失败路径缺失）: before={before} after={failedCount}");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>探针要求显式数据目录：未设置 KANBAN_DATA_DIR 时跳过（避免查默认路径误报）。</summary>
    private static string RequireDataDir()
    {
        var dataDir = Environment.GetEnvironmentVariable("KANBAN_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
            Assert.Skip("未设置 KANBAN_DATA_DIR：探针测试需显式指定数据目录以定位审计库");
        return dataDir!;
    }

    private static int CountAuditEntries(string dataDir, string action, bool succeeded = true)
    {
        // 审计实体落 audit_logs.db（DatabaseProvider.AuditPath），非 kanban.db
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + Path.Combine(dataDir, "Config", "audit_logs.db"));
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AuditEntries WHERE Action = @a AND Succeeded = @s";
        cmd.Parameters.AddWithValue("@a", action);
        cmd.Parameters.AddWithValue("@s", succeeded ? 1 : 0);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Probe_SubscribeThenQuery_SameConnection()
    {
        RequireCollector();
        // 模拟 MainAPP 场景：同一连接先订阅（长驻）再查询——若查询挂起则复现问题
        var client = new KanbanDataClient(HubUrl, NullLogger<KanbanDataClient>.Instance);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            client.OnSnapshot(_ => { });
            var subscribeTask = Task.Run(async () =>
            {
                await client.SubscribeSnapshotsAsync(cts.Token);
            }, cts.Token);

            // 等待订阅建立
            await Task.Delay(1000, cts.Token);

            var from = DateTime.Now.AddDays(-1);
            var request = new BatchHistoryQueryRequest
            {
                Queries =
                [
                    new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = from, To = DateTime.Now, DeviceId = "device-001", Page = 1, PageSize = 500 },
                ],
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.QueryHistoryBatchAsync(request, cts.Token);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 10000, $"订阅后查询挂起 {sw.ElapsedMilliseconds}ms");
            Assert.Equal(HistoryErrorCode.None, response.Results[0].ErrorCode);
            Assert.True(response.Results[0].ProductionLogs.Count > 0, "订阅后查询 0 条");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
