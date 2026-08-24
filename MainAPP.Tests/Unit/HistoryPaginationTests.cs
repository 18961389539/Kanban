using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 历史查询分页参数归一化测试：防止超大 Page/PageSize 造成
/// Skip 溢出或全量拉取（服务端分页下推的安全阀）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class HistoryPaginationTests
{
    [Fact]
    public void Normalize_ClampsPageSizeToMax()
    {
        var (page, size) = HistoryPagination.Normalize(1, int.MaxValue);
        Assert.Equal(1, page);
        Assert.Equal(HistoryPagination.MaxPageSize, size);
    }

    [Fact]
    public void Normalize_ClampsPageToMax()
    {
        var (page, size) = HistoryPagination.Normalize(int.MaxValue, 50);
        Assert.Equal(HistoryPagination.MaxPage, page);
        Assert.Equal(50, size);
    }

    [Fact]
    public void Normalize_ClampsNonPositiveToMinimum()
    {
        var (page, size) = HistoryPagination.Normalize(0, 0);
        Assert.Equal(1, page);
        Assert.Equal(1, size);

        var (page2, size2) = HistoryPagination.Normalize(-3, -7);
        Assert.Equal(1, page2);
        Assert.Equal(1, size2);
    }

    [Fact]
    public void Offset_NeverOverflowsInt()
    {
        // MaxPage × MaxPageSize ≈ 10^5，远小于 int.MaxValue；直接验证不溢出、不抛异常
        var offset = HistoryPagination.Offset(int.MaxValue, int.MaxValue);
        Assert.InRange(offset, 0, int.MaxValue);
        Assert.Equal((HistoryPagination.MaxPage - 1) * HistoryPagination.MaxPageSize, offset);
    }

    [Fact]
    public void Normalize_KeepsReasonableValues()
    {
        var (page, size) = HistoryPagination.Normalize(3, 100);
        Assert.Equal(3, page);
        Assert.Equal(100, size);
    }
}
