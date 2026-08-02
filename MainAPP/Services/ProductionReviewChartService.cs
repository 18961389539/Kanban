using MainAPP.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

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
        };
        model.Legends.Add(new Legend
        {
            LegendBackground = OxyColors.Transparent,
            LegendBorder = palette.Grid,
            LegendTextColor = palette.Text,
            LegendPosition = LegendPosition.TopRight,
            LegendPlacement = LegendPlacement.Outside,
        });
        var xAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            TicklineColor = palette.Grid,
            MajorGridlineColor = palette.Grid,
            MajorGridlineStyle = LineStyle.Solid,
            AxislineColor = palette.Grid,
            TextColor = palette.Text,
            TitleColor = palette.Text,
            StringFormat = bucketSize == ProductionReviewBucketSize.Day ? "MM-dd" : "HH:mm",
        };
        var yAxis = new LinearAxis
        {
            Title = "产量(件)",
            Position = AxisPosition.Left,
            TicklineColor = palette.Grid,
            MajorGridlineColor = palette.Grid,
            MajorGridlineStyle = LineStyle.Solid,
            AxislineColor = palette.Grid,
            TextColor = palette.Text,
            TitleColor = palette.Text,
        };
        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);

        var spanTicks = bucketSize switch
        {
            ProductionReviewBucketSize.Minute5 => TimeSpan.FromMinutes(5).Ticks,
            ProductionReviewBucketSize.Hour => TimeSpan.FromHours(1).Ticks,
            ProductionReviewBucketSize.Day => TimeSpan.FromDays(1).Ticks,
            _ => TimeSpan.FromHours(1).Ticks,
        };
        var barSpanTicks = (long)(spanTicks * 0.8);
        var okSeries = new RectangleBarSeries { Title = "OK 产量", FillColor = palette.Ok, StrokeColor = OxyColors.Transparent };
        var ngSeries = new RectangleBarSeries { Title = "NG 产量", FillColor = palette.Ng, StrokeColor = OxyColors.Transparent };
        for (var index = 0; index < buckets.Length; index++)
        {
            var x0 = DateTimeAxis.ToDouble(buckets[index]);
            var x1 = DateTimeAxis.ToDouble(buckets[index].AddTicks(barSpanTicks));
            var ok = okCounts[index];
            var ng = ngCounts[index];
            okSeries.Items.Add(new RectangleBarItem(x0, 0, x1, ok) { Color = palette.Ok });
            if (ng > 0) ngSeries.Items.Add(new RectangleBarItem(x0, ok, x1, ok + ng) { Color = palette.Ng });
        }
        model.Series.Add(okSeries);
        model.Series.Add(ngSeries);
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
            var targetSeries = new LineSeries
            {
                Title = $"目标 {target:F0} 件/桶",
                Color = palette.Pause,
                StrokeThickness = 1.5,
                LineStyle = LineStyle.Dash,
            };
            targetSeries.Points.Add(new DataPoint(DateTimeAxis.ToDouble(buckets[0]), target));
            targetSeries.Points.Add(new DataPoint(DateTimeAxis.ToDouble(buckets[^1]), target));
            model.Series.Add(targetSeries);
        }
        return model;
    }

    public PlotModel BuildOeeWaterfall(double performance, double availability, double quality, double oee,
        OverviewChartPalette palette)
    {
        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = palette.Text,
        };
        var categoryAxis = new CategoryAxis { Position = AxisPosition.Left, TextColor = palette.Text, TitleColor = palette.Text };
        var valueAxis = new LinearAxis { Position = AxisPosition.Bottom, Minimum = 0, Maximum = 1, StringFormat = "P0", TextColor = palette.Text, TitleColor = palette.Text };
        model.Axes.Add(categoryAxis);
        model.Axes.Add(valueAxis);
        var series = new BarSeries
        {
            FillColor = palette.Base,
            XAxisKey = "value",
            YAxisKey = "category",
        };
        valueAxis.Key = "value";
        categoryAxis.Key = "category";
        categoryAxis.Labels.Add("性能率");
        categoryAxis.Labels.Add("可用率");
        categoryAxis.Labels.Add("良品率");
        categoryAxis.Labels.Add("OEE");
        series.Items.Add(new BarItem { Value = performance, Color = palette.Base });
        series.Items.Add(new BarItem { Value = availability, Color = palette.Pause });
        series.Items.Add(new BarItem { Value = quality, Color = palette.Ok });
        series.Items.Add(new BarItem { Value = oee, Color = palette.Ng });
        model.Series.Add(series);
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

        var series = new RectangleBarSeries
        {
            StrokeColor = ChartPalette.HeatmapBorder,
            StrokeThickness = 0.5,
        };
        var bucketSpanTicks = bucketSize switch
        {
            ProductionReviewBucketSize.Minute5 => TimeSpan.FromMinutes(5).Ticks,
            ProductionReviewBucketSize.Hour => TimeSpan.FromHours(1).Ticks,
            ProductionReviewBucketSize.Day => TimeSpan.FromDays(1).Ticks,
            _ => TimeSpan.FromHours(1).Ticks,
        };

        for (var deviceIndex = 0; deviceIndex < rows.Count; deviceIndex++)
        {
            var hourly = rows[deviceIndex].HourlyOk;
            for (var bucketIndex = 0; bucketIndex < buckets.Length; bucketIndex++)
            {
                var value = bucketIndex < hourly.Count ? hourly[bucketIndex] : 0;
                var x0 = DateTimeAxis.ToDouble(buckets[bucketIndex]);
                var x1 = DateTimeAxis.ToDouble(buckets[bucketIndex].AddTicks(bucketSpanTicks));
                var color = value <= 0
                    ? ChartPalette.HeatmapZero
                    : ChartPalette.Heatmap((double)value / maxOk);
                series.Items.Add(new RectangleBarItem(
                    x0,
                    deviceIndex - 0.4,
                    x1,
                    deviceIndex + 0.4)
                {
                    Color = color,
                });
            }
        }
        model.Series.Add(series);
        return model;
    }
}
