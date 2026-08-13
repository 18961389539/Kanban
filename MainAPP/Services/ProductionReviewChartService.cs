using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.ViewModels;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using MainAPP.Resources;

namespace MainAPP.Services;

public interface IProductionReviewChartService
{
    PlotModel BuildTrend(
        DateTime[] buckets,
        int[] okCounts,
        int[] ngCounts,
        ProductionReviewBucketSize bucketSize,
        double targetCycle,
        int deviceCount,
        IReadOnlyList<ShiftConfig> shifts,
        OverviewChartPalette palette);

    PlotModel BuildOeeWaterfall(double performance, double availability, double quality, double oee,
        OverviewChartPalette palette);

    /// <summary>缺陷帕累托双轴图：柱=新增数量（左轴），折线=累计占比（右轴 0-100%）。</summary>
    PlotModel BuildDefectParetoChart(
        IReadOnlyList<DefectParetoSummary> defects,
        OverviewChartPalette palette);

    PlotModel BuildHeatmap(
        DateTime[] buckets,
        IReadOnlyList<ProductionReviewHeatmapRow> rows,
        ProductionReviewBucketSize bucketSize,
        OverviewChartPalette palette);
}

public sealed record OverviewChartPalette(
    OxyColor Text,
    OxyColor Grid,
    OxyColor Ok,
    OxyColor Ng,
    OxyColor ShiftBackground,
    OxyColor Base,
    OxyColor Pause);

public sealed record ProductionReviewHeatmapRow(
    string DeviceName,
    IReadOnlyList<int> HourlyOk);

public sealed class ProductionReviewChartService : IProductionReviewChartService
{
    public PlotModel BuildTrend(
        DateTime[] buckets,
        int[] okCounts,
        int[] ngCounts,
        ProductionReviewBucketSize bucketSize,
        double targetCycle,
        int deviceCount,
        IReadOnlyList<ShiftConfig> shifts,
        OverviewChartPalette palette)
    {
        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = palette.Text,
            // 中文渲染：SkiaSharp PngExporter 无字体回退，Segoe UI 会把中文画成"口"（P2-11）。
            DefaultFont = ChineseFontFamily,
        };
        model.Legends.Add(new Legend
        {
            // 图例移入图内右上（半透明底）：240px 高度下外侧图例挤压绘图区（P2-7）
            LegendBackground = OxyColor.FromArgb(160, 0x1A, 0x20, 0x29),
            LegendBorder = palette.Grid,
            LegendTextColor = palette.Text,
            LegendPosition = LegendPosition.TopRight,
            LegendPlacement = LegendPlacement.Inside,
        });
        // 桶跨度（提前到 xAxis 创建之前，用于 X 轴 Minimum/Maximum 兜底）
        var spanTicks = bucketSize switch
        {
            ProductionReviewBucketSize.Minute5 => TimeSpan.FromMinutes(5).Ticks,
            ProductionReviewBucketSize.Hour => TimeSpan.FromHours(1).Ticks,
            ProductionReviewBucketSize.Day => TimeSpan.FromDays(1).Ticks,
            _ => TimeSpan.FromHours(1).Ticks,
        };
        var barSpanTicks = (long)(spanTicks * 0.8);

