using Kanban.Contracts.Metrics;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class DefectWindowMetricsTests
{
    [Fact]
    public void ComputeIncrements_WithBaseline_UsesLastMinusBaseline()
    {
        var from = new DateTime(2026, 8, 8, 8, 0, 0);
        var to = from.AddHours(4);
        var points = new List<DefectHistoryPoint>
        {
            new("d1", "划痕", from.AddHours(-2), 10, "day"),
            new("d1", "划痕", from.AddHours(1), 15, "day"),
            new("d1", "划痕", from.AddHours(3), 18, "day"),
        };

        var inc = DefectWindowMetrics.ComputeIncrements(points, from, to);

        Assert.Equal(8, inc["d1"]);
    }

    [Fact]
    public void ComputeIncrements_NoBaseline_UsesLastMinusFirstInWindow()
    {
        var from = new DateTime(2026, 8, 8, 8, 0, 0);
        var to = from.AddHours(4);
        var points = new List<DefectHistoryPoint>
        {
            new("d1", "划痕", from.AddHours(1), 5, "day"),
            new("d1", "划痕", from.AddHours(3), 9, "day"),
        };

        var inc = DefectWindowMetrics.ComputeIncrements(points, from, to);

        Assert.Equal(4, inc["d1"]);
    }

    [Fact]
    public void ComputeIncrements_MergesSameDefectAcrossShiftGroups()
    {
        var from = new DateTime(2026, 8, 8, 8, 0, 0);
        var to = from.AddHours(8);
        var points = new List<DefectHistoryPoint>
        {
            new("d1", "划痕", from.AddHours(1), 3, "day"),
            new("d1", "划痕", from.AddHours(2), 5, "day"),
            new("d1", "划痕", from.AddHours(5), 1, "night"),
            new("d1", "划痕", from.AddHours(6), 4, "night"),
        };

        var inc = DefectWindowMetrics.ComputeIncrements(points, from, to);

        Assert.Equal(5, inc["d1"]);
    }
}
