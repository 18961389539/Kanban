using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Core.Entities;
using Kanban.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// HistoryQueryHandler 批量端点（QueryHistoryBatchAsync）测试：
/// 多设备一次往返、服务端全量查询（一次 SQL，无深分页）、子查询失败隔离与整体失败语义。
/// 批量端点解决生产复盘页「逐设备 × 逐页」串行往返的分钟级延迟；
/// 服务端全量语义避免逐页 Skip/Take 深分页在 30s Invoke 超时窗口内完不成的问题。
/// </summary>
public class HistoryQueryHandlerTests
{
    private readonly IHistoryService _history;
    private readonly IHistoryQueryExecutor _executor;
    private readonly HistoryQueryHandler _handler;

    public HistoryQueryHandlerTests()
    {
        _history = Substitute.For<IHistoryService>();
        _executor = Substitute.For<IHistoryQueryExecutor>();
        // 缺陷分支（QueryWindowBounds）走真实临时库验证，见 DefectHistoryStoreTests；此处不涉及
        _handler = new HistoryQueryHandler(_history, _executor, null!, Substitute.For<ILogger<HistoryQueryHandler>>());
    }

    [Fact]
    public async Task QueryBatchAsync_MultipleDevices_ReturnsResultsInRequestOrder()
    {
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 8);
        _executor.QueryProductionLogsStrict(from, to, "dev1", null)
            .Returns(new List<ProductionLog> { MakeLog(1, "dev1") });
        _executor.QueryProductionLogsStrict(from, to, "dev2", null)
            .Returns(new List<ProductionLog> { MakeLog(2, "dev2") });