        var xAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            TicklineColor = palette.Grid,
            MajorGridlineColor = palette.Grid,
            MajorGridlineStyle = LineStyle.Solid,
            AxislineColor = palette.Grid,
            TextColor = palette.Text,
            TitleColor = palette.Text,
            StringFormat = GetTrendAxisFormat(buckets, bucketSize),
            IntervalLength = 110, // 按像素间距自动疏密标签（P2-8 防重叠；P2-10 加大疏密减少文字量）
        };
        // X 轴兜底：buckets 跨度为 0 时（单桶/所有桶时间相同）显式扩展 ±1 个桶，
        // 避免 OxyPlot Axis.Transform 计算 (x - offset) * scale 时 scale = ±Infinity
        // 抛出 "Invalid transform (screen coordinate=-1.028e+308)"（修复 2026-08-11）。
        if (buckets.Length > 0)
        {
            var minBucket = buckets[0];
            var maxBucket = buckets[^1];
            var bucketSpan = maxBucket - minBucket;
            long padTicks = bucketSpan <= TimeSpan.Zero
                ? spanTicks
                : Math.Max((long)(bucketSpan.Ticks * 0.05), spanTicks);
            xAxis.Minimum = DateTimeAxis.ToDouble(minBucket.AddTicks(-padTicks));
            xAxis.Maximum = DateTimeAxis.ToDouble(maxBucket.AddTicks(padTicks));
        }
        var yAxis = new LinearAxis
        {
            Title = Strings.M190,
            Position = AxisPosition.Left,
            TicklineColor = palette.Grid,
            MajorGridlineColor = palette.Grid,
            MajorGridlineStyle = LineStyle.Solid,
            AxislineColor = palette.Grid,
            TextColor = palette.Text,
            TitleColor = palette.Text,
        };
        // 显式设 Y 轴 Maximum（数据 max(ok+ng) + 5% padding）。
        // 班次背景 RectangleAnnotation 覆盖整个绘图区需要 MaximumY 接近 ActualMaximum；
        // 直接用 double.MaxValue 会让 yaxis.Transform 在 DEBUG 校验时抛
        // "Invalid transform (screen coordinate=-1.028e+308)"（修复 2026-08-11）
        var maxOkPlusNg = Math.Max(1,
            okCounts.Zip(ngCounts, (o, n) => (long)o + n).DefaultIfEmpty(1L).Max());
        yAxis.Maximum = maxOkPlusNg * 1.05;
        // 方案 D 双轴：右轴 = 良品率（0-100%），承载良率折线与 90% 参考带
        var qualityAxis = new LinearAxis
        {
            Key = QualityAxisKey,
            Title = Strings.K001,
            Position = AxisPosition.Right,
            Minimum = 0,
            Maximum = 1,
            StringFormat = "P0",
            TicklineColor = palette.Grid,
            AxislineColor = palette.Grid,
            TextColor = palette.Text,
            TitleColor = palette.Text,
            MajorGridlineStyle = LineStyle.None, // 右轴不画网格，避免与左轴产量网格交叉混乱
        };
        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);
        model.Axes.Add(qualityAxis);

        // P0-1 班次分区：背景色带 + 班次名（shifts 参数此前传入但未使用，
        // 调色板 ShiftBackground 也为此设计遗留——此处补齐）
        var shiftSegments = EnumerateShiftSegments(buckets[0], buckets[^1], shifts);
        // 班次名去重：同名班次只标注窗口内首次出现的段（P2-10 精简重复文字）
        var labeledShifts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in shiftSegments)
        {
            var showName = segment.Shift is not null && labeledShifts.Add(segment.Shift.Name);
            model.Annotations.Add(new RectangleAnnotation
            {
                MinimumX = DateTimeAxis.ToDouble(segment.Start),
                MaximumX = DateTimeAxis.ToDouble(segment.End),
                MinimumY = 0,
                MaximumY = yAxis.Maximum,
                Fill = palette.ShiftBackground,
                Text = showName ? segment.Shift?.Name : null,
                TextPosition = new DataPoint(DateTimeAxis.ToDouble(segment.Start), 0),
                TextHorizontalAlignment = HorizontalAlignment.Left,
                TextVerticalAlignment = VerticalAlignment.Top,
                TextColor = OxyColor.FromArgb(70, palette.Text.R, palette.Text.G, palette.Text.B),
                ClipByXAxis = true,
            });
        }
        // 班次分隔竖线：相邻班次背景同色时依赖竖线区分边界（端点在图外时跳过）
        foreach (var boundary in shiftSegments.Select(s => s.End).Distinct())
        {
            if (boundary <= buckets[0] || boundary >= buckets[^1]) continue;
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = DateTimeAxis.ToDouble(boundary),
                Color = palette.Grid,
                LineStyle = LineStyle.Solid,
                StrokeThickness = 0.5,
            });
        }

        // 方案 D：90% 良品率参考带（右轴，浅绿半透明 + 虚线边界）
        model.Annotations.Add(new RectangleAnnotation
        {
            MinimumX = DateTimeAxis.ToDouble(buckets[0]),
            MaximumX = DateTimeAxis.ToDouble(buckets[^1]),
            MinimumY = 0.9,
            MaximumY = 1.0,
            YAxisKey = QualityAxisKey,
            Fill = OxyColor.FromArgb(22, QualityLineColor.R, QualityLineColor.G, QualityLineColor.B),
        });
        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            Y = 0.9,
            YAxisKey = QualityAxisKey,
            Color = OxyColor.FromArgb(150, QualityLineColor.R, QualityLineColor.G, QualityLineColor.B),
            LineStyle = LineStyle.Dash,
            StrokeThickness = 0.8,
        });

        var okSeries = new RectangleBarSeries
        {
            Title = Strings.M191,
            FillColor = palette.Ok,
            StrokeColor = OxyColors.Transparent,
            // {7}=item.Title 多行（时间[班次]/OK/NG/良率）。Format 参数：values=[Title,X轴,X0,X1,Y轴,Y0,Y1,item.Title]，
            // 共 8 个（values[0..7]），item 本身是独立参数不入 values——原 "{0}" 显示 series Title 且 {8} 越界（P2-17）
            TrackerFormatString = "{7}",
            LabelFormatString = null, // 不把多行 Title 画在柱上（P2-10 精简：悬停才显示）
        };
        var ngSeries = new RectangleBarSeries
        {
            Title = Strings.M192,
            FillColor = palette.Ng,
            StrokeColor = OxyColors.Transparent,
            TrackerFormatString = "{7}",
            LabelFormatString = null, // 同上：柱上不画文字
        };
        // 方案 D：良品率折线（右轴）；空桶 y=NaN 断线不连通
        var qualitySeries = new LineSeries
        {
            Title = Strings.K001,
            Color = QualityLineColor,
            StrokeThickness = 2.4,
            YAxisKey = QualityAxisKey,
            // LineSeries 参数：{0}=Title {2}=X {4}=Y；原 {2:P1} 把时间戳当百分比显示（P2-17）
            TrackerFormatString = "{0}: {4:P1}",
        };
        for (var index = 0; index < buckets.Length; index++)
        {
            var x0 = DateTimeAxis.ToDouble(buckets[index]);
            var x1 = DateTimeAxis.ToDouble(buckets[index].AddTicks(barSpanTicks));
            var ok = okCounts[index];
            var ng = ngCounts[index];
            var quality = ok + ng > 0 ? (double)ok / (ok + ng) : 0;
            var shiftName = shiftSegments.FirstOrDefault(
                s => buckets[index] >= s.Start && buckets[index] < s.End).Shift?.Name;
            // 多行 Title：悬停一次性读出 桶时间[班次] / OK+NG / 良率（P0-2）
            var title = $"{buckets[index]:MM-dd HH:mm}{(shiftName is null ? "" : $"  [{shiftName}]")}\n"
                + $"{Strings.M191}: {ok:N0}  {Strings.M192}: {ng:N0}\n"
                + $"{Strings.K001}: {quality:P1}";
            okSeries.Items.Add(new RectangleBarItem(x0, 0, x1, ok)
            {
                Color = palette.Ok,
                Title = title,
            });
            if (ng > 0) ngSeries.Items.Add(new RectangleBarItem(x0, ok, x1, ok + ng)
            {
                Color = palette.Ng,
                Title = title,
            });
            qualitySeries.Points.Add(new DataPoint(
                DateTimeAxis.ToDouble(buckets[index].AddTicks(spanTicks / 2)), // 桶中心（单位统一：ToDouble 返回天数）
                ok + ng > 0 ? quality : double.NaN));

            // P1-4 异常桶标注：NG 率超阈值时柱顶打红色 !（复盘时问题时段自动浮现）
            if (ok + ng > 0 && ng / (double)(ok + ng) > NgRateAlertThreshold)
            {
                model.Annotations.Add(new PointAnnotation
                {
                    X = DateTimeAxis.ToDouble(buckets[index].AddTicks(spanTicks / 2)), // 桶中心
                    Y = ok + ng,
                    Text = "!",
                    TextColor = palette.Ng,
                    Fill = palette.Ng,
                    Shape = MarkerType.Circle,
                    Size = 5,
                    StrokeThickness = 0,
                    Layer = AnnotationLayer.AboveSeries,
                });
            }
        }
        model.Series.Add(okSeries);
        model.Series.Add(ngSeries);
        model.Series.Add(qualitySeries);
        if (targetCycle > 0 && buckets.Length > 0 && deviceCount > 0)
        {
            var bucketHours = bucketSize switch
            {
                ProductionReviewBucketSize.Minute5 => 5.0 / 60.0,
                ProductionReviewBucketSize.Hour => 1.0,
                ProductionReviewBucketSize.Day => 24.0,
                _ => 1.0,
            };
            var target = targetCycle * bucketHours * deviceCount;
            // 目标线用 LineAnnotation 而非 LineSeries：Annotation 不参与轴自动缩放，
            // 目标值远高于实际产量时不再撑高 Y 轴导致柱体失真（P2-6）
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = target,
                MinimumX = DateTimeAxis.ToDouble(buckets[0]),
                MaximumX = DateTimeAxis.ToDouble(buckets[^1]),
                Color = palette.Pause,
                LineStyle = LineStyle.Dash,
                StrokeThickness = 1.5,
                Layer = AnnotationLayer.AboveSeries,
            });
        }
        return model;
    }

    /// <summary>NG 率超此阈值时该桶打异常标记（10% = 与良品率 90% 对应）。</summary>
    private const double NgRateAlertThreshold = 0.10;

    /// <summary>右轴 Key（良品率轴，0-100%）。</summary>
    private const string QualityAxisKey = "quality";

    /// <summary>良品率折线与参考带颜色（与 ChartPalette.Oee 一致的绿色）。</summary>
    private static readonly OxyColor QualityLineColor = OxyColor.FromRgb(0x34, 0xD3, 0x99);

    /// <summary>X 轴时间格式：按窗口跨度自动选择，避免小时桶长窗口标签重叠（P2-8/P2-10 精简）。</summary>
    private static string GetTrendAxisFormat(DateTime[] buckets, ProductionReviewBucketSize bucketSize)
    {
        if (bucketSize == ProductionReviewBucketSize.Day || buckets.Length < 2) return "MM-dd";
        var span = buckets[^1] - buckets[0];
        // ≥48h 只显示日期（小时粒度交给悬停 Title），减少横轴文字量
        return span.TotalHours >= 48 ? "MM-dd" : "HH:mm";
    }

    /// <summary>枚举窗口内所有班次时段（含跨天班次；窗口两端部分重叠的班次按交集裁剪）。</summary>
    private static List<ShiftSegment> EnumerateShiftSegments(
        DateTime from, DateTime to, IReadOnlyList<ShiftConfig> shifts)
    {
        var segments = new List<ShiftSegment>();
        if (shifts is null || shifts.Count == 0) return segments;
        var cursor = from.Date.AddDays(-1); // 前推一天：窗口起点可能落在班次中段
        var limit = to.Date.AddDays(1);
        while (cursor < limit)
        {
            foreach (var shift in shifts)
            {
                var start = cursor + shift.StartTime;
                var end = cursor + shift.EndTime;
                if (end <= start) end = end.AddDays(1); // 跨天班次（如 22:00-06:00）
                var segStart = start > from ? start : from;
                var segEnd = end < to ? end : to;
                if (segStart < segEnd)
                    segments.Add(new ShiftSegment(segStart, segEnd, shift));
            }
            cursor = cursor.AddDays(1);
        }
        return segments;
    }

    private readonly record struct ShiftSegment(DateTime Start, DateTime End, ShiftConfig? Shift);

    /// <summary>图表默认字体：中文字体（SkiaSharp 导出无字体回退，Segoe UI 会把中文画成"口"，P2-11）。</summary>
    private const string ChineseFontFamily = "Microsoft YaHei";

    public PlotModel BuildOeeWaterfall(double performance, double availability, double quality, double oee,
        OverviewChartPalette palette)
    {
        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = palette.Text,
            DefaultFont = ChineseFontFamily, // 中文渲染（P2-11）
        };
        // 纵向柱状图：OxyPlot 2.x 的 BarSeries 即垂直柱（2.2.0 已无 ColumnSeries 类），
        // 轴摆放为 CategoryAxis 在底部 X 轴 + LinearAxis 在左侧 Y 轴。
        // ⚠ Key 绑定是反直觉的：BarSeries 内部 GetCategoryAxis() 校验的是 YAxis（YAxisKey 指向的轴），
        // 因此 YAxisKey 必须指向 CategoryAxis、XAxisKey 必须指向 LinearAxis（值轴）。
        // 绑反不报编译错，构建模型也不报错，只在渲染期抛
        // "BarSeries requires a CategoryAxis on the Y axis"（2026-08-10 复盘页线上问题；
        // 与 ChartService.BuildOeeChart 的正确写法保持一致）。
        var categoryAxis = new CategoryAxis { Position = AxisPosition.Bottom, TextColor = palette.Text, TitleColor = palette.Text };
        // 轴起点策略：
        // ① minRate ≥ 0.7（设备健康、损失小）：启用紧凑起点（向下取整 0.1 档、上限 0.9），
        //    避免 0~0.9 大片空白并放大高值间的损失差异；上限 0.9 防止全 1.0 时 Minimum==Maximum 轴退化。
        // ② minRate < 0.7（损失显著，如低 OEE）：保持 0 起点，柱高=率值语义直观；
        //    若此时也压缩起点，低值柱会被压成细线，视觉上"只剩柱顶文字"（2026-08 线上问题）。
        var minRate = new[] { performance, availability, quality, oee }.Min();
        var axisMin = minRate >= 0.7 ? Math.Clamp(Math.Floor(minRate * 10) / 10, 0, 0.9) : 0;
        var valueAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = axisMin,
            Maximum = 1,
            StringFormat = "P0",
            TextColor = palette.Text,
            TitleColor = palette.Text,
        };
        model.Axes.Add(categoryAxis);
        model.Axes.Add(valueAxis);
        var series = new BarSeries
        {
            FillColor = palette.Base,
            // OxyPlot 2.x 约定：YAxisKey 指向类别轴（CategoryAxis）、XAxisKey 指向值轴（LinearAxis）。
            // 曾绑反导致渲染期抛 "BarSeries requires a CategoryAxis on the Y axis"（2026-08-10）。
            XAxisKey = "value",
            YAxisKey = "category",
            // 柱内标签（用户确认 2026-08-11）：数值显示在柱体内部而非柱顶上方；
            // 深色文字保证黄柱（可用率）上对比度足够（浅色文字黄底不可读）。
            LabelFormatString = "{0:P0}",
            LabelPlacement = LabelPlacement.Inside,
            LabelMargin = 6,
            TextColor = OxyColor.FromRgb(0x1A, 0x20, 0x29),
        };
        valueAxis.Key = "value";
        categoryAxis.Key = "category";
        categoryAxis.Labels.Add(Strings.M236);
        categoryAxis.Labels.Add(Strings.M237);
        categoryAxis.Labels.Add(Strings.M238);
        categoryAxis.Labels.Add("OEE");
        series.Items.Add(new BarItem { Value = performance, Color = palette.Base });
        series.Items.Add(new BarItem { Value = availability, Color = palette.Pause });
        series.Items.Add(new BarItem { Value = quality, Color = palette.Ok });
        series.Items.Add(new BarItem { Value = oee, Color = palette.Ng });
        model.Series.Add(series);
        return model;
    }

    public PlotModel BuildDefectParetoChart(
        IReadOnlyList<DefectParetoSummary> defects,
        OverviewChartPalette palette)
    {
        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = palette.Text,
            DefaultFont = ChineseFontFamily, // 中文渲染（P2-11）
        };
        if (defects is null || defects.Count == 0) return model;

        // 类别轴在底部 X 轴（缺陷名）
        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Bottom,
            Key = "category",
            TextColor = palette.Text,
            TitleColor = palette.Text,
            TicklineColor = palette.Grid,
            AxislineColor = palette.Grid,
        };
        foreach (var defect in defects)
            categoryAxis.Labels.Add(defect.DefectName);

        // 左轴：新增数量
        var countAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Key = "count",
            Minimum = 0,
            StringFormat = "N0",
            TicklineColor = palette.Grid,
            MajorGridlineColor = palette.Grid,
            MajorGridlineStyle = LineStyle.Solid,
            AxislineColor = palette.Grid,
            TextColor = palette.Text,
            TitleColor = palette.Text,
        };
        // 右轴：累计占比（0-100%），不画网格避免与左轴交叉混乱（同趋势图良率轴处理）
        var cumulativeAxis = new LinearAxis
        {
            Position = AxisPosition.Right,
            Key = "cum",
            Minimum = 0,
            Maximum = 1,
            StringFormat = "P0",
            TicklineColor = palette.Grid,
            AxislineColor = palette.Grid,
            TextColor = palette.Text,
            TitleColor = palette.Text,
            MajorGridlineStyle = LineStyle.None,
        };
        model.Axes.Add(categoryAxis);
        model.Axes.Add(countAxis);
        model.Axes.Add(cumulativeAxis);

        // 柱：新增数量。⚠ OxyPlot 2.x 约定：YAxisKey 指向 CategoryAxis、XAxisKey 指向值轴（见 BuildOeeWaterfall 注释）
        // 柱内标签（参照 OEE 瀑布图，2026-08-11）：数量显示在柱体内部 + 深色文字（红柱上对比度足够）
        var barSeries = new BarSeries
        {
            FillColor = palette.Ng,
            StrokeColor = OxyColors.Transparent,
            XAxisKey = "count",
            YAxisKey = "category",
            LabelFormatString = "{0:N0}", // 柱内数量标签
            LabelPlacement = LabelPlacement.Inside,
            LabelMargin = 6,
            TextColor = OxyColor.FromRgb(0x1A, 0x20, 0x29),
            TrackerFormatString = "{1}: {2}",
        };
        foreach (var defect in defects)
            barSeries.Items.Add(new BarItem { Value = defect.Count, Color = palette.Ng });
        model.Series.Add(barSeries);

        // 累计占比折线（右轴）；CumulativePercent 为 TopN 内部累计占比
        var cumulativeSeries = new LineSeries
        {
            YAxisKey = "cum",
            Color = ChartPalette.Oee,
            StrokeThickness = 2,
            MarkerType = MarkerType.Circle,
            MarkerSize = 4,
            MarkerFill = ChartPalette.Oee,
            MarkerStroke = ChartPalette.Oee,
            TrackerFormatString = "{0}: {2:P1}",
            Title = Strings.K655, // 累计占比（resx，本地化守卫要求）
        };
        for (var i = 0; i < defects.Count; i++)
            cumulativeSeries.Points.Add(new DataPoint(i, defects[i].CumulativePercent / 100.0));
        model.Series.Add(cumulativeSeries);

        return model;
    }

    public PlotModel BuildHeatmap(
        DateTime[] buckets,
        IReadOnlyList<ProductionReviewHeatmapRow> rows,
        ProductionReviewBucketSize bucketSize,
        OverviewChartPalette palette)
    {
        if (buckets.Length == 0 || rows.Count == 0)
        {
            return new PlotModel { Background = OxyColors.Transparent };
        }

        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = palette.Text,
            DefaultFont = ChineseFontFamily, // 中文渲染（P2-11）
        };
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            TicklineColor = palette.Grid,
            MajorGridlineColor = palette.Grid,
            MajorGridlineStyle = LineStyle.Solid,
            AxislineColor = palette.Grid,
            TextColor = palette.Text,
            StringFormat = bucketSize == ProductionReviewBucketSize.Day ? "MM-dd" : "HH:mm",
        });

        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Left,
            TextColor = palette.Text,
            TicklineColor = palette.Grid,
            AxislineColor = palette.Grid,
            MajorGridlineStyle = LineStyle.None,
        };
        foreach (var row in rows)
            categoryAxis.Labels.Add(row.DeviceName);
        model.Axes.Add(categoryAxis);

        var maxOk = 1;
        foreach (var row in rows)
        {
            for (var index = 0; index < buckets.Length && index < row.HourlyOk.Count; index++)
                maxOk = Math.Max(maxOk, row.HourlyOk[index]);
        }

        // 色阶图例：右侧渐变色条，让用户把颜色映射回数值
        var heatmapColors = new OxyColor[33];
        for (var i = 0; i <= 32; i++)
            heatmapColors[i] = ChartPalette.Heatmap((double)i / 32);
        var colorAxis = new LinearColorAxis
        {
            Position = AxisPosition.Right,
            HighColor = ChartPalette.Heatmap(1.0),
            LowColor = ChartPalette.HeatmapZero,
            Minimum = 0,
            Maximum = maxOk,
            Title = Strings.M190,
            TextColor = palette.Text,
            TitleColor = palette.Text,
            TicklineColor = palette.Grid,
            AxislineColor = palette.Grid,
            Palette = new OxyPalette(heatmapColors),
        };
        model.Axes.Add(colorAxis);

        var series = new RectangleBarSeries
        {
            StrokeColor = ChartPalette.HeatmapBorder,
            StrokeThickness = 0.5,
            // {7}=item.Title（设备+时间+产量），P2-17 与趋势图同修
            TrackerFormatString = "{7}",
            // 不把 Title 画在柱上（P2-10 同款：热力图靠颜色+右侧色阶表达数值，悬停才显示完整信息）
            LabelFormatString = null,
        };
        var bucketSpanTicks = bucketSize switch
        {
            ProductionReviewBucketSize.Minute5 => TimeSpan.FromMinutes(5).Ticks,
            ProductionReviewBucketSize.Hour => TimeSpan.FromHours(1).Ticks,
            ProductionReviewBucketSize.Day => TimeSpan.FromDays(1).Ticks,
            _ => TimeSpan.FromHours(1).Ticks,
        };

        var timeFormat = bucketSize == ProductionReviewBucketSize.Day ? "MM-dd" : "HH:mm";
        for (var deviceIndex = 0; deviceIndex < rows.Count; deviceIndex++)
        {
            var hourly = rows[deviceIndex].HourlyOk;
            var deviceName = rows[deviceIndex].DeviceName;
            for (var bucketIndex = 0; bucketIndex < buckets.Length; bucketIndex++)
            {
                var value = bucketIndex < hourly.Count ? hourly[bucketIndex] : 0;
                var x0 = DateTimeAxis.ToDouble(buckets[bucketIndex]);
                var x1 = DateTimeAxis.ToDouble(buckets[bucketIndex].AddTicks(bucketSpanTicks));
                var color = value <= 0
                    ? ChartPalette.HeatmapZero
                    : ChartPalette.Heatmap((double)value / maxOk);
                var timeLabel = buckets[bucketIndex].ToString(timeFormat);
                series.Items.Add(new RectangleBarItem(
                    x0,
                    deviceIndex - 0.4,
                    x1,
                    deviceIndex + 0.4)
                {
                    Color = color,
                    Title = $"{deviceName}  {timeLabel}  {Strings.M190}: {value:N0}",
                });
            }
        }
        model.Series.Add(series);
        return model;
    }
}
