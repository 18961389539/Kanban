using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.IO;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ProductionReviewExportChartServiceTests
{
    [Fact]
    public void DumpOeeWaterfallSvg_LabelInside_PreviewOnly()
    {
        // 临时预览（不落生产代码）：OEE 瀑布图柱内标签（LabelPlacement.Inside）效果
        // 对比：BuildOeeWaterfall 当前是柱顶上方（Outside）。此处仅构造副本改 Inside 导出 SVG 预览。
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text, ChartPalette.Grid, ChartPalette.Ok, ChartPalette.Ng,
            ChartPalette.ShiftBg, ChartPalette.Base, ChartPalette.Pause);
        var model = service.BuildOeeWaterfall(0.91, 0.85, 0.95, 0.67, palette);
        // 仅预览用：在测试里把标签改为柱内（生产代码不动）
        foreach (var s in model.Series.OfType<BarSeries>())
        {
            s.LabelPlacement = LabelPlacement.Inside;
            s.LabelMargin = 6;
        }
        ((IPlotModel)model).Update(true);
        var svg = SvgExporter.ExportToString(model, 700, 300, true);
        var shotsDir = Path.Combine(Path.GetTempPath(), "kanban_shots");
        Directory.CreateDirectory(shotsDir);
        File.WriteAllText(Path.Combine(shotsDir, "oee_waterfall_label_inside_preview.svg"), svg);
        Assert.True(svg.Contains("91%"), "SVG 应包含 91% 标签");
    }

    [Fact]
    public void DumpTrendSvg_Temporary_Diagnostic()
    {
        // 临时诊断：导出趋势图 SVG 检查异常文字（排查 OxyPlot 源码版渲染，验证后删除）
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text, ChartPalette.Grid, ChartPalette.Ok, ChartPalette.Ng,
            ChartPalette.ShiftBg, ChartPalette.Base, ChartPalette.Pause);
        var now = DateTime.Now;
        var buckets = Enumerable.Range(0, 48).Select(i => now.AddHours(i - 24)).ToArray();
        var ok = new int[48];
        var ng = new int[48];
        for (var i = 0; i < 48; i++)
        {
            ok[i] = 80 + i % 20;
            ng[i] = i % 5 == 0 ? 15 : 3;
        }
        var shifts = new List<ShiftConfig>
        {
            new() { Name = "白班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
            new() { Name = "夜班", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) },
        };
        var model = service.BuildTrend(buckets, ok, ng, ProductionReviewBucketSize.Hour, 60, 1, shifts, palette);
        ((IPlotModel)model).Update(true);
        var svg = SvgExporter.ExportToString(model, 900, 280, true);
        var shotsDir = Path.Combine(Path.GetTempPath(), "kanban_shots");
        Directory.CreateDirectory(shotsDir);
        File.WriteAllText(Path.Combine(shotsDir, "trend_svg_dump.svg"), svg);
        Assert.True(svg.Length > 1000);
    }

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
    public void OeeWaterfall_PassesPlotModelUpdateValidation()
    {
        // 回归：BarSeries 的 YAxisKey 曾反向绑定到 LinearAxis（XAxisKey/YAxisKey 对调），
        // 构建模型不报错，但渲染期 PlotModel.Update() 调用 UpdateBarSeriesManagers→GetCategoryAxis()
        // 抛 "BarSeries requires a CategoryAxis on the Y axis"（2026-08-10 复盘页线上问题）。
        // 此测试显式触发 Update 校验，防止同类轴绑定错误漏网。
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text,
            ChartPalette.Grid,
            ChartPalette.Ok,
            ChartPalette.Ng,
            ChartPalette.ShiftBg,
            ChartPalette.Base,
            ChartPalette.Pause);

        var waterfall = service.BuildOeeWaterfall(0.8, 0.9, 0.95, 0.68, palette);

        // PlotModel.Update 是 IPlotModel 的显式接口实现（WPF PlotView 渲染路径）
        var exception = Record.Exception(() => ((IPlotModel)waterfall).Update(true));
        Assert.Null(exception);

        // 轴绑定语义：YAxisKey 指向 CategoryAxis、XAxisKey 指向 LinearAxis
        var series = Assert.IsType<BarSeries>(waterfall.Series[0]);
        Assert.Equal("category", series.YAxisKey);
        Assert.Equal("value", series.XAxisKey);
    }

    [Fact]
    public void OeeWaterfall_RendersToSvgWithoutException()
    {
        // 回归：轴绑定错误在 Update 校验层就能捕获，但柱顶标签（LabelFormatString）走 Render 阶段，
        // 再补一次完整渲染验证（SvgExporter 全管线渲染，无需 GUI/SkiaSharp）。
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text,
            ChartPalette.Grid,
            ChartPalette.Ok,
            ChartPalette.Ng,
            ChartPalette.ShiftBg,
            ChartPalette.Base,
            ChartPalette.Pause);

        var waterfall = service.BuildOeeWaterfall(0.33, 0.958, 0.955, 0.302, palette);
        ((IPlotModel)waterfall).Update(true);

        var svg = SvgExporter.ExportToString(waterfall, 420, 260, true);
        // 4 根柱 + 4 个柱顶标签（P0 百分比）都应渲染出来
        Assert.Contains("<rect", svg);
        Assert.Contains("33%", svg);
        Assert.Contains("96%", svg);
        Assert.Contains("30%", svg);
    }

    [Fact]
    public void DefectParetoChart_BuildsStructureAndPassesUpdateValidation()
    {
        // 方案 A 双轴帕累托：柱=数量（左轴）+ 折线=累计占比（右轴）。
        // 回归：BarSeries 轴 Key 必须按 OxyPlot 2.x 约定绑定（YAxisKey=CategoryAxis），
        // 且 PlotModel.Update 渲染期校验不能抛（2026-08-10 瀑布图同类事故）。
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text,
            ChartPalette.Grid,
            ChartPalette.Ok,
            ChartPalette.Ng,
            ChartPalette.ShiftBg,
            ChartPalette.Base,
            ChartPalette.Pause);

        var chart = service.BuildDefectParetoChart(
            [
                new DefectParetoSummary { DefectName = "划痕", DeviceName = "注塑机1", Count = 5, CumulativePercent = 41.7 },
                new DefectParetoSummary { DefectName = "色差", DeviceName = "注塑机1", Count = 4, CumulativePercent = 75.0 },
                new DefectParetoSummary { DefectName = "变形", DeviceName = "注塑机1", Count = 3, CumulativePercent = 100.0 },
            ],
            palette);

        // 三根轴：类别（Bottom）+ 数量（Left）+ 累计占比（Right）
        Assert.Equal(3, chart.Axes.Count);
        Assert.NotNull(chart.Axes.FirstOrDefault(a => a is CategoryAxis && a.Position == AxisPosition.Bottom));
        var countAxis = Assert.IsType<LinearAxis>(chart.Axes.First(a => a.Key == "count"));
        Assert.Equal(AxisPosition.Left, countAxis.Position);
        var cumAxis = Assert.IsType<LinearAxis>(chart.Axes.First(a => a.Key == "cum"));
        Assert.Equal(AxisPosition.Right, cumAxis.Position);
        Assert.Equal(0, cumAxis.Minimum);
        Assert.Equal(1, cumAxis.Maximum);

        var bar = Assert.IsType<BarSeries>(chart.Series[0]);
        Assert.Equal("count", bar.XAxisKey);
        Assert.Equal("category", bar.YAxisKey); // OxyPlot 2.x 约定：YAxisKey 指向 CategoryAxis
        Assert.Equal(3, bar.Items.Count);
        var line = Assert.IsType<LineSeries>(chart.Series[1]);
        Assert.Equal("cum", line.YAxisKey);
        Assert.Equal(3, line.Points.Count);
        Assert.Equal(0.417, line.Points[0].Y, 3);

        // 渲染期校验：与 WPF PlotView 渲染路径一致（IPlotModel.Update 是显式实现）
        var exception = Record.Exception(() => ((IPlotModel)chart).Update(true));
        Assert.Null(exception);

        // 空列表：返回空模型不抛
        var empty = service.BuildDefectParetoChart([], palette);
        Assert.Empty(empty.Series);
    }

    [Fact]
    public void OeeWaterfall_KeepsAxisSpanWhenAllRatesAreOne()
    {
        // 回归：全 1.0 时起点若取 1.0 会 Minimum==Maximum 轴退化，柱子整列消失只剩文字（修复前线上问题）
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text,
            ChartPalette.Grid,
            ChartPalette.Ok,
            ChartPalette.Ng,
            ChartPalette.ShiftBg,
            ChartPalette.Base,
            ChartPalette.Pause);

        var waterfall = service.BuildOeeWaterfall(1.0, 1.0, 1.0, 1.0, palette);

        var valueAxis = Assert.IsType<OxyPlot.Axes.LinearAxis>(waterfall.Axes.Single(axis => axis.Key == "value"));
        Assert.Equal(0.9, valueAxis.Minimum);
        Assert.Equal(1.0, valueAxis.Maximum);
        Assert.True(valueAxis.Maximum > valueAxis.Minimum);
        Assert.Equal(4, ((BarSeries)waterfall.Series[0]).Items.Count);
    }

    [Fact]
    public void OeeWaterfall_KeepsZeroOriginWhenRatesAreLow()
    {
        // 回归：低 OEE（性能 24%/OEE 22%）时若也压缩轴起点，低值柱会被压成细线，
        // 视觉上只剩柱端文字（2026-08 线上问题）；此时必须保持 0 起点，柱长=率值直观。
        var service = new ProductionReviewChartService();
        var palette = new OverviewChartPalette(
            ChartPalette.Text,
            ChartPalette.Grid,
            ChartPalette.Ok,
            ChartPalette.Ng,
            ChartPalette.ShiftBg,
            ChartPalette.Base,
            ChartPalette.Pause);

        var waterfall = service.BuildOeeWaterfall(0.243, 0.944, 0.949, 0.217, palette);

        var valueAxis = Assert.IsType<OxyPlot.Axes.LinearAxis>(waterfall.Axes.Single(axis => axis.Key == "value"));
        Assert.Equal(0, valueAxis.Minimum);
        Assert.Equal(1.0, valueAxis.Maximum);
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
