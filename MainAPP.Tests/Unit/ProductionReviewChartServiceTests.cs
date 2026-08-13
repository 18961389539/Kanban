using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.ViewModels;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 复盘页产量趋势图（BuildTrend）回归测试：班次分区、异常桶标注、目标线 annotation、多行 Tracker Title。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class ProductionReviewChartServiceTests
{
    private static OverviewChartPalette CreatePalette() => new(
        ChartPalette.Text, ChartPalette.Grid, ChartPalette.Ok, ChartPalette.Ng,
        ChartPalette.ShiftBg, ChartPalette.Base, ChartPalette.Pause);

    private static readonly ShiftConfig[] DayShift =
    [
        new() { Name = "早班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
    ];

    [Fact]
    public void BuildTrend_WithShifts_AddsShiftBackgroundAndBoundaryAnnotations()
    {
        var from = new DateTime(2026, 8, 8, 0, 0, 0);
        var buckets = Enumerable.Range(0, 48).Select(i => from.AddHours(i)).ToArray(); // 2 天
        var ok = Enumerable.Repeat(100, 48).ToArray();
        var ng = Enumerable.Repeat(2, 48).ToArray();

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        // 早班(8:00-20:00) × 窗口内 2 天 = 2 段背景（90% 参考带绑右轴，不计入）
        var backgrounds = model.Annotations.OfType<RectangleAnnotation>()
            .Where(a => string.IsNullOrEmpty(a.YAxisKey)).ToList();
        Assert.Equal(2, backgrounds.Count);
        // P2-10 班次名去重：同名班次只标首次出现的段，后续段 Text=null
        Assert.Equal("早班", backgrounds[0].Text);
        Assert.Null(backgrounds[1].Text);
        // 边界竖线：每天 20:00 边界（窗口内 2 条）
        var boundaryLines = model.Annotations.OfType<LineAnnotation>()
            .Where(a => a.Type == LineAnnotationType.Vertical).ToList();
        Assert.Equal(2, boundaryLines.Count);
        // 目标线为 Horizontal annotation（不参与轴缩放）；90% 参考线绑右轴
        var targetLine = model.Annotations.OfType<LineAnnotation>()
            .Single(a => a.Type == LineAnnotationType.Horizontal && string.IsNullOrEmpty(a.YAxisKey));
        Assert.Equal(LineStyle.Dash, targetLine.LineStyle);
        Assert.Single(model.Annotations.OfType<LineAnnotation>()
            .Where(a => a.Type == LineAnnotationType.Horizontal && a.YAxisKey == "quality"));
    }

    [Fact]
    public void BuildTrend_HighNgRateBucket_GetsAlertMarker()
    {
        var from = new DateTime(2026, 8, 8, 0, 0, 0);
        var buckets = Enumerable.Range(0, 8).Select(i => from.AddHours(i)).ToArray();
        var ok = Enumerable.Repeat(100, 8).ToArray();
        var ng = Enumerable.Repeat(0, 8).ToArray();
        ng[3] = 30; // 30/130 ≈ 23% > 10% 阈值

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        var markers = model.Annotations.OfType<PointAnnotation>().ToList();
        Assert.Single(markers);
        Assert.Equal("!", markers[0].Text);
        Assert.Equal(130, markers[0].Y, 1);
    }

    [Fact]
    public void BuildTrend_TrackerTitle_ContainsTimeShiftAndQuality()
    {
        var from = new DateTime(2026, 8, 8, 9, 0, 0);
        var buckets = new[] { from, from.AddHours(1) };
        var ok = new[] { 90, 0 };
        var ng = new[] { 10, 0 };

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        var bar = model.Series.OfType<RectangleBarSeries>().First().Items[0];
        Assert.Contains("08-08 09:00", bar.Title);
        Assert.Contains("[早班]", bar.Title);
        Assert.Contains("90.0%", bar.Title); // 90/(90+10)
    }

    [Fact]
    public void BuildTrend_TrackerFormatString_ShowsItemTitleAndQuality()
    {
        // P2-17 回归：左键点击 Tracker 必须显示多行 Title 与良品率百分比。
        // RectangleBarSeries 的 StringHelper.Format 参数 values=[Title,X轴,X0,X1,Y轴,Y0,Y1,item.Title]，
        // {7}=item.Title（多行）；LineSeries 的 {4}=Y 值（良品率），{2} 是 X 值（时间戳）——原 {2:P1} 显示错误。
        var from = new DateTime(2026, 8, 8, 9, 0, 0);
        var buckets = new[] { from, from.AddHours(1) };
        var ok = new[] { 90, 0 };
        var ng = new[] { 10, 0 };

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        var bars = model.Series.OfType<RectangleBarSeries>().ToList();
        Assert.All(bars, b => Assert.Equal("{7}", b.TrackerFormatString));
        var qualityLine = model.Series.OfType<LineSeries>().Single(s => s.YAxisKey == "quality");
        Assert.Equal("{0}: {4:P1}", qualityLine.TrackerFormatString);

        // 用与 OxyPlot GetNearestPoint 相同的 StringHelper.Format 调用形态验证输出
        var bar = bars[0].Items[0];
        var rendered = OxyPlot.StringHelper.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            bars[0].TrackerFormatString,
            bar,
            bars[0].Title,
            "X", 0d, 1d,
            "Y", 0d, 90d,
            bar.Title);
        Assert.Contains("08-08 09:00", rendered);
        Assert.Contains("OK 产量: 90", rendered);
        Assert.Contains("良品率: 90.0%", rendered);
    }

    [Fact]
    public void BuildTrend_NoShifts_NoBackgroundButStillTargetLine()
    {
        var from = new DateTime(2026, 8, 8, 0, 0, 0);
        var buckets = Enumerable.Range(0, 12).Select(i => from.AddHours(i)).ToArray();
        var ok = Enumerable.Repeat(50, 12).ToArray();
        var ng = Enumerable.Repeat(1, 12).ToArray();

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 100, 1, [], CreatePalette());

        Assert.Empty(model.Annotations.OfType<RectangleAnnotation>()
            .Where(a => string.IsNullOrEmpty(a.YAxisKey))); // 无班次 → 无左轴背景带
        Assert.Single(model.Annotations.OfType<LineAnnotation>()
            .Where(a => a.Type == LineAnnotationType.Horizontal && string.IsNullOrEmpty(a.YAxisKey))); // 目标线
    }

    [Fact]
    public void BuildTrend_QualityAxisAndLine_Present()
    {
        var from = new DateTime(2026, 8, 8, 9, 0, 0);
        var buckets = new[] { from, from.AddHours(1), from.AddHours(2) };
        var ok = new[] { 90, 0, 50 };
        var ng = new[] { 10, 0, 0 };

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        // 右轴（良品率 0-100%）
        var qualityAxis = model.Axes.OfType<LinearAxis>().Single(a => a.Key == "quality");
        Assert.Equal(AxisPosition.Right, qualityAxis.Position);
        Assert.Equal(0, qualityAxis.Minimum);
        Assert.Equal(1, qualityAxis.Maximum);

        // 良率折线：90% / 空桶断线(NaN) / 100%
        var qualityLine = model.Series.OfType<LineSeries>().Single(s => s.YAxisKey == "quality");
        Assert.Equal(3, qualityLine.Points.Count);
        Assert.Equal(0.9, qualityLine.Points[0].Y, 3);
        Assert.True(double.IsNaN(qualityLine.Points[1].Y), "空桶应为断线点（NaN）");
        Assert.Equal(1.0, qualityLine.Points[2].Y, 3);

        // 90% 参考带（右轴 RectangleAnnotation）
        Assert.Single(model.Annotations.OfType<RectangleAnnotation>().Where(a => a.YAxisKey == "quality"));
    }

    [Fact]
    public void BuildTrend_QualityLineX_WithinBucketRange()
    {
        // 回归：折线 X 必须落在桶时间范围内（曾把 TimeSpan.Ticks 加到 ToDouble 天数上，
        // 导致 X≈1.8e9 撑爆 X 轴，RectangleBarSeries 柱形被压缩到不可见——复盘页趋势图柱形消失）
        var from = new DateTime(2026, 8, 8, 9, 0, 0);
        var buckets = new[] { from, from.AddHours(1) };
        var ok = new[] { 80, 50 };
        var ng = new[] { 20, 2 }; // 第一桶 NG 率 20% > 10% 阈值，必须触发异常桶标注

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        var qualityLine = model.Series.OfType<LineSeries>().Single(s => s.YAxisKey == "quality");
        // 折线 X 是桶中心：范围应为 [buckets[0], buckets[^1] + 1 桶跨度]（天数量级）
        var xMin = DateTimeAxis.ToDouble(buckets[0]);
        var xMax = DateTimeAxis.ToDouble(buckets[^1]) + 1; // +1 天裕量
        foreach (var point in qualityLine.Points)
        {
            // 曾把 TimeSpan.Ticks 加到 ToDouble 天数上，X 会变成 ~1.8e9（公元 490 万年）
            Assert.InRange(point.X, xMin, xMax);
        }

        // 异常桶标注（NG 率 20% 的桶必触发）的 X 同样在范围内
        var markers = model.Annotations.OfType<PointAnnotation>().ToList();
        Assert.Single(markers);
        foreach (var marker in markers)
        {
            Assert.InRange(marker.X, xMin, xMax);
        }
    }

    /// <summary>
    /// 回归测试：buckets 只有一个元素（X 跨度为 0）时，BuildTrend 不能让 OxyPlot 在渲染时抛
    /// "Invalid transform (screen coordinate=-1.028e+308)"。X 轴必须显式兜底扩展 ±1 个桶。
    /// </summary>
    [Fact]
    public void BuildTrend_SingleBucket_DoesNotThrowAndRendersXAxisRange()
    {
        var from = new DateTime(2026, 8, 8, 9, 0, 0);
        var buckets = new[] { from };
        var ok = new[] { 80 };
        var ng = new[] { 2 };

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        var xAxis = model.Axes.OfType<DateTimeAxis>().Single();
        // Minimum/Maximum 必须显式设置（OxyPlot 用 NaN 表示未设），且跨度 ≥ 1 桶（1 小时）
        // 以避免 ActualMin == ActualMax 导致 Axis.Transform 抛出
        Assert.False(double.IsNaN(xAxis.Minimum), "X 轴 Minimum 必须显式兜底，不能为 NaN");
        Assert.False(double.IsNaN(xAxis.Maximum), "X 轴 Maximum 必须显式兜底，不能为 NaN");
        var span = xAxis.Maximum - xAxis.Minimum;
        Assert.True(span > 0, $"X 轴跨度必须 > 0，实际 {span}");

        // 触发渲染（PlotModel.Update 是 IPlotModel 显式实现），断言不再抛 InvalidOperationException
        var ex = Record.Exception(() => ((IPlotModel)model).Update(true));
        Assert.Null(ex);
    }

    /// <summary>
    /// 回归测试：所有 buckets 时间相同（X 跨度为 0）时同样兜底。
    /// </summary>
    [Fact]
    public void BuildTrend_AllBucketsSameTime_XAxisRangeStillExpanded()
    {
        var sameTime = new DateTime(2026, 8, 8, 9, 0, 0);
        var buckets = new[] { sameTime, sameTime, sameTime };
        var ok = new[] { 50, 60, 70 };
        var ng = new[] { 1, 1, 1 };

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        var xAxis = model.Axes.OfType<DateTimeAxis>().Single();
        var span = xAxis.Maximum - xAxis.Minimum;
        Assert.True(span > 0, $"X 轴跨度必须 > 0，实际 {span}");
    }

    /// <summary>
    /// 回归：班次背景 RectangleAnnotation 之前 MaximumY=double.MaxValue，OxyPlot Render 时
    /// yaxis.Transform(double.MaxValue) 抛出 "Invalid transform (screen coordinate=-1.028e+308)"。
    /// 修复：yAxis.Maximum 显式设为数据最大值 + 5% padding，RectangleAnnotation.MaximumY 用 yAxis.Maximum。
    /// </summary>
    [Fact]
    public void BuildTrend_WithShifts_RendersRectangleAnnotationsWithoutOverflow()
    {
        var from = new DateTime(2026, 8, 8, 12, 0, 0);
        var buckets = Enumerable.Range(0, 48).Select(i => from.AddHours(i)).ToArray();
        var ok = Enumerable.Repeat(100, 48).ToArray();
        var ng = Enumerable.Repeat(2, 48).ToArray();

        var model = new ProductionReviewChartService().BuildTrend(
            buckets, ok, ng, ProductionReviewBucketSize.Hour, 120, 1, DayShift, CreatePalette());

        // 触发完整渲染（含 RectangleAnnotation.Render）
        var ex = Record.Exception(() => ((IPlotModel)model).Update(true));
        Assert.Null(ex);

        // yAxis 必须显式设了 Maximum（不再是 OxyPlot 自动算）
        var yAxis = model.Axes.OfType<LinearAxis>().Single(a => a.Position == AxisPosition.Left);
        Assert.False(double.IsNaN(yAxis.Maximum), "yAxis.Maximum 必须显式兜底，不能为 NaN");
        Assert.True(yAxis.Maximum > 0, $"yAxis.Maximum 必须 > 0，实际 {yAxis.Maximum}");
    }

    /// <summary>
    /// 柱内标签回归（2026-08-11 用户确认）：OEE 瀑布图数值显示在柱体内部（Inside），
    /// 深色文字保证黄柱（可用率）上对比度足够（浅色文字黄底不可读）。
    /// </summary>
    [Fact]
    public void BuildOeeWaterfall_LabelInside_DarkText()
    {
        var model = new ProductionReviewChartService().BuildOeeWaterfall(
            0.91, 0.85, 0.95, 0.67, CreatePalette());

        var bar = model.Series.OfType<BarSeries>().Single();
        Assert.Equal(LabelPlacement.Inside, bar.LabelPlacement);
        Assert.Equal("{0:P0}", bar.LabelFormatString);
        // 深色文字（黄柱上浅色文字不可读）
        var dark = OxyColor.FromRgb(0x1A, 0x20, 0x29);
        Assert.Equal(dark, bar.TextColor);

        // 完整渲染不抛（Transform 溢出校验）
        var ex = Record.Exception(() => ((IPlotModel)model).Update(true));
        Assert.Null(ex);
    }

    /// <summary>
    /// 缺陷帕累托柱内标签回归（2026-08-11 用户确认参照 OEE 瀑布图）：
    /// 数量显示在柱体内部（Inside）+ 深色文字。
    /// </summary>
    [Fact]
    public void BuildDefectPareto_LabelInside_DarkText()
    {
        var defects = new List<DefectParetoSummary>
        {
            new() { DefectName = "划伤", Count = 120, CumulativePercent = 50 },
            new() { DefectName = "毛刺", Count = 60, CumulativePercent = 75 },
            new() { DefectName = "变形", Count = 30, CumulativePercent = 100 },
        };
        var model = new ProductionReviewChartService().BuildDefectParetoChart(defects, CreatePalette());

        var bar = model.Series.OfType<BarSeries>().Single();
        Assert.Equal(LabelPlacement.Inside, bar.LabelPlacement);
        Assert.Equal("{0:N0}", bar.LabelFormatString);
        var dark = OxyColor.FromRgb(0x1A, 0x20, 0x29);
        Assert.Equal(dark, bar.TextColor);

        // 完整渲染不抛
        var ex = Record.Exception(() => ((IPlotModel)model).Update(true));
        Assert.Null(ex);
    }
}