        var response = await _handler.QueryBatchAsync(new BatchHistoryQueryRequest
        {
            Queries =
            [
                new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = from, To = to, DeviceId = "dev1", Page = 1, PageSize = 500 },
                new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = from, To = to, DeviceId = "dev2", Page = 1, PageSize = 500 },
            ],
        });

        Assert.Equal(2, response.Results.Count);
        Assert.Equal("dev1", Assert.Single(response.Results[0].ProductionLogs).DeviceId);
        Assert.Equal("dev2", Assert.Single(response.Results[1].ProductionLogs).DeviceId);
        Assert.Equal(HistoryErrorCode.None, response.Results[0].ErrorCode);
    }

    [Fact]
    public async Task QueryBatchAsync_LargeWindow_ReturnsFullSetInOneCall()
    {
        // 模拟大窗口全量（1200 条）：服务端一次 Strict 全量查询返回，不做逐页 Skip/Take
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 8);
        _executor.QueryProductionLogsStrict(from, to, "dev1", null)
            .Returns(Enumerable.Range(1, 1200).Select(i => MakeLog(i, "dev1")).ToList());

        var response = await _handler.QueryBatchAsync(new BatchHistoryQueryRequest
        {
            Queries =
            [
                new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = from, To = to, DeviceId = "dev1", Page = 1, PageSize = 500 },
            ],
        });

        var result = Assert.Single(response.Results);
        Assert.Equal(HistoryErrorCode.None, result.ErrorCode);
        Assert.Equal(1200, result.ProductionLogs.Count);
        Assert.Equal(1200, result.Total);
        // 全量语义：只调一次 Strict 查询，绝不逐页翻页（深分页性能灾难）
        _executor.Received(1).QueryProductionLogsStrict(from, to, "dev1", null);
    }

    [Fact]
    public async Task QueryBatchAsync_OneSubQueryFails_MarksThatItemOnly()
    {
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 8);
        _executor.QueryProductionLogsStrict(from, to, "dev1", null)
            .Returns(new List<ProductionLog> { MakeLog(1, "dev1") });
        _executor.QueryProductionLogsStrict(from, to, "dev2", null)
            .Returns(x => throw new InvalidOperationException("db down"));

        var response = await _handler.QueryBatchAsync(new BatchHistoryQueryRequest
        {
            Queries =
            [
                new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = from, To = to, DeviceId = "dev1", Page = 1, PageSize = 500 },
                new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = from, To = to, DeviceId = "dev2", Page = 1, PageSize = 500 },
            ],
        });

        // 失败子查询隔离：dev1 正常，dev2 该项 ErrorCode=QueryFailed
        Assert.Equal(HistoryErrorCode.None, response.Results[0].ErrorCode);
        Assert.Equal(HistoryErrorCode.QueryFailed, response.Results[1].ErrorCode);
        Assert.NotEmpty(response.Results[1].Error);
    }

    [Fact]
    public async Task QueryBatchAsync_EmptyRequest_ReturnsEmptyResults()
    {
        var response = await _handler.QueryBatchAsync(new BatchHistoryQueryRequest { Queries = [] });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task QueryBatchAsync_WorkOrderQuery_UsesWorkOrderPath()
    {
        _history.QueryProductionLogsByWorkOrder(42)
            .Returns(new List<ProductionLog> { MakeLog(7, "dev1") });

        var response = await _handler.QueryBatchAsync(new BatchHistoryQueryRequest
        {
            Queries =
            [
                new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, WorkOrderId = 42, Page = 1, PageSize = 500 },
            ],
        });

        var result = Assert.Single(response.Results);
        Assert.Equal(HistoryErrorCode.None, result.ErrorCode);
        Assert.Equal(7, Assert.Single(result.ProductionLogs).Id);
    }

    // ──────────── 资源上限：子查询数 / 时间范围（防无鉴权 Hub 上的全表扫描 DoS） ────────────

    [Fact]
    public async Task QueryBatchAsync_TooManyQueries_TruncatesToLimit()
    {
        // 超过 32 个子查询：只执行前 32 个（截断而非拒绝，正常客户端 3~10 个不受影响）
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 8);
        _executor.QueryProductionLogsStrict(from, to, Arg.Any<string>(), null)
            .Returns(new List<ProductionLog> { MakeLog(1, "dev1") });

        var queries = Enumerable.Range(0, 40)
            .Select(i => new HistoryQueryRequest
            {
                QueryType = HistoryQueryType.ProductionLog,
                From = from, To = to,
                DeviceId = $"dev{i % 3}", Page = 1, PageSize = 500,
            })
            .ToList();

        var response = await _handler.QueryBatchAsync(new BatchHistoryQueryRequest { Queries = queries });

        Assert.Equal(32, response.Results.Count); // 截断到上限
        _executor.Received(32).QueryProductionLogsStrict(from, to, Arg.Any<string>(), null);
    }

    [Fact]
    public async Task QueryBatchAsync_NullRange_DefaultsToRecent24Hours()
    {
        // 未指定 From/To：默认最近 24h 窗口（替代历史 MinValue..MaxValue 全表扫描）
        _executor.QueryProductionLogsStrict(Arg.Any<DateTime>(), Arg.Any<DateTime>(), "dev1", null)
            .Returns(new List<ProductionLog> { MakeLog(1, "dev1") });

        var response = await _handler.QueryBatchAsync(new BatchHistoryQueryRequest
        {
            Queries =
            [
                new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, DeviceId = "dev1", Page = 1, PageSize = 500 },
            ],
        });

        Assert.Equal(HistoryErrorCode.None, Assert.Single(response.Results).ErrorCode);
        // From ≈ now-24h（±1min 容差），To ≈ now
        _executor.Received(1).QueryProductionLogsStrict(
            Arg.Is<DateTime>(f => f > DateTime.Now.AddHours(-25) && f <= DateTime.Now.AddHours(-23)),
            Arg.Is<DateTime>(t => t > DateTime.Now.AddMinutes(-1) && t <= DateTime.Now),
            "dev1", null);
    }

    [Fact]
    public async Task QueryBatchAsync_WideWindow_TruncatedToRecent7Days()
    {
        // 30 天窗口超限：截断为最近 7 天（To 保持不变，From 前移）
        var from = DateTime.Now.AddDays(-30);
        var to = DateTime.Now;
        _executor.QueryProductionLogsStrict(Arg.Any<DateTime>(), Arg.Any<DateTime>(), "dev1", null)
            .Returns(new List<ProductionLog> { MakeLog(1, "dev1") });

        var response = await _handler.QueryBatchAsync(new BatchHistoryQueryRequest
        {
            Queries =
            [
                new HistoryQueryRequest { QueryType = HistoryQueryType.ProductionLog, From = from, To = to, DeviceId = "dev1", Page = 1, PageSize = 500 },
            ],
        });

        Assert.Equal(HistoryErrorCode.None, Assert.Single(response.Results).ErrorCode);
        _executor.Received(1).QueryProductionLogsStrict(
            Arg.Is<DateTime>(f => f >= to.AddDays(-7).AddMinutes(-1) && f <= to.AddDays(-7).AddMinutes(1)),
            Arg.Is<DateTime>(t => t == to),
            "dev1", null);
    }

    private static ProductionLog MakeLog(int id, string deviceId) => new()
    {
        Id = id,
        DeviceId = deviceId,
        DeviceName = $"设备{deviceId}",
        ShiftName = "早班",
        OkProduction = id,
        NgProduction = 0,
        StatusWord = 1,
        Timestamp = new DateTime(2026, 8, 8, 8, 0, 0),
    };
}
