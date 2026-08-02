using Kanban.Core.Models;
using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using MainAPP.Models;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace MainAPP.Services;

/// <summary>
/// OxyPlot 图表构建服务，统一深色主题适配。
/// </summary>
public static class ChartService
{
    // ──────────── 主题常量（统一引用 ChartPalette，消除硬编码重复） ────────────

    private static readonly OxyColor _textColor = ChartPalette.Text;
    private static readonly OxyColor _gridColor = ChartPalette.Grid;
    private static readonly OxyColor _runColor = ChartPalette.Run;
    private static readonly OxyColor _alarmColor = ChartPalette.Alarm;
    private static readonly OxyColor _pauseColor = ChartPalette.Pause;
    private static readonly OxyColor _primaryColor = ChartPalette.Ok;
    private static readonly OxyColor _secondaryColor = ChartPalette.Secondary;
    private static readonly OxyColor _idleColor = ChartPalette.Idle;
    private static readonly OxyColor _borderColor = ChartPalette.Border;
    private static readonly OxyColor _axisColor = ChartPalette.Axis;
    private static readonly OxyColor _gridStrongColor = ChartPalette.GridStrong;

    /// <summary>
    /// 创建基础 PlotModel（透明背景、深色文字、深色网格）。
    /// </summary>
    private static PlotModel CreateBaseModel(string? title = null)
    {
        var model = new PlotModel
        {
            Title = title,
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TitleColor = _textColor,
            TextColor = _textColor,
        };
        model.Legends.Add(new Legend
        {
            LegendBackground = ChartPalette.LegendBackground,
            LegendBorder = _gridColor,
            LegendTextColor = _textColor,
            LegendPosition = LegendPosition.TopRight,
            LegendPlacement = LegendPlacement.Outside,
        });
        return model;
    }

    /// <param name="format">
    /// 数值格式化说明符（如 "F2" "P2" "F0"），勿用 "{0:F2}" 包装。
    /// OxyPlot 内部会自行拼接 "{0:" + format + "}"。
    /// </param>
    private static LinearAxis CreateLinearAxis(string title, AxisPosition pos, string? format = null)
    {
        var axis = new LinearAxis
        {
            Title = title,
            Position = pos,
            TicklineColor = _gridColor,
            MajorGridlineColor = _gridColor,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = _gridColor,
            TitleColor = _textColor,
            TextColor = _textColor,
            StringFormat = format,
        };
        return axis;
    }

    private static DateTimeAxis CreateDateTimeAxis(string title, string format = "MM-dd\nHH:mm")
    {
        return new DateTimeAxis
        {
            Title = title,
            Position = AxisPosition.Bottom,
            TicklineColor = _gridColor,
            MajorGridlineColor = _gridColor,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = _gridColor,
            TitleColor = _textColor,
            TextColor = _textColor,
            StringFormat = format,
        };
    }

    private static CategoryAxis CreateCategoryAxis(string title, IEnumerable<string> labels)
    {
        return new CategoryAxis
        {
            Title = title,
            Position = AxisPosition.Bottom,
            TicklineColor = _gridColor,
            MajorGridlineStyle = LineStyle.None,
            AxislineColor = _gridColor,
            TitleColor = _textColor,
            TextColor = _textColor,
            ItemsSource = labels.ToList(),
        };
    }

    private static readonly OxyColor _okFill = ChartPalette.RunFill;
    private static readonly OxyColor _ngFill = ChartPalette.AlarmFill;

    // ════════════════════ Tab 0: 产量趋势堆叠面积图 ════════════════════

    /// <summary>
    /// 构建产量趋势堆叠面积图（OK 绿色底 + NG 红色叠在上方，单Y轴）。
    /// AreaSeries 不支持 IsStacked，通过手工叠加 Y 值实现：NG 的 Y = OK + NG。
    /// </summary>
    public static PlotModel BuildProductionChart(
        IEnumerable<(DateTime Time, int Ok, int Ng)> data)
    {
        var model = CreateBaseModel("产量趋势");
        var list = data.ToList();
        if (list.Count == 0) return model;

        model.Axes.Add(CreateLinearAxis("产量(件)", AxisPosition.Left, "F2"));
        model.Axes.Add(CreateDateTimeAxis("时间"));

        var okSeries = new AreaSeries
        {
            Title = "OK 产量(件)",
            Color = _runColor,
            Fill = _okFill,
            StrokeThickness = 1,
        };

        var ngSeries = new AreaSeries
        {
            Title = "NG 产量(件)",
            Color = _alarmColor,
            Fill = _ngFill,
            StrokeThickness = 1,
        };

        foreach (var (time, ok, ng) in list)
        {
            okSeries.Points.Add(DateTimeAxis.CreateDataPoint(time, ok));
            ngSeries.Points.Add(DateTimeAxis.CreateDataPoint(time, ok + ng));

            AddProductionLabel(model, time, ok / 2.0, ok, "OK");
            AddProductionLabel(model, time, ok + ng / 2.0, ng, "NG");
        }

        model.Series.Add(okSeries);
        model.Series.Add(ngSeries);

        return model;
    }

