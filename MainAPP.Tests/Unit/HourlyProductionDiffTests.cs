using Kanban.Contracts.Metrics;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class HourlyProductionDiffTests
{
    [Fact]
    public void FromSamples_DiffsAgainstWindowBaseline_OneBarPerHour()
    {
        var from = new DateTime(2026, 9, 6, 8, 0, 0);
        var now = new DateTime(2026, 9, 6, 9, 40, 0);
        var series = HourlyProductionDiff.FromSamples(
        [
            (new DateTime(2026, 9, 6, 8, 30, 0), 130, 0),
            (new DateTime(2026, 9, 6, 9, 20, 0), 180, 0),
        ], from, now, baselineOk: 100, baselineNg: 0);

        Assert.Equal(2, series.Hours.Length);
        Assert.Equal(from, series.Hours[0]);
        Assert.Equal(from.AddHours(1), series.Hours[1]);
        Assert.Equal(30, series.OkDiff[0]);
        Assert.Equal(50, series.OkDiff[1]);
    }

    [Fact]
    public void FromSamples_EmptyHoursFillForward_DoesNotTreatGapAsReset()
    {
        var from = new DateTime(2026, 9, 6, 8, 0, 0);
        var to = new DateTime(2026, 9, 6, 10, 10, 0);
        var series = HourlyProductionDiff.FromSamples(
        [
            (new DateTime(2026, 9, 6, 8, 20, 0), 40, 2),
            (new DateTime(2026, 9, 6, 10, 5, 0), 90, 4),
        ], from, to, 10, 1);

        Assert.Equal(3, series.Hours.Length);
        Assert.Equal(30, series.OkDiff[0]);
        Assert.Equal(0, series.OkDiff[1]);
        Assert.Equal(50, series.OkDiff[2]);
        Assert.Equal(1, series.NgDiff[0]);
        Assert.Equal(0, series.NgDiff[1]);
        Assert.Equal(2, series.NgDiff[2]);
    }

    [Fact]
    public void FromSamples_FirstBucketBelowBaseline_KeepsItsProduction()
    {
        var from = new DateTime(2026, 9, 22, 8, 0, 0);
        var to = new DateTime(2026, 9, 22, 10, 10, 0);
        // 计数器在 08:00 被清零：窗口前基线 648/72 是上一班的累计值，
        // 首桶末样本 272/18 低于它 —— 修复前差值为负被夹成 0，早上的产量整段消失。
        var series = HourlyProductionDiff.FromSamples(
        [
            (new DateTime(2026, 9, 22, 8, 59, 59), 272, 18),
            (new DateTime(2026, 9, 22, 9, 37, 21), 463, 32),
        ], from, to, baselineOk: 648, baselineNg: 72);

        Assert.Equal(272, series.OkDiff[0]);
        Assert.Equal(18, series.NgDiff[0]);
        Assert.Equal(191, series.OkDiff[1]);
        Assert.Equal(14, series.NgDiff[1]);
    }

    [Fact]
    public void FromSamples_CounterReset_KeepsPostResetProduction()
    {
        var from = new DateTime(2026, 9, 22, 16, 0, 0);
        var to = new DateTime(2026, 9, 22, 18, 10, 0);
        // 17:00 计数器被清零后重新累计到 256：修复前 256-430 为负 → 夹成 0，复位后那一桶全丢。
        var series = HourlyProductionDiff.FromSamples(
        [
            (new DateTime(2026, 9, 22, 16, 55, 0), 430, 30),
            (new DateTime(2026, 9, 22, 17, 5, 0), 12, 1),
            (new DateTime(2026, 9, 22, 17, 54, 59), 256, 21),
        ], from, to, baselineOk: 100, baselineNg: 10);

        Assert.Equal(330, series.OkDiff[0]);
        Assert.Equal(20, series.NgDiff[0]);
        Assert.Equal(256, series.OkDiff[1]);
        Assert.Equal(21, series.NgDiff[1]);
    }

    [Fact]
    public void FromSamples_TrimBeforeLastReset_KeepsOnlyLastGeneration()
    {
        var from = new DateTime(2026, 9, 22, 8, 0, 0);
        var to = new DateTime(2026, 9, 22, 18, 10, 0);
        var series = HourlyProductionDiff.FromSamples(
        [
            (new DateTime(2026, 9, 22, 8, 59, 59), 272, 18),
            (new DateTime(2026, 9, 22, 9, 37, 21), 463, 32),
            (new DateTime(2026, 9, 22, 16, 59, 57), 0, 0),
            (new DateTime(2026, 9, 22, 17, 54, 59), 256, 21),
        ], from, to, 648, 72, trimBeforeLastReset: true);

        var idx = Array.FindIndex(series.Hours, h => h == new DateTime(2026, 9, 22, 17, 0, 0));
        Assert.True(idx > 0);
        Assert.Equal(256, series.OkDiff[idx]);
        Assert.Equal(21, series.NgDiff[idx]);
        Assert.All(series.OkDiff.Take(idx), v => Assert.Equal(0, v));
        Assert.All(series.NgDiff.Take(idx), v => Assert.Equal(0, v));
    }

    [Fact]
    public void PadToShiftWindow_NightShift_FillsAllTwelveHours()
    {
        var start = new DateTime(2026, 9, 17, 20, 0, 0);
        var end = new DateTime(2026, 9, 18, 8, 0, 0);
        var elapsed = HourlyProductionDiff.FromSamples(
        [
            (new DateTime(2026, 9, 17, 20, 20, 0), 130, 0),
        ], start, new DateTime(2026, 9, 17, 20, 30, 0), baselineOk: 100, baselineNg: 0);

        Assert.Single(elapsed.Hours);
        Assert.Equal(30, elapsed.OkDiff[0]);

        var padded = HourlyProductionDiff.PadToShiftWindow(elapsed, start, end);
        Assert.Equal(12, padded.Hours.Length);
        Assert.Equal(start, padded.Hours[0]);
        Assert.Equal(new DateTime(2026, 9, 18, 7, 0, 0), padded.Hours[^1]);
        Assert.Equal(30, padded.OkDiff[0]);
        Assert.All(padded.OkDiff.Skip(1), v => Assert.Equal(0, v));
    }
}
