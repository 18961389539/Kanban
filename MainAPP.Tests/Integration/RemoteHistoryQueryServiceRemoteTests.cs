using System.IO;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// RemoteHistoryQueryService Remote 分支契约测试（审查修复 2026-08-13 补 0 覆盖盲区）：
/// 本地 Kestrel 起最小 SignalR Hub 实现 QueryHistoryAsync/QueryHistoryBatchAsync，
/// 验证 Remote 路由（IsRemote=true）的分页、全量翻页聚合、按窗口批量（含 >32 分块）、
/// 服务端错误与截断异常路径——此前该分支"由 E2E/冒烟覆盖"的注释不实，实为盲区。
/// </summary>
[Trait("Category", "Integration")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public class RemoteHistoryQueryServiceRemoteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private string _hubAddress = "";
    private string _tempDir = "";
    private DatabaseProvider _db = null!;
    private HistoryService _local = null!;
    private DefectHistoryStore _localDefects = null!;

    // ──────────── 测试专用最小 Hub（弱类型，方法名与客户端 nameof 一致） ────────────

    public sealed class RemoteQueryTestHub : Hub
    {
        /// <summary>测试数据源：Id 1..N 的生产日志（按需切片）。</summary>
        public static List<ProductionLogDto> ProductionData = [];

        /// <summary>Error 注入：非空时 QueryHistoryAsync 返回该错误。</summary>
        public static string? InjectError;

        /// <summary>Total 覆盖：非 null 时忽略实际数据量（用于截断测试）。</summary>
        public static int? OverrideTotal;

        /// <summary>QueryHistoryAsync 调用次数（断言批量往返次数用）。</summary>
        public static int SingleQueryCount;

        /// <summary>QueryHistoryBatchAsync 调用次数（断言窗口批量分块用）。</summary>
        public static int BatchQueryCount;

        public static void ResetStatic()
        {
            ProductionData = [];
            InjectError = null;
            OverrideTotal = null;
            SingleQueryCount = 0;
            BatchQueryCount = 0;
        }

        public Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request)
        {
            Interlocked.Increment(ref SingleQueryCount);
            if (InjectError is { } err)
                return Task.FromResult(new HistoryQueryResponse { Error = err, ErrorCode = HistoryErrorCode.QueryFailed });

            var filtered = ProductionData
                .Where(d => string.IsNullOrEmpty(request.DeviceId) || d.DeviceId == request.DeviceId)
                .Where(d => request.From == null || d.Timestamp >= request.From)
                .Where(d => request.To == null || d.Timestamp <= request.To)
                .ToList();
            var total = OverrideTotal ?? filtered.Count;
            var page = Math.Max(1, request.Page);
            var size = Math.Clamp(request.PageSize, 1, 500);
            var items = filtered.Skip((page - 1) * size).Take(size).ToList();
            return Task.FromResult(new HistoryQueryResponse
            {
                Total = total,
                Page = page,
                PageSize = size,
                ProductionLogs = items,
            });
        }

        public Task<BatchHistoryQueryResponse> QueryHistoryBatchAsync(BatchHistoryQueryRequest request)
        {
            Interlocked.Increment(ref BatchQueryCount);
            var results = new List<HistoryQueryResponse>();
            foreach (var q in request.Queries)
            {
                if (InjectError is { } err)
                {
                    results.Add(new HistoryQueryResponse { Error = err, ErrorCode = HistoryErrorCode.QueryFailed });
                    continue;
                }
                var filtered = ProductionData
                    .Where(d => string.IsNullOrEmpty(q.DeviceId) || d.DeviceId == q.DeviceId)
                    .Where(d => q.From == null || d.Timestamp >= q.From)
                    .Where(d => q.To == null || d.Timestamp <= q.To)
                    .ToList();
                results.Add(new HistoryQueryResponse
                {
                    Total = filtered.Count,
                    ProductionLogs = filtered,
                });
            }
            return Task.FromResult(new BatchHistoryQueryResponse { Results = results });
        }
    }

    // ──────────── 生命周期 ────────────

    public async ValueTask InitializeAsync()
    {
        RemoteQueryTestHub.ResetStatic();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        _app = builder.Build();
        _app.MapHub<RemoteQueryTestHub>("/hubs/remotequery");
        await _app.StartAsync();
        _hubAddress = _app.Urls.First();

        _tempDir = Path.Combine(Path.GetTempPath(), "RemoteQueryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var settings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(settings);
        _db.EnsureCreatedAll();
        _local = new HistoryService(_db, NullLogger<HistoryService>.Instance);
        _localDefects = new DefectHistoryStore(_db, NullLogger<DefectHistoryStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<RemoteHistoryQueryService> CreateRemoteServiceAsync()
    {
        var client = new KanbanDataClient($"{_hubAddress}/hubs/remotequery",
            NullLogger<KanbanDataClient>.Instance, useMessagePack: false);
        await client.ConnectAsync();
        var runtime = Substitute.For<IRuntimeMode>();
        runtime.IsRemote.Returns(true);
        return new RemoteHistoryQueryService(_local, _localDefects, new SnEventStore(_db), client, runtime,
            NullLogger<RemoteHistoryQueryService>.Instance);
    }

    private static ProductionLogDto Log(int id, string device = "dev-1", int ok = 10)
        => new()
        {
            Id = id,
            DeviceId = device,
            DeviceName = "设备",
            ShiftName = "白班",
            OkProduction = ok,
            NgProduction = 0,
            StatusWord = 1,
            Timestamp = new DateTime(2026, 7, 22, 10, 0, 0).AddMinutes(id),
        };

    // ──────────── 用例 ────────────

    [Fact]
    public async Task QueryProductionLogsPaged_Remote_ReturnsSlicedPage()
    {
        RemoteQueryTestHub.ProductionData = Enumerable.Range(1, 25).Select(i => Log(i)).ToList();
        var svc = await CreateRemoteServiceAsync();

        var (items, total) = svc.QueryProductionLogsPaged(
            DateTime.MinValue, DateTime.MaxValue, "dev-1", null, 3, 10);

        Assert.Equal(25, total);
        Assert.Equal(5, items.Count); // 第 3 页：21..25
        Assert.Equal(21, items[0].Id);
        Assert.Equal(25, items[^1].Id);
    }

    [Fact]
    public async Task QueryProductionLogs_Remote_FetchesAllPages_Aggregated()
    {
        // Total=1200 而每页最多 500 → 需 3 页聚合
        RemoteQueryTestHub.ProductionData = Enumerable.Range(1, 1200).Select(i => Log(i)).ToList();
        var svc = await CreateRemoteServiceAsync();

        var logs = svc.QueryProductionLogs(DateTime.MinValue, DateTime.MaxValue, "dev-1");

        Assert.Equal(1200, logs.Count);
        Assert.Equal(1, logs[0].Id);
        Assert.Equal(1200, logs[^1].Id);
        Assert.Equal("dev-1", logs[500].DeviceId); // 映射为实体而非 DTO
    }

    [Fact]
    public async Task QueryProductionLogs_Remote_ServerError_ThrowsLocalized()
    {
        RemoteQueryTestHub.ProductionData = [Log(1)];
        RemoteQueryTestHub.InjectError = "disk I/O error";
        var svc = await CreateRemoteServiceAsync();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            svc.QueryProductionLogs(DateTime.MinValue, DateTime.MaxValue, "dev-1"));

        Assert.Contains("历史查询失败", ex.Message);
    }

    [Fact]
    public async Task QueryProductionLogs_Remote_TruncatedByPageCap_ThrowsLimitMessage()
    {
        // Total 永大于已收（每页 500）→ 200 页上限未收齐 → 抛 F325 截断异常（此前静默返回部分数据）
        RemoteQueryTestHub.ProductionData = [Log(1)];
        RemoteQueryTestHub.OverrideTotal = int.MaxValue;
        var svc = await CreateRemoteServiceAsync();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            svc.QueryProductionLogs(DateTime.MinValue, DateTime.MaxValue, "dev-1"));

        Assert.Contains("10 万条", ex.Message);
        Assert.True(RemoteQueryTestHub.SingleQueryCount >= 200, "应翻页至上限后抛异常");
    }

    [Fact]
    public async Task QueryProductionLogsByDeviceWindowsBatch_Remote_ChunksBeyond32()
    {
        // 40 个窗口 > 服务端单批 32 上限 → 应分 2 次批量 Invoke 且结果聚合完整（审查修复批次的分块逻辑）
        RemoteQueryTestHub.ProductionData = Enumerable.Range(1, 40)
            .Select(i => Log(i, device: $"dev-{i:D2}", ok: i * 10))
            .ToList();
        var svc = await CreateRemoteServiceAsync();
        var windows = Enumerable.Range(1, 40)
            .Select(i => (WorkOrderId: 1000 + i, DeviceId: $"dev-{i:D2}", From: DateTime.MinValue, To: DateTime.MaxValue))
            .ToList();

        var result = svc.QueryProductionLogsByDeviceWindowsBatch(windows);

        Assert.Equal(40, result.Count);
        Assert.Equal(2, RemoteQueryTestHub.BatchQueryCount); // 32 + 8 两批
        Assert.Equal(250, result[1025][0].OkProduction);    // dev-25 → 25*10
        Assert.Equal(400, result[1040][0].OkProduction);    // dev-40 → 40*10
    }
}