    private static void AddProductionLabel(PlotModel model, DateTime time, double value, int amount, string prefix)
    {
        if (amount <= 0) return;

        model.Annotations.Add(new TextAnnotation
        {
            Text = $"{prefix} {amount:N0}",
            TextPosition = DateTimeAxis.CreateDataPoint(time, value),
            TextColor = _textColor,
            FontSize = 10,
            Stroke = OxyColors.Transparent,
            Background = OxyColors.Transparent,
            Padding = new OxyThickness(2),
            TextVerticalAlignment = VerticalAlignment.Middle,
            TextHorizontalAlignment = HorizontalAlignment.Center,
        });
    }

    // ════════════════════ Tab 1: 状态时长饼图 ════════════════════

    /// <summary>
    /// 构建状态时长饼图（运行/报警/待机 总时长占比）。
    /// </summary>
    public static PlotModel BuildStatusChart(
        IEnumerable<(DateTime Date, double RunHours, double AlarmHours, double PauseHours)> data)
    {
        var model = CreateBaseModel("状态时长分布");
        var list = data.ToList();
        if (list.Count == 0) return model;

        // 汇总所有天
        var totalRun = list.Sum(d => d.RunHours);
        var totalAlarm = list.Sum(d => d.AlarmHours);
        var totalPause = list.Sum(d => d.PauseHours);

        // 饼图专属配置：使用深色背景内部图例
        model.Legends.Clear();
        model.Legends.Add(new Legend
        {
            LegendBackground = ChartPalette.LegendBackground,
            LegendBorder = _axisColor,
            LegendTextColor = _textColor,
            LegendPosition = LegendPosition.TopRight,
            LegendPlacement = LegendPlacement.Outside,
            LegendFontSize = 13,
            LegendPadding = 8,
            LegendItemSpacing = 6,
        });

        var series = new PieSeries
        {
            InsideLabelFormat = "{1:F2}h",   // 内部：值+单位
            OutsideLabelFormat = "{2:F1}%",  // 外部：百分比
            StrokeThickness = 1,
            Stroke = _borderColor,
            TickLabelDistance = 8,
            Slices =
            {
                // 用纯名称作为 Label（图例自动按 Label 渲染）
                new PieSlice("运行", totalRun) { Fill = _runColor },
                new PieSlice("报警", totalAlarm) { Fill = _alarmColor },
                new PieSlice("待机", totalPause) { Fill = _pauseColor },
            },
        };
        model.Series.Add(series);

        return model;
    }

    /// <summary>
    /// 按天堆叠柱状图（运行/报警/待机每天分布）。
    /// </summary>
    public static PlotModel BuildStatusBarChart(
        IEnumerable<(DateTime Date, double RunHours, double AlarmHours, double PauseHours)> data)
    {
        var model = CreateBaseModel("每日状态时长");
        var list = data.ToList();
        if (list.Count == 0) return model;

        var catAxis = CreateCategoryAxis("", list.Select(d => d.Date.ToString("MM-dd")));
        catAxis.Key = "sBarCat";
        model.Axes.Add(catAxis);

        var valAxis = CreateLinearAxis("小时", AxisPosition.Left, "F2");
        valAxis.Key = "sBarVal";
        model.Axes.Add(valAxis);

        BarSeries MakeBar(string title, OxyColor fill)
        {
            return new BarSeries
            {
                Title = title,
                FillColor = fill,
                StrokeColor = fill,
                IsStacked = true,
                XAxisKey = "sBarVal",
                YAxisKey = "sBarCat",
            };
        }

        var run = MakeBar("运行", _runColor);
        var alarm = MakeBar("报警", _alarmColor);
        var pause = MakeBar("待机", _pauseColor);
        foreach (var d in list)
        {
            run.Items.Add(new BarItem { Value = d.RunHours });
            alarm.Items.Add(new BarItem { Value = d.AlarmHours });
            pause.Items.Add(new BarItem { Value = d.PauseHours });
        }
        model.Series.Add(run);
        model.Series.Add(alarm);
        model.Series.Add(pause);

        // 堆叠柱标签：只在堆叠顶部（pause 柱顶）显示总时长，避免每段都标导致拥挤
        var alarmBase = AccumulateStackBase(run, null); // alarm 基线 = run 值
        var pauseBase = AccumulateStackBase(alarm, alarmBase); // pause 基线 = run + alarm
        AddBarLabels(model, pause, BarOrientation.Vertical, "sBarCat", "sBarVal", pauseBase, "F2");

        return model;
    }

