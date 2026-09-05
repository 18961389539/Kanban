using Kanban.Contracts.Enums;
using Kanban.Contracts.Metrics;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>锁住主页缺陷帕累托口径：条宽相对合计、累计 80%、其他桶、空状态。</summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class DefectParetoMetricsTests
{
    [Fact]
    public void Build_NoDevice_IgnoresInputs()
    {
        var result = DefectParetoMetrics.Build(
            [new DefectParetoInput("a", 9, DefectSeverity.Minor, DefectCategory.Other, "D1")],
            ngCount: 10,
            hasDevice: false);
        Assert.Equal(DefectParetoEmptyKind.NoDevice, result.EmptyKind);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.ConfiguredCount);
        Assert.Null(result.ShareOfNg);
    }

    [Fact]
    public void Build_NotConfigured_WhenNoDefectKinds()
    {
        var result = DefectParetoMetrics.Build([], ngCount: 5);
        Assert.Equal(DefectParetoEmptyKind.NotConfigured, result.EmptyKind);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.ConfiguredCount);
    }

    [Fact]
    public void Build_AllZero_KeepsConfiguredCount()
    {
        var result = DefectParetoMetrics.Build(
        [
            new("毛边", 0, DefectSeverity.Minor, DefectCategory.Appearance, "D1"),
            new("缩水", 0, DefectSeverity.Major, DefectCategory.Dimension, "D2"),
        ], ngCount: 3);
        Assert.Equal(DefectParetoEmptyKind.AllZero, result.EmptyKind);
        Assert.Equal(2, result.ConfiguredCount);
        Assert.Empty(result.Rows);
        Assert.Null(result.ShareOfNg);
    }

    [Fact]
    public void Build_BarShare_IsRelativeToTotal_NotToMax()
    {
        var result = DefectParetoMetrics.Build(
        [
            new("A", 80, DefectSeverity.Critical, DefectCategory.Function, "D1"),
            new("B", 20, DefectSeverity.Minor, DefectCategory.Other, "D2"),
        ], ngCount: 100);

        Assert.Equal(DefectParetoEmptyKind.HasData, result.EmptyKind);
        Assert.Equal(100, result.TotalCount);
        Assert.Equal(0.8, result.Rows[0].ShareOfTotal, precision: 6);
        Assert.Equal(0.2, result.Rows[1].ShareOfTotal, precision: 6);
        Assert.Equal(1.0, result.Rows[1].CumulativeShare, precision: 6);
        Assert.Equal(1.0, result.ShareOfNg);
    }

    [Fact]
    public void Build_VitalFew_IncludesItemThatCrosses80Percent()
    {
        var result = DefectParetoMetrics.Build(
        [
            new("A", 50, DefectSeverity.Critical, DefectCategory.Appearance, ""),
            new("B", 30, DefectSeverity.Major, DefectCategory.Appearance, ""),
            new("C", 20, DefectSeverity.Minor, DefectCategory.Appearance, ""),
        ], ngCount: 0);

        Assert.True(result.Rows[0].IsVitalFew);
        Assert.True(result.Rows[1].IsVitalFew); // 50+30=80%
        Assert.False(result.Rows[2].IsVitalFew);
        Assert.Null(result.ShareOfNg);
    }

    [Fact]
    public void Build_FirstItemAlreadyOver80_OnlyFirstIsVitalFew()
    {
        var result = DefectParetoMetrics.Build(
        [
            new("A", 90, DefectSeverity.Critical, DefectCategory.Appearance, ""),
            new("B", 10, DefectSeverity.Minor, DefectCategory.Appearance, ""),
        ], ngCount: 10);

        Assert.True(result.Rows[0].IsVitalFew);
        Assert.False(result.Rows[1].IsVitalFew);
        Assert.Equal(1.0, result.ShareOfNg);
    }

    [Fact]
    public void Build_MoreThanTopN_AddsOthersBucket()
    {
        var inputs = new List<DefectParetoInput>();
        for (var i = 0; i < 10; i++)
            inputs.Add(new($"D{i}", 10 - i, DefectSeverity.Minor, DefectCategory.Other, $"D{200 + i}"));

        var result = DefectParetoMetrics.Build(inputs, ngCount: 50);
        Assert.Equal(9, result.Rows.Count);
        Assert.Equal(8, result.Rows.Count(r => !r.IsOthers));
        var others = result.Rows[^1];
        Assert.True(others.IsOthers);
        Assert.Equal(0, others.Rank);
        Assert.Equal(2, others.OtherKindCount);
        Assert.Equal(3, others.Count);
        Assert.False(others.IsVitalFew);
        Assert.Equal(1.0, others.CumulativeShare, precision: 6);
        Assert.Equal(55, result.TotalCount);
        Assert.Equal(10, result.PositiveKindCount);
    }

    [Fact]
    public void Build_TieBreaksByNameOrdinal()
    {
        var result = DefectParetoMetrics.Build(
        [
            new("b", 5, DefectSeverity.Minor, DefectCategory.Other, ""),
            new("a", 5, DefectSeverity.Minor, DefectCategory.Other, ""),
        ], ngCount: 5);

        Assert.Equal(["a", "b"], result.Rows.Select(r => r.Name));
    }

    [Fact]
    public void Build_IgnoresNonPositiveCountsInRanking()
    {
        var result = DefectParetoMetrics.Build(
        [
            new("zero", 0, DefectSeverity.Minor, DefectCategory.Other, ""),
            new("neg", -3, DefectSeverity.Minor, DefectCategory.Other, ""),
            new("ok", 4, DefectSeverity.Major, DefectCategory.Function, "D9"),
        ], ngCount: 4);

        Assert.Single(result.Rows);
        Assert.Equal("ok", result.Rows[0].Name);
        Assert.Equal(3, result.ConfiguredCount);
        Assert.Equal(4, result.TotalCount);
        Assert.Equal("D9", result.Rows[0].PlcAddress);
        Assert.Equal(DefectSeverity.Major, result.Rows[0].Severity);
    }
}
