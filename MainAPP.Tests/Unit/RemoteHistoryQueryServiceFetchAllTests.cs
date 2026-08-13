using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// RemoteHistoryQueryService.FetchAllPages 单元测试：锁定"客户端自动翻页聚合"行为——
/// 服务端单页上限 500（HistoryPagination.MaxPageSize），全量语义查询必须按响应 Total 逐页拉取，
/// 使 Remote 模式与 Local 模式（全量）行为一致。该类为 internal static，MainAPP 已 InternalsVisibleTo。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class RemoteHistoryQueryServiceFetchAllTests
{
    private const int MaxPages = 200; // 与 RemoteHistoryQueryService.MaxFetchAllPages 对齐
    private const int PageSize = 500; // 与服务端 HistoryPagination.MaxPageSize 对齐

    private static HistoryQueryRequest Request()
        => new() { QueryType = HistoryQueryType.ProductionLog, Page = 1, PageSize = PageSize };

    /// <summary>构造第 page 页响应：返回本页 count 条，Total 为服务端全量 Count。</summary>
    private static HistoryQueryResponse Page(int page, int total, int count)
        => new()
        {
            Total = total,
            Page = page,
            PageSize = PageSize,
            ProductionLogs = Enumerable.Range(0, count)
                .Select(i => new ProductionLogDto
                {
                    Id = (page - 1) * PageSize + i,
                    DeviceId = "dev-1",
                    DeviceName = "注塑机-1",
                    ShiftName = "白班",
                    OkProduction = 1,
                    NgProduction = 0,
                    StatusWord = 1,
                    WorkOrderId = null,
                    Timestamp = new DateTime(2026, 8, 1, 10, 0, 0).AddSeconds((page - 1) * PageSize + i),
                })
                .ToList(),
        };

    [Fact]
    public void FetchAllPages_超过单页上限时_逐页拉取至Total收齐并聚合()
    {
        // 1250 条 = 500 + 500 + 250（尾页），服务端每页最多 500
        var requestedPages = new List<int>();
        var response = RemoteHistoryQueryService.FetchAllPages(
            Request(),
            r =>
            {
                requestedPages.Add(r.Page);
                var count = Math.Min(PageSize, Math.Max(0, 1250 - (r.Page - 1) * PageSize));
                return Page(r.Page, total: 1250, count);
            },
            NullLogger<RemoteHistoryQueryService>.Instance);

        Assert.Equal([1, 2, 3], requestedPages);
        Assert.Equal(1250, response.ProductionLogs.Count);
        Assert.Equal(1250, response.Total);
        // 聚合顺序保持页序
        Assert.Equal(0, response.ProductionLogs[0].Id);
        Assert.Equal(1249, response.ProductionLogs[^1].Id);
    }

    [Fact]
    public void FetchAllPages_未分页路径Total等于本页条数_只请求一页()
    {
        // 按工单/最新一条等未分页路径：服务端 Total = 本页条数，首页即收齐
        var requestedPages = new List<int>();
        var response = RemoteHistoryQueryService.FetchAllPages(
            Request(),
            r =>
            {
                requestedPages.Add(r.Page);
                return Page(r.Page, total: 500, count: 500);
            },
            NullLogger<RemoteHistoryQueryService>.Instance);

        Assert.Equal([1], requestedPages);
        Assert.Equal(500, response.ProductionLogs.Count);
    }

    [Fact]
    public void FetchAllPages_空结果_只请求一页()
    {
        var requestedPages = new List<int>();
        var response = RemoteHistoryQueryService.FetchAllPages(
            Request(),
            r =>
            {
                requestedPages.Add(r.Page);
                return Page(r.Page, total: 0, count: 0);
            },
            NullLogger<RemoteHistoryQueryService>.Instance);

        Assert.Equal([1], requestedPages);
        Assert.Empty(response.ProductionLogs);
    }

    [Fact]
    public void FetchAllPages_服务端Total语义异常_达页数上限后抛异常()
    {
        // Total 永远大于已收（每页 500 但 Total=int.MaxValue）：必须在 MaxFetchAllPages 页后停止
        // 并抛异常（审查修复 2026-08-13：此前静默返回部分数据，调用方无法区分"收齐"与"截断"）
        var requestedPages = new List<int>();
        var ex = Assert.Throws<InvalidOperationException>(() => RemoteHistoryQueryService.FetchAllPages(
            Request(),
            r =>
            {
                requestedPages.Add(r.Page);
                return Page(r.Page, total: int.MaxValue, count: PageSize);
            },
            NullLogger<RemoteHistoryQueryService>.Instance));

        Assert.Equal(MaxPages, requestedPages.Count);
        Assert.Contains("10 万条", ex.Message); // F325 文案
    }

    [Fact]
    public void FetchAllPages_任一页返回Error_立即返回该页不再翻页()
    {
        var requestedPages = new List<int>();
        var response = RemoteHistoryQueryService.FetchAllPages(
            Request(),
            r =>
            {
                requestedPages.Add(r.Page);
                if (r.Page == 1)
                    return Page(1, total: 1000, count: PageSize);
                return new HistoryQueryResponse { Error = "落库失败: disk I/O" };
            },
            NullLogger<RemoteHistoryQueryService>.Instance);

        Assert.Equal([1, 2], requestedPages);
        Assert.Equal("落库失败: disk I/O", response.Error);
    }
}