    /// <summary>
    /// 状态甘特图：横轴时间、纵轴状态（运行/报警/待机），每段画矩形色条。
    /// 期望输入 (Start, End, State) 三段式数据。
    /// </summary>
    public static PlotModel BuildStatusGanttChart(
        IEnumerable<(DateTime Start, DateTime End, int State)> segments)
    {
        var model = CreateBaseModel("状态时间线");
        var list = segments.Where(s => s.State >= 1 && s.State <= 3).ToList();
        if (list.Count == 0) return model;

        model.Axes.Add(CreateDateTimeAxis("时间"));

        var stateAxis = new LinearAxis
        {
            Title = "状态",
            Position = AxisPosition.Left,
            Minimum = 0.5,
            Maximum = 3.5,
            MajorStep = 1,
            TicklineColor = _gridColor,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.None,
            AxislineColor = _gridColor,
            TitleColor = _textColor,
            TextColor = _textColor,
            LabelFormatter = v => v switch
            {
                1 => "运行",
                2 => "报警",
                3 => "待机",
                _ => ""
            },
        };
        model.Axes.Add(stateAxis);

        var series = new RectangleBarSeries
        {
            Title = "状态",
        };

        foreach (var (start, end, state) in list)
        {
            if (end <= start) continue;
            var color = state switch
            {
                1 => _runColor,
                2 => _alarmColor,
                3 => _pauseColor,
                _ => OxyColors.Gray,
            };
            series.Items.Add(new RectangleBarItem(
                DateTimeAxis.ToDouble(start),
                state - 0.35,
                DateTimeAxis.ToDouble(end),
                state + 0.35)
            { Color = color });

            // 短段（< 5 分钟）不显示标签，避免文字溢出
            var duration = end - start;
            if (duration.TotalMinutes >= 5)
            {
                var midX = DateTimeAxis.ToDouble(start.Add(duration / 2));
                var label = duration.TotalHours >= 1
                    ? $"{duration.TotalHours:F1}h"
                    : $"{duration.TotalMinutes:F0}m";
                model.Annotations.Add(new TextAnnotation
                {
                    Text = label,
                    TextPosition = new DataPoint(midX, state),
                    TextColor = OxyColors.White,
                    FontSize = 11,
                    Stroke = OxyColors.Transparent,
                    Background = OxyColors.Transparent,
                    Padding = new OxyThickness(2),
                    TextVerticalAlignment = VerticalAlignment.Middle,
                    TextHorizontalAlignment = HorizontalAlignment.Center,
                });
            }
        }
        model.Series.Add(series);

        return model;
    }

    // ════════════════════ Tab 2: 报警频次横向柱状图 ════════════════════

    /// <summary>
    /// 构建报警频次横向柱状图（按报警名称聚合触发次数）。
    /// </summary>
    public static PlotModel BuildAlarmChart(
        IEnumerable<(string AlarmName, int TriggerCount, double AvgDurationMin)> data)
    {
        var model = CreateBaseModel("报警频次统计");
        var list = data.ToList();
        if (list.Count == 0) return model;

        // CategoryAxis 放在 Y 轴（横向柱状图）
        var catAxis = new CategoryAxis
        {
            Title = "报警名称",
            Position = AxisPosition.Left,
            TicklineColor = _gridColor,
            MajorGridlineStyle = LineStyle.None,
            AxislineColor = _gridColor,
            TitleColor = _textColor,
            TextColor = _textColor,
            ItemsSource = list.Select(d => d.AlarmName).ToList(),
        };
        catAxis.Key = "alarmCat";
        model.Axes.Add(catAxis);
        var alarmValAxis = CreateLinearAxis("次数", AxisPosition.Bottom, "F2");
        alarmValAxis.Key = "alarmVal";
        model.Axes.Add(alarmValAxis);

        var countSeries = new BarSeries
        {
            Title = "触发次数",
            FillColor = _alarmColor,
            StrokeColor = _alarmColor,
            XAxisKey = "alarmVal",
            YAxisKey = "alarmCat",
        };
        var durSeries = new BarSeries
        {
            Title = "平均时长(min)",
            FillColor = _pauseColor,
            StrokeColor = _pauseColor,
            XAxisKey = "alarmVal",
            YAxisKey = "alarmCat",
        };

        foreach (var d in list)
        {
            countSeries.Items.Add(new BarItem { Value = d.TriggerCount });
            durSeries.Items.Add(new BarItem { Value = d.AvgDurationMin });
        }

        model.Series.Add(countSeries);
        model.Series.Add(durSeries);

        // 横向柱标签：次数（整数）+ 平均时长（2 位小数）
        AddBarLabels(model, countSeries, BarOrientation.Horizontal, "alarmCat", "alarmVal", null, "F0");
        AddBarLabels(model, durSeries, BarOrientation.Horizontal, "alarmCat", "alarmVal", null, "F2");

        return model;
    }

    // ════════════════════ Tab 3: OEE 指标柱状图 + 目标线 ════════════════════

