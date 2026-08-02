using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using OxyPlot;
using OxyPlot.Series;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ProductionReviewExportChartServiceTests
{
    [Fact]
    public void CsvExport_EscapesCommaAndQuotes()
    {
        var service = new ProductionReviewCsvExportService();
        var result = service.Build(new ProductionReviewCsvData(
            DateTime.Now.AddHours(-1),
            DateTime.Now,
            "白班",
            10,
            1,
            0.9,
            0.8,
            1,
            0,
            0,
            1,
            [new DeviceOverviewSummary { DeviceName = "设备,\"A\"" }],
            [],
            []));

        Assert.Contains("\"设备,\"\"A\"\"\"", result);
    }

    [Fact]
    public void ChartService_BuildsTrendAndWaterfallModels()
    {
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text,
            ChartPalette.Grid,
            ChartPalette.Ok,
            ChartPalette.Ng,
            ChartPalette.ShiftBg,
            ChartPalette.Base,
            ChartPalette.Pause);
        var buckets = new[] { DateTime.Now.AddHours(-1), DateTime.Now };

        var trend = service.BuildTrend(
            buckets,
            [10, 20],
            [1, 2],
            ProductionReviewBucketSize.Hour,
            30,
            1,
            [],
            palette);
        var waterfall = service.BuildOeeWaterfall(0.8, 0.9, 0.95, 0.68, palette);

        Assert.Equal(3, trend.Series.Count);
        Assert.Equal(2, trend.Series.OfType<RectangleBarSeries>().Count());
        Assert.Single(trend.Series.OfType<LineSeries>());
        Assert.Single(waterfall.Series.OfType<BarSeries>());
        Assert.Equal(4, ((BarSeries)waterfall.Series[0]).Items.Count);
    }

    [Fact]
    public void ChartService_BuildsHeatmapAndHandlesEmptyInput()
    {
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text,
            ChartPalette.Grid,
            ChartPalette.Ok,
            ChartPalette.Ng,
            ChartPalette.ShiftBg,
            ChartPalette.Base,
            ChartPalette.Pause);
        var buckets = new[] { DateTime.Now.AddHours(-1), DateTime.Now };

        var heatmap = service.BuildHeatmap(
            buckets,
            [new ProductionReviewHeatmapRow("设备 A", [3, 0])],
            ProductionReviewBucketSize.Hour,
            palette);
        var empty = service.BuildHeatmap(
            [],
            [],
            ProductionReviewBucketSize.Hour,
            palette);

        Assert.Single(heatmap.Series.OfType<RectangleBarSeries>());
        Assert.Equal(2, ((RectangleBarSeries)heatmap.Series[0]).Items.Count);
        Assert.Empty(empty.Series);
    }
}
