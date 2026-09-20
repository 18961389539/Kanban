using System;
using System.Linq;
using Kanban.Collector.Core.Entities;
using Kanban.Contracts.Metrics;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class DeviceDetailHourBoardTests
{
    [Fact]
    public void BuildHourByHourBoard_UsesSameOkDiffAsHourlySeries()
    {
        var history = new InMemoryHistoryService();
        history.ProductionLogs.AddRange(
        [
            Log("d1", new DateTime(2026, 9, 6, 7, 50, 0), 100),
            Log("d1", new DateTime(2026, 9, 6, 8, 30, 0), 130),
            Log("d1", new DateTime(2026, 9, 6, 9, 20, 0), 180),
        ]);

        var from = new DateTime(2026, 9, 6, 8, 0, 0);
        var now = new DateTime(2026, 9, 6, 9, 40, 0);
        var series = DeviceDetailQueryService.QueryHourlySeries(history, "d1", from, now);
        var board = DeviceDetailQueryService.BuildHourByHourBoard(
            history, "d1", from, new DateTime(2026, 9, 6, 16, 0, 0), now, 50);

        Assert.Equal(2, series.Buckets.Length);
        Assert.Equal(30, series.OkDiff[0]);
        Assert.Equal(50, series.OkDiff[1]);

        Assert.Equal(8, board.Count);
        Assert.Equal(30, board[0].Actual);
        Assert.Equal(HourBucketState.Miss, board[0].State);
        Assert.Equal(50, board[1].Actual);
        Assert.Equal(HourBucketState.Current, board[1].State);
        Assert.All(board.Skip(2), item => Assert.Equal(HourBucketState.Future, item.State));
    }

    [Fact]
    public void BuildHourlyProductionChart_StillReturnsModelWhenSeriesExists()
    {
        var history = new InMemoryHistoryService();
        history.ProductionLogs.Add(Log("d1", new DateTime(2026, 9, 6, 10, 15, 0), 12));
        var chart = DeviceDetailQueryService.BuildHourlyProductionChart(
            history, "d1", 50, 8, new DateTime(2026, 9, 6, 12, 0, 0));
        Assert.NotNull(chart);
    }

    private static ProductionLog Log(string deviceId, DateTime at, int ok) => new()
    {
        DeviceId = deviceId,
        OkProduction = ok,
        NgProduction = 0,
        Timestamp = at,
        ShiftName = "白班",
    };
}