    /// <summary>
    /// 构建 OEE 指标柱状图（合格率/性能率/可用率/OEE + 85% 目标线）。
    /// OxyPlot 2.2 中纵向柱通过 BarSeries + 显式 YAxisKey/XAxisKey 实现。
    /// </summary>
    public static PlotModel BuildOeeChart(double quality, double performance, double availability, double oee, double target = KpiThresholds.OeeGood)
    {
        var model = CreateBaseModel("OEE 指标");

        var categoryAxis = CreateCategoryAxis("", new[] { "C良品率", "B性能达标率", "A时间稼动率", "OEE" });
        categoryAxis.Key = "xCategory";
        model.Axes.Add(categoryAxis);

        var valAxis = CreateLinearAxis("百分比", AxisPosition.Left, "P2");
        valAxis.Key = "xValue";
        valAxis.Minimum = 0;
        valAxis.Maximum = 1;
        model.Axes.Add(valAxis);

        var series = new BarSeries
        {
            Title = "实际值",
            XAxisKey = "xValue",
            YAxisKey = "xCategory",
        };
        series.Items.Add(new BarItem { Value = quality, Color = _runColor });
        series.Items.Add(new BarItem { Value = performance, Color = _primaryColor });
        series.Items.Add(new BarItem { Value = availability, Color = _secondaryColor });
        series.Items.Add(new BarItem { Value = oee, Color = ChartPalette.Oee });
        model.Series.Add(series);

        // 目标线（横向虚线，LineSeries 默认 X→X轴 Y→Y轴）
        var targetSeries = new LineSeries
        {
            Title = $"目标 {target:P2}",
            Color = _alarmColor,
            StrokeThickness = 2,
            LineStyle = LineStyle.Dash,
        };
        targetSeries.Points.Add(new DataPoint(-0.5, target));
        targetSeries.Points.Add(new DataPoint(3.5, target));
        model.Series.Add(targetSeries);

        // 柱子顶部数值标签（百分比格式 P1）
        AddBarLabels(model, series, BarOrientation.Vertical, "xCategory", "xValue", null, "P1");

        return model;
    }

