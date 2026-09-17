using System.Linq;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
using MainAPP.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ChartServiceTests
{
    // ═══════════════ BuildOeeChart ═══════════════

    [Fact]
    public void BuildOeeChart_NormalValues_ReturnsBarChartWith4Bars()
    {
        var chart = ChartService.BuildOeeChart(0.95, 0.90, 0.85, 0.73)!;

        Assert.NotNull(chart);
        var bar = chart.Series.OfType<BarSeries>().FirstOrDefault();
        Assert.NotNull(bar);
        Assert.Equal(4, bar!.Items.Count);
        Assert.Equal(0.95, bar.Items[0].Value);
        Assert.Equal(0.90, bar.Items[1].Value);
        Assert.Equal(0.85, bar.Items[2].Value);
        Assert.Equal(0.73, bar.Items[3].Value);

        // 目标线存在
        var targetLine = chart.Series.OfType<LineSeries>().FirstOrDefault(l => l.Title.Contains("目标"));
        Assert.NotNull(targetLine);
    }

    [Fact]
    public void BuildOeeChart_AllZero_ReturnsChartWithZeroBars()
    {
        var chart = ChartService.BuildOeeChart(0, 0, 0, 0)!;

        Assert.NotNull(chart);
        var bar = chart.Series.OfType<BarSeries>().FirstOrDefault();
        Assert.NotNull(bar);
        Assert.Equal(4, bar!.Items.Count);
        Assert.All(bar.Items, item => Assert.Equal(0, item.Value));
    }

    [Fact]
    public void BuildOeeChart_PerfectValues_ReturnsChartWithFullBars()
    {
        var chart = ChartService.BuildOeeChart(1.0, 1.0, 1.0, 1.0)!;

        Assert.NotNull(chart);
        var bar = chart.Series.OfType<BarSeries>().FirstOrDefault();
        Assert.NotNull(bar);
        Assert.Equal(4, bar!.Items.Count);
        Assert.All(bar.Items, item => Assert.Equal(1, item.Value));
    }

    [Fact]
    public void BuildOeeChart_CustomTarget_ReturnsTargetLine()
    {
        var chart = ChartService.BuildOeeChart(0.9, 0.9, 0.9, 0.7, 0.90)!;

        var targetLine = chart.Series.OfType<LineSeries>().FirstOrDefault(l => l.Title.Contains("目标"));
        Assert.NotNull(targetLine);
        // 代码使用 P2 格式 → "90.00%"
        Assert.Contains("90.00%", targetLine!.Title);
    }

    [Fact]
    public void BuildOeeChart_DefaultTarget_Is85Percent()
    {
        var chart = ChartService.BuildOeeChart(0.9, 0.9, 0.9, 0.7)!;

        var targetLine = chart.Series.OfType<LineSeries>().FirstOrDefault(l => l.Title.Contains("目标"));
        Assert.NotNull(targetLine);
        // 代码使用 P2 格式 → "85.00%"
        Assert.Contains("85.00%", targetLine!.Title);
    }

    [Fact]
    public void BuildOeeChart_NegativeValues_BarsHaveNegativeValues()
    {
        var chart = ChartService.BuildOeeChart(-0.1, -0.2, -0.3, -0.4)!;

        Assert.NotNull(chart);
        var bar = chart.Series.OfType<BarSeries>().FirstOrDefault();
        Assert.NotNull(bar);
        Assert.Equal(4, bar!.Items.Count);
        Assert.Equal(-0.1, bar.Items[0].Value);
        Assert.Equal(-0.2, bar.Items[1].Value);
        Assert.Equal(-0.3, bar.Items[2].Value);
        Assert.Equal(-0.4, bar.Items[3].Value);
    }

    [Fact]
    public void BuildOeeChart_HasCategoryAxisWith4Labels()
    {
        var chart = ChartService.BuildOeeChart(0.9, 0.9, 0.9, 0.9)!;

        var catAxis = chart.Axes.OfType<CategoryAxis>().FirstOrDefault();
        Assert.NotNull(catAxis);
        var labels = catAxis!.ItemsSource?.OfType<string>().ToList() ?? new List<string>();
        Assert.Equal(new[] { "C良品率", "B性能达标率", "A时间稼动率", "OEE" }, labels);
    }

    [Fact]
    public void BuildOeeChart_HasValueAxisWithPercentageFormat()
    {
        var chart = ChartService.BuildOeeChart(0.9, 0.9, 0.9, 0.9)!;

        // OxyPlot 2.2.0 中 CategoryAxis 继承自 LinearAxis，需排除 CategoryAxis 才能拿到真正的值轴
        var valAxis = chart.Axes.OfType<LinearAxis>().FirstOrDefault(a => a is not CategoryAxis);
        Assert.NotNull(valAxis);
        // 验证值轴标题和范围（百分比轴 0~1）
        Assert.Equal("百分比", valAxis!.Title);
        Assert.Equal(0, valAxis.Minimum);
        Assert.Equal(1, valAxis.Maximum);
    }

    // ═══════════════ BuildProductionChart ═══════════════

    [Fact]
    public void BuildProductionChart_EmptyData_ReturnsEmptyModel()
    {
        var chart = ChartService.BuildProductionChart(Enumerable.Empty<(DateTime, int, int)>())!;
        Assert.NotNull(chart);
        Assert.Empty(chart.Series);
    }

    [Fact]
    public void BuildProductionChart_NormalData_ReturnsAreaSeries()
    {
        var now = DateTime.Now;
        var data = new[]
        {
            (now.AddHours(-2), 100, 5),
            (now.AddHours(-1), 120, 3),
            (now, 150, 8),
        };

        var chart = ChartService.BuildProductionChart(data)!;
        Assert.NotNull(chart);
        var areas = chart.Series.OfType<AreaSeries>().ToList();
        Assert.Equal(2, areas.Count);
    }

    // ═══════════════ BuildStatusChart ═══════════════

    [Fact]
    public void BuildStatusChart_EmptyData_ReturnsEmptyModel()
    {
        var chart = ChartService.BuildStatusChart(Enumerable.Empty<(DateTime, double, double, double)>())!;
        Assert.NotNull(chart);
        Assert.Empty(chart.Series);
    }

    // ═══════════════ BuildAlarmChart ═══════════════

    [Fact]
    public void BuildAlarmChart_EmptyData_ReturnsEmptyModel()
    {
        var chart = ChartService.BuildAlarmChart(Enumerable.Empty<(string, int, double)>())!;
        Assert.NotNull(chart);
        Assert.Empty(chart.Series);
    }

    // ═══════════════ BuildOeeTrendChart ═══════════════

    [Fact]
    public void BuildOeeTrendChart_EmptyData_ReturnsEmptyModel()
    {
        var chart = ChartService.BuildOeeTrendChart(Enumerable.Empty<(DateTime, double, string)>())!;
        Assert.NotNull(chart);
        Assert.Empty(chart.Series);
    }

    // ═══════════════ BuildOeeShiftBarChart ═══════════════

    [Fact]
    public void BuildOeeShiftBarChart_EmptyData_ReturnsEmptyModel()
    {
        var chart = ChartService.BuildOeeShiftBarChart(Enumerable.Empty<(string, double)>())!;
        Assert.NotNull(chart);
        Assert.Empty(chart.Series);
    }

    // ═══════════════ BuildOeeRing ═══════════════

    [Fact]
    public void BuildOeeRing_ZeroValue_ReturnsIdleChart()
    {
        var chart = ChartService.BuildOeeRing(0)!;
        Assert.NotNull(chart);
        var pie = chart.Series.OfType<PieSeries>().FirstOrDefault();
        Assert.NotNull(pie);
        Assert.Single(pie!.Slices); // 仅 1 个灰色占位扇区
    }

    [Fact]
    public void BuildOeeRing_FullValue_ReturnsFullColorChart()
    {
        var chart = ChartService.BuildOeeRing(1.0)!;
        Assert.NotNull(chart);
        var pie = chart.Series.OfType<PieSeries>().FirstOrDefault();
        Assert.NotNull(pie);
        Assert.Single(pie!.Slices); // 仅 1 个满色扇区
    }

    // ═══════════════ BuildQualityRing ═══════════════

    [Fact]
    public void BuildQualityRing_NormalValue_ReturnsTwoSlices()
    {
        var chart = ChartService.BuildQualityRing(0.5)!;
        Assert.NotNull(chart);
        var pie = chart.Series.OfType<PieSeries>().FirstOrDefault();
        Assert.NotNull(pie);
        Assert.Equal(2, pie!.Slices.Count);
    }

    // ═══════════════ BuildQualityPieChart ═══════════════

    [Fact]
    public void BuildQualityPieChart_ZeroTotal_ReturnsIdleChart()
    {
        var chart = ChartService.BuildQualityPieChart(0, 0)!;
        Assert.NotNull(chart);
        var pie = chart.Series.OfType<PieSeries>().FirstOrDefault();
        Assert.NotNull(pie);
        Assert.Single(pie!.Slices);
        Assert.Equal("无数据", pie.Slices[0].Label);
    }

    [Fact]
    public void BuildQualityPieChart_NormalData_ReturnsOkNgSlices()
    {
        var chart = ChartService.BuildQualityPieChart(100, 20)!;
        Assert.NotNull(chart);
        var pie = chart.Series.OfType<PieSeries>().FirstOrDefault();
        Assert.NotNull(pie);
        Assert.Equal(2, pie!.Slices.Count);
        Assert.Contains("OK", pie.Slices[0].Label);
        Assert.Contains("NG", pie.Slices[1].Label);
    }

    [Fact]
    public void BuildShiftQualityTrendChart_Empty_ReturnsModelWithoutSeries()
    {
        var chart = ChartService.BuildShiftQualityTrendChart([]);
        Assert.NotNull(chart);
        Assert.Empty(chart.Series);
    }

    [Fact]
    public void BuildShiftQualityTrendChart_Points_DrawsQualityAndTarget()
    {
        var now = DateTime.Today.AddHours(8);
        var shiftEnd = now.AddHours(8);
        var chart = ChartService.BuildShiftQualityTrendChart(
        [
            (now, 0.98),
            (now.AddHours(1), 0.96),
            (now.AddHours(2), 0.94),
        ], 0.95, now, shiftEnd);
        var lines = chart.Series.OfType<LineSeries>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(3, lines[0].Points.Count);
        Assert.Equal(2, lines[1].Points.Count);
        var xAxis = chart.Axes.OfType<DateTimeAxis>().Single();
        Assert.Equal(DateTimeAxis.ToDouble(now), xAxis.Minimum);
        Assert.Equal(DateTimeAxis.ToDouble(shiftEnd), xAxis.Maximum);
    }

    [Fact]
    public void BuildShiftQualityTrendChart_SinglePoint_PadsTimeAxis()
    {
        var now = DateTime.Today.AddHours(8);
        var shiftEnd = now.AddHours(8);
        var chart = ChartService.BuildShiftQualityTrendChart([(now, 0.97)], 0.95, now, shiftEnd);
        var xAxis = chart.Axes.OfType<DateTimeAxis>().Single();
        Assert.Equal(DateTimeAxis.ToDouble(now), xAxis.Minimum);
        Assert.Equal(DateTimeAxis.ToDouble(shiftEnd), xAxis.Maximum);
    }

    [Fact]
    public void BuildShiftQualityAndOutputChart_DrawsOkBarsAndQualityLine()
    {
        var start = DateTime.Today.AddHours(8);
        var end = start.AddHours(12);
        var chart = ChartService.BuildShiftQualityAndOutputChart(
            [start, start.AddHours(1), start.AddHours(2)],
            [12, 0, 7],
            [(start.AddMinutes(30), 0.98), (start.AddHours(2), 0.94)],
            0.95, start, end);

        var bars = Assert.Single(chart.Series.OfType<RectangleBarSeries>());
        Assert.Equal(2, bars.Items.Count); // 中间 0 不画柱
        var lines = chart.Series.OfType<LineSeries>().ToList();
        Assert.Equal(2, lines.Count); // 良率 + 达标线
        Assert.Equal(2, lines[0].Points.Count);
        Assert.Equal("shiftQuality", lines[0].YAxisKey);
        var xAxis = chart.Axes.OfType<DateTimeAxis>().Single();
        Assert.Equal(DateTimeAxis.ToDouble(start), xAxis.Minimum);
        Assert.Equal(DateTimeAxis.ToDouble(end), xAxis.Maximum);
        Assert.Contains(chart.Axes.OfType<LinearAxis>(), a => a.Position == AxisPosition.Right);
    }

    [Fact]
    public void BuildShiftQualityAndOutputChart_Empty_ReturnsModelWithoutSeries()
    {
        var chart = ChartService.BuildShiftQualityAndOutputChart([], [], []);
        Assert.Empty(chart.Series);
    }

    [Fact]
    public void BuildShiftHourlyOkBarChart_OneBarPerHour_OkOnly()
    {
        var from = DateTime.Today.AddHours(8);
        var chart = ChartService.BuildShiftHourlyOkBarChart(
            [from, from.AddHours(1), from.AddHours(2)],
            [12, 20, 7]);
        var bars = Assert.Single(chart.Series.OfType<BarSeries>());
        Assert.Equal(3, bars.Items.Count);
        Assert.Equal(12, bars.Items[0].Value);
        Assert.Equal(20, bars.Items[1].Value);
        Assert.Equal(7, bars.Items[2].Value);
        Assert.Empty(chart.Series.OfType<LineSeries>());
    }

    [Fact]
    public void BuildShiftHourlyOkBarChart_EmptyBuckets_NoSeries()
    {
        var chart = ChartService.BuildShiftHourlyOkBarChart([], []);
        Assert.Empty(chart.Series);
    }

    [Fact]
    public void BuildShiftHourlyOkBarChart_AllZero_UsesIntegerAxis()
    {
        var from = DateTime.Today.AddHours(20);
        var hours = Enumerable.Range(0, 12).Select(i => from.AddHours(i)).ToArray();
        var chart = ChartService.BuildShiftHourlyOkBarChart(hours, new int[12]);
        var y = chart.Axes.OfType<LinearAxis>().Single(a => a.Position == AxisPosition.Left);
        Assert.Equal(8, y.Maximum);
        Assert.Equal(2, y.MajorStep);
        var cat = chart.Axes.OfType<CategoryAxis>().Single();
        Assert.Equal(12, ((System.Collections.IList)cat.ItemsSource!).Count);
    }

    // ═══════════════ BuildStatusGanttChart ═══════════════

    [Fact]
    public void BuildStatusGanttChart_EmptyData_ReturnsEmptyModel()
    {
        var chart = ChartService.BuildStatusGanttChart(Enumerable.Empty<(DateTime, DateTime, int)>())!;
        Assert.NotNull(chart);
        Assert.Empty(chart.Series);
    }

    [Fact]
    public void BuildStatusGanttChart_FiltersInvalidStates()
    {
        var baseTime = new DateTime(2026, 1, 1, 8, 0, 0);
        var segments = new[]
        {
            (baseTime, baseTime.AddHours(1), 1),                  // 运行 - 有效
            (baseTime.AddHours(1), baseTime.AddHours(2), 2),      // 报警 - 有效
            (baseTime.AddHours(2), baseTime.AddHours(3), 3),      // 暂停 - 有效
            (baseTime.AddHours(3), baseTime.AddHours(4), 0),      // 无效
            (baseTime.AddHours(4), baseTime.AddHours(5), 4),      // 无效
            (baseTime.AddHours(5), baseTime.AddHours(6), 5),      // 无效
        };

        var chart = ChartService.BuildStatusGanttChart(segments)!;
        Assert.NotNull(chart);
        var series = chart.Series.OfType<RectangleBarSeries>().FirstOrDefault();
        Assert.NotNull(series);
        // 仅 3 个有效状态（1, 2, 3）保留
        Assert.Equal(3, series!.Items.Count);
    }

    // ═══════════════ BuildStatusBarChart ═══════════════

    [Fact]
    public void BuildStatusBarChart_EmptyData_ReturnsEmptyModel()
    {
        var chart = ChartService.BuildStatusBarChart(Enumerable.Empty<(DateTime, double, double, double)>())!;
        Assert.NotNull(chart);
        Assert.Empty(chart.Series);
    }

    // ═══════════════ BuildStatusPieChart ═══════════════

    [Fact]
    public void BuildStatusPieChart_ZeroTotal_ReturnsIdleChart()
    {
        // 总时长为 0 时返回"待机"占位饼图（不再返回 null）
        var chart = ChartService.BuildStatusPieChart(0, 0, 0);
        Assert.NotNull(chart);
    }

    [Fact]
    public void BuildStatusPieChart_NormalData_ReturnsPieChart()
    {
        var chart = ChartService.BuildStatusPieChart(3600, 120, 600);

        Assert.NotNull(chart);
        var pie = chart!.Series.OfType<PieSeries>().FirstOrDefault();
        Assert.NotNull(pie);
        Assert.Equal(3, pie!.Slices.Count); // 运行/报警/暂停
    }

    [Fact]
    public void BuildStatusPieChart_WithOffline_ReturnsFourSlices()
    {
        // 图例含离线行且总时长含离线，饼图必须同口径计入离线扇区（2026-08-30 口径修复）
        var chart = ChartService.BuildStatusPieChart(3600, 120, 600, 900);

        Assert.NotNull(chart);
        var pie = chart!.Series.OfType<PieSeries>().FirstOrDefault();
        Assert.NotNull(pie);
        Assert.Equal(4, pie!.Slices.Count); // 运行/报警/暂停/离线
    }

    [Fact]
    public void BuildStatusPieChart_HidesOutsideTicksAndLegend()
    {
        // 空 OutsideLabelFormat 仍会画径向刻度，裁切后像小扇区上的白刺；图例由卡片承载。
        var chart = ChartService.BuildStatusPieChart(3600, 120, 60, 30);
        var pie = chart.Series.OfType<PieSeries>().Single();

        Assert.Null(pie.OutsideLabelFormat);
        Assert.Null(pie.InsideLabelFormat);
        Assert.True(pie.Diameter < 1);
        Assert.Equal(-90, pie.StartAngle);
        Assert.False(chart.IsLegendVisible);
        Assert.Empty(chart.Legends);
    }

    // ═══════════════ BuildDefectBarChart ═══════════════

    [Fact]
    public void BuildDefectBarChart_Empty_ReturnsNull()
    {
        Assert.Null(ChartService.BuildDefectBarChart([]));
    }

    [Fact]
    public void BuildDefectBarChart_AllZero_ReturnsNull()
    {
        Assert.Null(ChartService.BuildDefectBarChart([("A", 0), ("B", 0)]));
    }

    [Fact]
    public void BuildDefectBarChart_Top10Ordering()
    {
        var defects = Enumerable.Range(1, 20)
            .Select(i => ($"缺陷{i}", 100 - i))
            .ToList();

        var chart = ChartService.BuildDefectBarChart(defects);
        Assert.NotNull(chart);

        var bar = chart!.Series.OfType<BarSeries>().FirstOrDefault();
        Assert.NotNull(bar);
        Assert.InRange(bar!.Items.Count, 1, 10); // Top 10
    }

    [Fact]
    public void BuildDefectBarChart_SingleDefect_ReturnsChart()
    {
        var chart = ChartService.BuildDefectBarChart([("划痕", 5)]);

        Assert.NotNull(chart);
        Assert.Contains(chart!.Series, s => s is BarSeries);
    }

    /// <summary>
    /// 回归测试（2026-08-11 文案错配修复）：折线标题必须是"累计占比"，
    /// 轴/系列不得再出现"停机时长""报警时长""速度""报警次数"等错配文案。
    /// </summary>
    [Fact]
    public void BuildDefectBarChart_LineTitleIsCumulativeShare_NoWrongLabels()
    {
        var chart = ChartService.BuildDefectBarChart([("划痕", 12), ("毛刺", 8), ("压痕", 3)]);
        Assert.NotNull(chart);

        var line = chart!.Series.OfType<LineSeries>().Single();
        Assert.Equal(MainAPP.Resources.Strings.M271, line.Title); // 累计占比

        // 所有轴的 Title 与折线/柱的 Title 都不得含错误文案
        var wrongTexts = new[]
        {
            MainAPP.Resources.Strings.M209, // 报警次数
            MainAPP.Resources.Strings.M210, // 报警时长(分钟)
            MainAPP.Resources.Strings.M211, // 停机时长(分钟)
            MainAPP.Resources.Strings.M212, // 速度(件/小时)
        };
        var allTitles = chart.Axes.Select(a => a.Title)
            .Concat(chart.Series.Select(s => s.Title))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        Assert.DoesNotContain(allTitles, t => wrongTexts.Contains(t));
    }

    /// <summary>
    /// 回归测试（2026-08-11 柱内标签统一）：柱标签必须是柱内 + 深色文字（与复盘页帕累托一致），
    /// 且不再叠加柱顶 TextAnnotation。
    /// </summary>
    [Fact]
    public void BuildDefectBarChart_BarLabelsInside_DarkText()
    {
        var chart = ChartService.BuildDefectBarChart([("划痕", 12), ("毛刺", 8)]);
        Assert.NotNull(chart);

        var bar = chart!.Series.OfType<BarSeries>().Single();
        Assert.Equal("{0:N0}", bar.LabelFormatString);
        Assert.Equal(OxyPlot.Series.LabelPlacement.Inside, bar.LabelPlacement);
        Assert.Equal(OxyColor.FromRgb(0x1A, 0x20, 0x29), bar.TextColor);

        // 累计百分比点标签仍在（% 结尾的 TextAnnotation）
        Assert.Contains(chart.Annotations, a => a is OxyPlot.Annotations.TextAnnotation ta
            && ta.Text != null && ta.Text.EndsWith("%"));
        // 柱顶数字标签（纯数字 TextAnnotation，原 AddBarLabels 方式）已移除
        Assert.DoesNotContain(chart.Annotations, a => a is OxyPlot.Annotations.TextAnnotation ta
            && ta.Text != null && ta.Text.All(char.IsDigit));
    }

    [Fact]
    public void BuildHomeDefectParetoChart_Empty_ReturnsNull()
    {
        Assert.Null(ChartService.BuildHomeDefectParetoChart([]));
    }

    [Fact]
    public void BuildHomeDefectParetoChart_VerticalBarsAndCumulativeLine()
    {
        var rows = new[]
        {
            new HomeDefectTopItem { Name = "划痕", Count = 12, CumulativeShare = 0.60, Severity = DefectSeverity.Critical },
            new HomeDefectTopItem { Name = "毛刺", Count = 8, CumulativeShare = 1.00, Severity = DefectSeverity.Minor },
        };
        var chart = ChartService.BuildHomeDefectParetoChart(rows);
        Assert.NotNull(chart);

        var cat = chart!.Axes.OfType<CategoryAxis>().Single();
        Assert.Equal(AxisPosition.Bottom, cat.Position);

        var bars = Assert.Single(chart.Series.OfType<BarSeries>());
        Assert.Equal(2, bars.Items.Count);
        Assert.Equal(12, bars.Items[0].Value);

        var line = Assert.Single(chart.Series.OfType<LineSeries>());
        Assert.Equal(MainAPP.Resources.Strings.M271, line.Title);
        Assert.Equal(2, line.Points.Count);
        Assert.Equal(60, line.Points[0].Y, precision: 6);
        Assert.Equal(100, line.Points[1].Y, precision: 6);

        var pct = chart.Axes.OfType<LinearAxis>().Single(a => a.Position == AxisPosition.Right);
        Assert.Equal(0, pct.Minimum);
        Assert.Equal(100, pct.Maximum);
    }

    // ═══════════════ BuildWorkOrderGanttChart（工单排程甘特）═══════════════

    private static (Device D1, Device D2) SampleDevices() => (
        new Device { Id = "dev-1", Name = "注塑机A1" },
        new Device { Id = "dev-2", Name = "焊接机器人B2" });

    private static WorkOrder SampleWorkOrder(
        string no, string deviceId, DateTime start, DateTime end, WorkOrderStatus status)
        => new()
        {
            Id = no.GetHashCode(),
            OrderNo = no,
            DeviceId = deviceId,
            PlannedStart = start,
            PlannedEnd = end,
            Status = status,
        };

    [Fact]
    public void BuildWorkOrderGanttChart_EmptyWorkOrders_ReturnsModelWithDeviceAxis()
    {
        var (d1, d2) = SampleDevices();
        var chart = ChartService.BuildWorkOrderGanttChart([d1, d2], [], new HashSet<int>(), DateTime.Now)!;

        Assert.NotNull(chart);
        var deviceAxis = chart.Axes.OfType<CategoryAxis>().FirstOrDefault();
        Assert.NotNull(deviceAxis);
        Assert.Equal(["注塑机A1", "焊接机器人B2"], deviceAxis!.ItemsSource?.OfType<string>().ToList());
        // 无工单也有"现在"参考线（时间窗口回退 ±12h），但无矩形条
        Assert.Empty(chart.Series.OfType<RectangleBarSeries>().Single().Items);
    }

    [Fact]
    public void BuildWorkOrderGanttChart_RendersBarPerDeviceSlot()
    {
        var (d1, d2) = SampleDevices();
        var now = new DateTime(2026, 1, 1, 8, 0, 0);
        var orders = new[]
        {
            SampleWorkOrder("WO-1", "dev-1", now, now.AddHours(2), WorkOrderStatus.Running),
            SampleWorkOrder("WO-2", "dev-2", now.AddHours(1), now.AddHours(3), WorkOrderStatus.Pending),
        };

        var chart = ChartService.BuildWorkOrderGanttChart([d1, d2], orders, new HashSet<int>(), now)!;

        var bar = chart.Series.OfType<RectangleBarSeries>().Single();
        Assert.Equal(2, bar.Items.Count);
        // 各工单落在对应设备行槽位：dev-1 → 槽 0，dev-2 → 槽 1（CategoryAxis 整数坐标，标签对齐）
        var dev1 = bar.Items.Single(i => Math.Abs((i.Y0 + i.Y1) / 2 - 0) < 1e-9);
        Assert.Equal(0, (dev1.Y0 + dev1.Y1) / 2, 9);
        var dev2 = bar.Items.Single(i => Math.Abs((i.Y0 + i.Y1) / 2 - 1) < 1e-9);
        Assert.Equal(1, (dev2.Y0 + dev2.Y1) / 2, 9);
    }

    [Fact]
    public void BuildWorkOrderGanttChart_InvalidTimeRanges_AreSkipped()
    {
        var (d1, _) = SampleDevices();
        var now = new DateTime(2026, 1, 1, 8, 0, 0);
        var orders = new[]
        {
            // 合法
            SampleWorkOrder("WO-VALID", "dev-1", now, now.AddHours(1), WorkOrderStatus.Pending),
            // PlannedEnd <= PlannedStart → 跳过
            SampleWorkOrder("WO-BAD", "dev-1", now, now, WorkOrderStatus.Pending),
        };

        var chart = ChartService.BuildWorkOrderGanttChart([d1], orders, new HashSet<int>(), now)!;

        var bar = chart.Series.OfType<RectangleBarSeries>().Single();
        Assert.Single(bar.Items);
        Assert.Equal("WO-VALID", chart.Annotations.OfType<OxyPlot.Annotations.TextAnnotation>()
            .Select(a => a.Text).SingleOrDefault()); // 状态待定不标；此处仅验证无异常
    }

    [Fact]
    public void BuildWorkOrderGanttChart_ConflictOrdersGetWarningColor()
    {
        var (d1, _) = SampleDevices();
        var now = new DateTime(2026, 1, 1, 8, 0, 0);
        var orders = new[]
        {
            SampleWorkOrder("WO-A", "dev-1", now, now.AddHours(2), WorkOrderStatus.Running),
            SampleWorkOrder("WO-B", "dev-1", now.AddHours(1), now.AddHours(3), WorkOrderStatus.Pending),
        };
        var conflictIds = new HashSet<int> { orders[0].Id, orders[1].Id };

        var chart = ChartService.BuildWorkOrderGanttChart([d1], orders, conflictIds, now)!;

        var bar = chart.Series.OfType<RectangleBarSeries>().Single();
        Assert.Equal(2, bar.Items.Count);
        // 冲突一律橙色调（0xFB,0xBF,0x24），覆盖 Running 的绿色
        var warning = OxyColor.FromRgb(0xFB, 0xBF, 0x24);
        Assert.All(bar.Items, i => Assert.Equal(warning, i.Color));
    }

    [Fact]
    public void BuildWorkOrderGanttChart_HighlightedWorkOrderUsesBrightColorAndDimsOthers()
    {
        var (d1, _) = SampleDevices();
        var now = new DateTime(2026, 1, 1, 8, 0, 0);
        var orders = new[]
        {
            SampleWorkOrder("WO-A", "dev-1", now, now.AddHours(2), WorkOrderStatus.Running),
            SampleWorkOrder("WO-B", "dev-1", now.AddHours(1), now.AddHours(3), WorkOrderStatus.Pending),
        };
        var highlight = OxyColor.FromRgb(0x38, 0xBD, 0xF8);

        var chart = ChartService.BuildWorkOrderGanttChart(
            [d1], orders, new HashSet<int>(), now, highlightedWorkOrderId: orders[1].Id)!;

        var bar = chart.Series.OfType<RectangleBarSeries>().Single();
        Assert.Equal(2, bar.Items.Count);
        var selected = bar.Items.Single(i => i.Color == highlight);
        var other = bar.Items.Single(i => i.Color != highlight);
        Assert.Equal(highlight, selected.Color);
        Assert.Equal(96, other.Color.A);
    }

    [Fact]
    public void BuildWorkOrderGanttChart_HasNowReferenceLine()
    {
        var (d1, _) = SampleDevices();
        var chart = ChartService.BuildWorkOrderGanttChart(
            [d1], [], new HashSet<int>(), new DateTime(2026, 1, 1, 12, 0, 0))!;

        var nowLine = chart.Annotations.OfType<OxyPlot.Annotations.LineAnnotation>()
            .SingleOrDefault(a => a.Type == OxyPlot.Annotations.LineAnnotationType.Vertical);
        Assert.NotNull(nowLine);
        Assert.Equal(DateTimeAxis.ToDouble(new DateTime(2026, 1, 1, 12, 0, 0)), nowLine!.X);
    }

    /// <summary>孤立工单（设备已删）被安全跳过，不抛异常且不产条。</summary>
    [Fact]
    public void BuildWorkOrderGanttChart_OrphanWorkOrder_SkippedSilently()
    {
        var (d1, _) = SampleDevices();
        var now = new DateTime(2026, 1, 1, 8, 0, 0);
        var orders = new[]
        {
            SampleWorkOrder("WO-ORPHAN", "deleted-device", now, now.AddHours(1), WorkOrderStatus.Pending),
        };

        var chart = ChartService.BuildWorkOrderGanttChart([d1], orders, new HashSet<int>(), now)!;

        var bar = chart.Series.OfType<RectangleBarSeries>().Single();
        Assert.Empty(bar.Items);
    }
}
