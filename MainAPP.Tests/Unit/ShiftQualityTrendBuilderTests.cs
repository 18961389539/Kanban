using Kanban.Contracts.Metrics;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class ShiftQualityTrendBuilderTests
{
    [Fact]
    public void FromSamples_FiltersShiftAndComputesQuality()
    {
        var start = new DateTime(2026, 9, 16, 8, 0, 0);
        var points = ShiftQualityTrendBuilder.FromSamples(
        [
            (start.AddMinutes(10), 90, 10, "早班"),
            (start.AddMinutes(20), 1, 1, "晚班"),
            (start.AddMinutes(30), 190, 10, "早班"),
        ], "早班", start, start.AddHours(2));

        Assert.Single(points);
        Assert.Equal(0.95, points[0].Quality, 6);
    }

    [Fact]
    public void FromSamples_KeepsOnePointPerHour()
    {
        var start = new DateTime(2026, 9, 16, 8, 0, 0);
        var points = ShiftQualityTrendBuilder.FromSamples(
        [
            (start.AddMinutes(10), 90, 10, "早班"),
            (start.AddMinutes(40), 95, 5, "早班"),
            (start.AddHours(1).AddMinutes(5), 80, 20, "早班"),
        ], "早班", start, start.AddHours(3));

        Assert.Equal(2, points.Count);
        Assert.Equal(0.95, points[0].Quality, 6);
        Assert.Equal(start.AddMinutes(40), points[0].Time);
        Assert.Equal(0.8, points[1].Quality, 6);
    }

    [Fact]
    public void MergeLive_SameHour_ReplacesPoint_NextHour_Appends()
    {
        var t0 = new DateTime(2026, 9, 16, 8, 0, 0);
        var history = new List<(DateTime Time, double Quality)>();
        for (var i = 0; i < 5; i++)
        {
            history = ShiftQualityTrendBuilder.MergeLive(
                history, t0.AddMinutes(i * 10), 0.96 - i * 0.001, hasOutput: true);
        }

        Assert.Single(history);
        history = ShiftQualityTrendBuilder.MergeLive(history, t0.AddHours(1).AddMinutes(5), 0.9, hasOutput: true);
        Assert.Equal(2, history.Count);
        Assert.Equal(0.9, history[1].Quality, 6);
    }

    [Fact]
    public void MergeLive_WithoutPersistingHistory_StaysSinglePoint()
    {
        var t0 = new DateTime(2026, 9, 16, 8, 0, 0);
        var history = new List<(DateTime Time, double Quality)>();
        var merged = history;
        for (var i = 0; i < 5; i++)
            merged = ShiftQualityTrendBuilder.MergeLive(history, t0.AddSeconds(i * 25), 0.96, hasOutput: true);

        Assert.Single(merged);
    }

    [Fact]
    public void MergeLive_ReplacesRecentPoint()
    {
        var t0 = new DateTime(2026, 9, 16, 8, 0, 0);
        var history = new List<(DateTime Time, double Quality)> { (t0, 0.9) };
        var merged = ShiftQualityTrendBuilder.MergeLive(history, t0.AddSeconds(5), 0.92, hasOutput: true);
        Assert.Single(merged);
        Assert.Equal(0.92, merged[0].Quality, 6);
    }

    [Fact]
    public void Downsample_KeepsFirstAndLast()
    {
        var start = new DateTime(2026, 9, 16, 8, 0, 0);
        var points = Enumerable.Range(0, 200)
            .Select(i => (start.AddMinutes(i), 0.9 + i / 2000.0))
            .ToList();
        var sampled = ShiftQualityTrendBuilder.Downsample(points, 80);
        Assert.Equal(80, sampled.Count);
        Assert.Equal(points[0].Item1, sampled[0].Time);
        Assert.Equal(points[^1].Item1, sampled[^1].Time);
    }
}