    /// <summary>
    /// OEE 趋势折线图：按时间排列每个班次的 OEE 值。
    /// </summary>
    public static PlotModel BuildOeeTrendChart(
        IEnumerable<(DateTime ShiftTime, double Oee, string ShiftName)> data, double target = 0.85)
    {
        var model = CreateBaseModel("OEE 趋势");
        var list = data.OrderBy(d => d.ShiftTime).ToList();
        if (list.Count == 0) return model;

        model.Axes.Add(CreateDateTimeAxis("班次时间", "MM-dd HH:mm"));
        var yAxis = CreateLinearAxis("OEE", AxisPosition.Left, "P2");
        yAxis.Minimum = 0;
        yAxis.Maximum = 1;
        model.Axes.Add(yAxis);

        var series = new LineSeries
        {
            Title = "OEE",
            Color = _primaryColor,
            StrokeThickness = 2.5,
            MarkerType = MarkerType.Circle,
            MarkerSize = 6,
            MarkerFill = _primaryColor,
            MarkerStroke = _primaryColor,
        };
        foreach (var d in list)
            series.Points.Add(DateTimeAxis.CreateDataPoint(d.ShiftTime, d.Oee));

        model.Series.Add(series);

        // 数据点标签：每个点上方显示 OEE 百分比
        foreach (var d in list)
        {
            model.Annotations.Add(new TextAnnotation
            {
                Text = d.Oee.ToString("P1"),
                TextPosition = DateTimeAxis.CreateDataPoint(d.ShiftTime, d.Oee),
                TextColor = _textColor,
                FontSize = 11,
                Stroke = OxyColors.Transparent,
                Background = OxyColors.Transparent,
                Padding = new OxyThickness(2),
                TextVerticalAlignment = VerticalAlignment.Bottom,
                TextHorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        // 目标线
        var targetLine = new LineSeries
        {
            Title = $"目标 {target:P0}",
            Color = _alarmColor,
            StrokeThickness = 1.5,
            LineStyle = LineStyle.Dash,
        };
        targetLine.Points.Add(new DataPoint(DateTimeAxis.ToDouble(list.First().ShiftTime), target));
        targetLine.Points.Add(new DataPoint(DateTimeAxis.ToDouble(list.Last().ShiftTime), target));
        model.Series.Add(targetLine);

        return model;
    }

    /// <summary>
    /// 班次对比柱状图：按班次名称聚合 OEE 均值。
    /// </summary>
    public static PlotModel BuildOeeShiftBarChart(
        IEnumerable<(string ShiftName, double Oee)> data, double target = KpiThresholds.OeeGood)
    {
        var model = CreateBaseModel("班次 OEE 对比");
        var list = data.GroupBy(d => d.ShiftName)
            .Select(g => (ShiftName: g.Key, Oee: g.Average(d => d.Oee)))
            .OrderBy(d => d.ShiftName)
            .ToList();
        if (list.Count == 0) return model;

        var catAxis = CreateCategoryAxis("", list.Select(d => d.ShiftName));
        catAxis.Key = "cat";
        model.Axes.Add(catAxis);

        var yAxis = CreateLinearAxis("OEE", AxisPosition.Left, "P2");
        yAxis.Key = "val";
        yAxis.Minimum = 0;
        yAxis.Maximum = 1;
        model.Axes.Add(yAxis);

        var series = new BarSeries
        {
            Title = "OEE",
            FillColor = _primaryColor,
            StrokeColor = _primaryColor,
            XAxisKey = "val",
            YAxisKey = "cat",
        };
        foreach (var d in list)
            series.Items.Add(new BarItem { Value = d.Oee });

        model.Series.Add(series);

        // 柱子顶部数值标签（百分比格式 P1）
        AddBarLabels(model, series, BarOrientation.Vertical, "cat", "val", null, "P1");

        return model;
    }

    // ──────────── 从 HomeViewModel 迁入的图表（消除 ViewModel 内联图表构建） ────────────

    /// <summary>
    /// 构建状态时长饼图：运行 / 报警 / 待机（迁自 HomeViewModel.BuildStatusPieChart）。
    /// 参数均为秒。total &lt;= 0 时返回灰色占位饼图（"初始"），保证未连接 PLC 时图表仍渲染。
    /// </summary>
    public static PlotModel BuildStatusPieChart(double runTime, double alarmTime, double pausedTime)
    {
        var total = runTime + alarmTime + pausedTime;
        var model = CreateBaseModel();
        var series = new PieSeries
        {
            InsideLabelFormat = "", OutsideLabelFormat = "{2:F0}%",
            StrokeThickness = 1, Stroke = _borderColor,
        };
        if (total <= 0)
        {
            // 无数据占位：单个灰色扇区，不显示标签避免与图例重复
            series.InsideLabelFormat = "";
            series.OutsideLabelFormat = "";
            series.Slices.Add(new PieSlice("初始", 1) { Fill = _idleColor });
        }
        else
        {
            series.Slices.Add(new PieSlice("运行", runTime) { Fill = _runColor });
            series.Slices.Add(new PieSlice("报警", alarmTime) { Fill = _alarmColor });
            series.Slices.Add(new PieSlice("待机", pausedTime) { Fill = _pauseColor });
        }
        model.Series.Add(series);
        return model;
    }

    /// <summary>
    /// 构建设备明细使用的紧凑横向状态占比条，仅保留运行/报警/待机颜色，不绘制标签。
    /// </summary>
    public static PlotModel BuildStatusDistributionBarChart(double runTime, double alarmTime, double pausedTime)
    {
        var model = CreateBaseModel();
        model.Legends.Clear();
        var total = runTime + alarmTime + pausedTime;
        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Left,
            IsAxisVisible = false,
            ItemsSource = new[] { "状态" },
        };
        var valueAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom,
            IsAxisVisible = false,
            Minimum = 0,
            Maximum = Math.Max(total, 1),
            MinimumPadding = 0,
            MaximumPadding = 0,
        };
        model.Axes.Add(categoryAxis);
        model.Axes.Add(valueAxis);

        BarSeries CreateSegment(OxyColor color, double value)
        {
            var series = new BarSeries
            {
                IsStacked = true,
                XAxisKey = valueAxis.Key,
                YAxisKey = categoryAxis.Key,
                FillColor = color,
                StrokeColor = color,
                BarWidth = 0.72,
            };
            series.Items.Add(new BarItem { Value = value });
            return series;
        }

        if (total <= 0)
        {
            model.Series.Add(CreateSegment(_idleColor, 1));
        }
        else
        {
            model.Series.Add(CreateSegment(_runColor, runTime));
            model.Series.Add(CreateSegment(_alarmColor, alarmTime));
            model.Series.Add(CreateSegment(_pauseColor, pausedTime));
        }
        return model;
    }

    /// <summary>
    /// 构建单值环形图：value 部分用指定颜色，剩余部分用深灰背景。
    /// 用于 OEE 预览的 4 个独立环图（可用率/性能率/合格率/OEE）。
    /// value 会被 clamp 到 [0, 1]。无图例，中心留空由 UI 叠加文字。
    /// </summary>
    private static PlotModel BuildRingChart(double value, OxyColor color)
    {
        var model = CreateBaseModel();
        model.Legends.Clear();
        var v = Math.Clamp(value, 0, 1);
        var series = new PieSeries
        {
            InsideLabelFormat = "",
            OutsideLabelFormat = "",
            StrokeThickness = 1,
            Stroke = _borderColor,
            InnerDiameter = 0.62,
        };
        if (v <= 0)
        {
            series.Slices.Add(new PieSlice("", 1) { Fill = _idleColor });
        }
        else if (v >= 1)
        {
            series.Slices.Add(new PieSlice("", 1) { Fill = color });
        }
        else
        {
            series.Slices.Add(new PieSlice("", v) { Fill = color });
            series.Slices.Add(new PieSlice("", 1 - v) { Fill = ChartPalette.GridStrong });
        }
        model.Series.Add(series);
        return model;
    }

    /// <summary>OEE 综合指标环图（主色蓝紫）</summary>
    public static PlotModel BuildOeeRing(double oee) => BuildRingChart(oee, _primaryColor);

    /// <summary>可用率环图（绿）</summary>
    public static PlotModel BuildAvailabilityRing(double availability) => BuildRingChart(availability, _runColor);

    /// <summary>性能率环图（黄）</summary>
    public static PlotModel BuildPerformanceRing(double performance) => BuildRingChart(performance, _pauseColor);

    /// <summary>合格率环图（浅紫）</summary>
    public static PlotModel BuildQualityRing(double quality) => BuildRingChart(quality, _secondaryColor);

    /// <summary>
    /// 构建合格率饼图：OK / NG 数量占比。
    /// total &lt;= 0 时返回灰色占位环形图（"无数据"），保证未连接 PLC 时图表仍渲染。
    /// </summary>
    public static PlotModel BuildQualityPieChart(int okCount, int ngCount)
    {
        var total = okCount + ngCount;
        var model = CreateBaseModel(null!);
        model.Title = null;

        var series = new PieSeries
        {
            InsideLabelFormat = "",
            OutsideLabelFormat = "",
            StrokeThickness = 1,
            Stroke = _borderColor,
            InnerDiameter = 0.55,
        };

        if (total <= 0)
        {
            // 无数据占位：单个灰色扇区
            series.Slices.Add(new PieSlice("无数据", 1) { Fill = _idleColor });
        }
        else
        {
            // 百分比并入图例 Label，避免小扇区文字拥挤
            string Pct(int v) => $"{(double)v / total * 100:F1}%";
            series.Slices.Add(new PieSlice($"OK ({Pct(okCount)})", okCount) { Fill = _runColor });
            series.Slices.Add(new PieSlice($"NG ({Pct(ngCount)})", ngCount) { Fill = _alarmColor });
        }

        model.Series.Add(series);
        return model;
    }

    /// <summary>
    /// 构建缺陷帕累托图：TOP5 缺陷柱状图（左 Y 轴·数量）+ 累计百分比折线（右次轴·0-100%）。
    /// 无缺陷或全为 0 时返回 null。帕累托原则：聚焦贡献最大的少数缺陷。
    /// </summary>
    public static PlotModel? BuildDefectBarChart(IEnumerable<(string Name, int Count)> defects)
    {
        var list = defects
            .Where(d => d.Count > 0)
            .OrderByDescending(d => d.Count)
            .Take(5)
            .ToList();
        if (list.Count == 0) return null;

        var total = list.Sum(d => d.Count);
        var model = CreateBaseModel();

        var catAxis = CreateCategoryAxisCustom("defectCat", list.Select(d => d.Name));
        catAxis.Title = "缺陷类型";
        catAxis.Angle = -30;
        model.Axes.Add(catAxis);

        var valAxis = CreateLinearAxis("数量(件)", AxisPosition.Left);
        valAxis.Key = "defectVal";
        valAxis.MinimumPadding = 0;
        model.Axes.Add(valAxis);

        // 右次轴：累计百分比（0-100，与左轴数量独立刻度）
        var pctAxis = new LinearAxis
        {
            Title = "累计占比",
            Position = AxisPosition.Right,
            Key = "defectPct",
            Minimum = 0,
            Maximum = 100,
            MinimumPadding = 0,
            MaximumPadding = 0,
            TextColor = _textColor,
            TicklineColor = _axisColor,
            MajorGridlineStyle = LineStyle.None,
            AxislineColor = _axisColor,
            LabelFormatter = v => $"{v:F0}%",
        };
        model.Axes.Add(pctAxis);

        var barSeries = new BarSeries
        {
            Title = "数量",
            FillColor = ChartPalette.Base,
            StrokeColor = ChartPalette.Base,
            StrokeThickness = 1,
            XAxisKey = "defectVal",
            YAxisKey = "defectCat",
        };
        foreach (var d in list)
            barSeries.Items.Add(new BarItem { Value = d.Count });
        model.Series.Add(barSeries);

        // 帕累托曲线：累计百分比，X 用类别索引，Y 用百分比绑定右次轴
        var cumSeries = new LineSeries
        {
            Title = "累计占比",
            Color = ChartPalette.Pause,
            StrokeThickness = 2,
            MarkerType = MarkerType.Circle,
            MarkerSize = 5,
            MarkerFill = ChartPalette.Pause,
            XAxisKey = "defectCat",
            YAxisKey = "defectPct",
        };
        double cum = 0;
        for (int i = 0; i < list.Count; i++)
        {
            cum += list[i].Count;
            var pct = cum * 100.0 / total;
            cumSeries.Points.Add(new DataPoint(i, pct));

            // 折线点标签：显示累计百分比
            model.Annotations.Add(new TextAnnotation
            {
                Text = $"{pct:F0}%",
                TextPosition = new DataPoint(i, pct),
                TextColor = ChartPalette.Pause,
                FontSize = 11,
                Stroke = OxyColors.Transparent,
                Background = OxyColors.Transparent,
                Padding = new OxyThickness(2),
                TextVerticalAlignment = VerticalAlignment.Bottom,
                TextHorizontalAlignment = HorizontalAlignment.Center,
                XAxisKey = "defectCat",
                YAxisKey = "defectPct",
            });
        }
        model.Series.Add(cumSeries);

        // 柱子顶部数值标签（整数件数）
        AddBarLabels(model, barSeries, BarOrientation.Vertical, "defectCat", "defectVal", null, "F0");

        return model;
    }

    /// <summary>
    /// 创建分类轴辅助（迁自 HomeViewModel 内联轴构建，消除颜色硬编码重复）。
    /// </summary>
    private static CategoryAxis CreateCategoryAxisCustom(string key, IEnumerable<string> labels)
    {
        var axis = new CategoryAxis
        {
            Key = key,
            TextColor = _textColor,
            TicklineColor = _axisColor,
            MajorGridlineColor = _gridStrongColor,
            MajorGridlineStyle = LineStyle.Solid,
            AxislineColor = _axisColor,
        };
        foreach (var label in labels)
            axis.Labels.Add(label);
        return axis;
    }

    // ──────────── 柱子数值标签辅助（OxyPlot 2.2 BarSeries 无 LabelFormat，用 TextAnnotation 实现） ────────────

    /// <summary>
    /// 标签位置：纵向柱（CategoryAxis 在底部 X 轴，值在 Y 轴）或横向柱（CategoryAxis 在左侧 Y 轴，值在 X 轴）。
    /// </summary>
    private enum BarOrientation { Vertical, Horizontal }

    /// <summary>
    /// 为 BarSeries 的每个柱子添加数值标签（TextAnnotation）。
    /// 纵向柱：标签在柱子顶部上方；横向柱：标签在柱子右端右侧。
    /// 堆叠柱：标签放在堆叠顶部（topValue = baseValue + value）。
    /// </summary>
    /// <param name="model">目标 PlotModel</param>
    /// <param name="series">目标 BarSeries（需已添加 Items）</param>
    /// <param name="orientation">柱子方向</param>
    /// <param name="catAxisKey">CategoryAxis 的 Key（类别索引轴）</param>
    /// <param name="valAxisKey">LinearAxis 的 Key（数值轴）</param>
    /// <param name="baseValues">堆叠基线（每柱之前的累计值，null 表示非堆叠从 0 开始）</param>
    /// <param name="format">数值格式（如 "F2" "P1" "F0"）</param>
    private static void AddBarLabels(
        PlotModel model,
        BarSeries series,
        BarOrientation orientation,
        string catAxisKey,
        string valAxisKey,
        double[]? baseValues,
        string format)
    {
        for (int i = 0; i < series.Items.Count; i++)
        {
            var item = series.Items[i];
            if (item.Value == 0) continue; // 0 值不显示标签避免噪音

            var baseVal = baseValues != null && i < baseValues.Length ? baseValues[i] : 0;
            var topVal = baseVal + item.Value;
            var label = topVal.ToString(format);

            var annot = new TextAnnotation
            {
                Text = label,
                TextColor = _textColor,
                FontSize = 12,
                Stroke = OxyColors.Transparent,
                Background = OxyColors.Transparent,
                Padding = new OxyThickness(2),
                XAxisKey = orientation == BarOrientation.Vertical ? catAxisKey : valAxisKey,
                YAxisKey = orientation == BarOrientation.Vertical ? valAxisKey : catAxisKey,
            };

            if (orientation == BarOrientation.Vertical)
            {
                // 纵向柱：X=类别索引，Y=柱顶值，文字在柱顶上方
                annot.TextPosition = new DataPoint(i, topVal);
                annot.TextVerticalAlignment = VerticalAlignment.Bottom;
                annot.TextHorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                // 横向柱：X=柱右端值，Y=类别索引，文字在柱右右侧
                annot.TextPosition = new DataPoint(topVal, i);
                annot.TextVerticalAlignment = VerticalAlignment.Middle;
                annot.TextHorizontalAlignment = HorizontalAlignment.Left;
            }

            model.Annotations.Add(annot);
        }
    }

    /// <summary>
    /// 计算堆叠柱的累计基线（每柱之前的累计值），用于堆叠柱状图标签定位。
    /// 返回每柱的基线数组；多个 series 按顺序累加。
    /// </summary>
    private static double[] AccumulateStackBase(BarSeries series, double[]? previousBase)
    {
        var result = new double[series.Items.Count];
        for (int i = 0; i < series.Items.Count; i++)
        {
            var prev = previousBase != null && i < previousBase.Length ? previousBase[i] : 0;
            result[i] = prev + series.Items[i].Value;
        }
        return result;
    }

    // ──────────── 设备详情页：缺陷占比饼图 + 按小时产量柱状图 ────────────

    /// <summary>
    /// 构建缺陷占比饼图：各缺陷类型按数量占比。
    /// 全部为 0 或空集合时返回 null（由调用方显示空状态）。
    /// </summary>
    public static PlotModel? BuildDefectPieChart(IEnumerable<(string Name, int Count)> defects)
    {
        var list = defects
            .Where(d => d.Count > 0)
            .OrderByDescending(d => d.Count)
            .ToList();
        if (list.Count == 0) return null;

        var total = list.Sum(d => d.Count);
        var model = CreateBaseModel(null!);
        model.Title = null;

        // 调色板：为每个缺陷分配不同色相，最多 8 种循环
        var palette = new[]
        {
            _alarmColor, _pauseColor, _primaryColor, _secondaryColor,
            OxyColor.FromRgb(0x2D, 0xD4, 0xBF), OxyColor.FromRgb(0xFB, 0x92, 0x3C),
            OxyColor.FromRgb(0xA7, 0x8B, 0xFA), OxyColor.FromRgb(0x4D, 0xE0, 0x8C),
        };

        var series = new PieSeries
        {
            InsideLabelFormat = "",
            OutsideLabelFormat = "{2:F1}%",
            StrokeThickness = 1,
            Stroke = _borderColor,
            InnerDiameter = 0.5,
            TickLabelDistance = 6,
        };

        for (int i = 0; i < list.Count; i++)
        {
            var pct = (double)list[i].Count / total * 100;
            series.Slices.Add(new PieSlice(
                $"{list[i].Name} ({pct:F1}%)",
                list[i].Count)
            { Fill = palette[i % palette.Length] });
        }

        model.Series.Add(series);
        return model;
    }

    /// <summary>
    /// 构建按小时产量柱状图（OK 绿色 + NG 红色堆叠）。
    /// buckets 为桶起始时间数组，okCounts/ngCounts 为对应增量产量。
    /// 数据全为 0 时仍返回空轴图表（由调用方决定是否显示空状态）。
    /// </summary>
    public static PlotModel BuildHourlyProductionBarChart(
        DateTime[] buckets, int[] okCounts, int[] ngCounts, int targetCycle = 0)
    {
        var model = CreateBaseModel("按小时产量");

        var catLabels = buckets.Select(b => b.ToString("HH:mm")).ToList();
        var catAxis = CreateCategoryAxis("时间", catLabels);
        catAxis.Key = "hourCat";
        catAxis.Angle = -45;
        model.Axes.Add(catAxis);

        var valAxis = CreateLinearAxis("产量(件)", AxisPosition.Left, "F0");
        valAxis.Key = "hourVal";
        model.Axes.Add(valAxis);

        List<BarItem> okItems = [];
        List<BarItem> ngItems = [];
        for (int i = 0; i < buckets.Length; i++)
        {
            var ok = i < okCounts.Length ? Math.Max(0, okCounts[i]) : 0;
            var ng = i < ngCounts.Length ? Math.Max(0, ngCounts[i]) : 0;
            okItems.Add(new BarItem { Value = ok });
            ngItems.Add(new BarItem { Value = ng });
        }

        var okSeries = new BarSeries
        {
            Title = "OK",
            FillColor = _runColor,
            StrokeColor = _runColor,
            IsStacked = true,
            XAxisKey = "hourVal",
            YAxisKey = "hourCat",
            ItemsSource = okItems,
        };
        var ngSeries = new BarSeries
        {
            Title = "NG",
            FillColor = _alarmColor,
            StrokeColor = _alarmColor,
            IsStacked = true,
            XAxisKey = "hourVal",
            YAxisKey = "hourCat",
            ItemsSource = ngItems,
        };
        model.Series.Add(okSeries);
        model.Series.Add(ngSeries);

        if (targetCycle > 0)
        {
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = targetCycle,
                Color = _secondaryColor,
                LineStyle = LineStyle.Dash,
                StrokeThickness = 2,
                Text = $"目标 {targetCycle} 件/h",
                TextOrientation = AnnotationTextOrientation.Horizontal,
                TextVerticalAlignment = VerticalAlignment.Top,
            });
        }

        // 堆叠柱标签：只在 NG 柱顶显示 OK+NG 合计
        var ngBase = AccumulateStackBase(okSeries, null);
        AddBarLabels(model, ngSeries, BarOrientation.Vertical, "hourCat", "hourVal", ngBase, "F0");

        return model;
    }
}
