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
