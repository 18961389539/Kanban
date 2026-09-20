using System;
using Kanban.Analysis;
using Kanban.Contracts.Dtos;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ProductionWindowMetricsTests
{
    [Fact]
    public void SumWindowProduction_EmptyWindow_ReturnsZero()
    {
        var (ok, ng) = ProductionWindowMetrics.SumWindowProduction([], [Log(1, 10, 1)], new DateTime(2026, 8, 8, 8, 0, 0));
        Assert.Equal(0, ok);
        Assert.Equal(0, ng);
    }

    [Fact]
    public void SumWindowProduction_SameInstance_UsesWindowStartAsBaseline()
    {
        var from = new DateTime(2026, 8, 8, 8, 0, 0);
        var window = new[]
        {
            Log(1, 10, 0, from),
            Log(2, 25, 1, from.AddMinutes(20)),
        };
        var (ok, ng) = ProductionWindowMetrics.SumWindowProduction([.. window], [], from);
        Assert.Equal(15, ok);
        Assert.Equal(1, ng);
    }

    [Fact]
    public void SplitShiftInstances_DropsOnOkRollback()
    {
        var t = new DateTime(2026, 8, 8, 8, 0, 0);
        var groups = ProductionWindowMetrics.SplitShiftInstances(
        [
            Log(1, 10, 0, t),
            Log(2, 3, 0, t.AddMinutes(1)),
        ]);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Sample15Min_KeepsLastInBucket()
    {
        var t = new DateTime(2026, 8, 8, 8, 0, 0);
        var sampled = ProductionWindowMetrics.Sample15Min(
        [
            Log(1, 10, 0, t.AddMinutes(1)),
            Log(2, 12, 0, t.AddMinutes(10)),
            Log(3, 20, 0, t.AddMinutes(16)),
        ]);
        Assert.Equal(2, sampled.Count);
        Assert.Equal(12, sampled[0].OkProduction);
        Assert.Equal(20, sampled[1].OkProduction);
    }

    private static ProductionLogDto Log(int id, int ok, int ng, DateTime? time = null) => new()
    {
        Id = id,
        DeviceId = "dev1",
        DeviceName = "设备1",
        ShiftName = "早班",
        OkProduction = ok,
        NgProduction = ng,
        Timestamp = time ?? new DateTime(2026, 8, 8, 8, 0, 0),
    };
}
