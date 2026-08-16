using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// OEE 计算器单元测试
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class OeeCalculatorTests
{
    // ──────────── 合格率 ────────────

    [Fact]
    public void QualityRate_AllOk_Returns1()
    {
        Assert.Equal(1.0, OeeCalculator.CalculateQualityRate(100, 0));
    }

    [Fact]
    public void QualityRate_AllNg_Returns0()
    {
        Assert.Equal(0.0, OeeCalculator.CalculateQualityRate(0, 100));
    }

    [Fact]
    public void QualityRate_HalfOkHalfNg_ReturnsHalf()
    {
        Assert.Equal(0.5, OeeCalculator.CalculateQualityRate(50, 50));
    }

    [Fact]
    public void QualityRate_ZeroTotal_Returns0()
    {
        Assert.Equal(0.0, OeeCalculator.CalculateQualityRate(0, 0));
    }

    [Theory]
    [InlineData(120, 0)]    // 超过 100% 时 clamp 到 1
    [InlineData(-10, 0)]     // 负值 clamp 到 0
    public void QualityRate_ClampedToRange(int ok, int ng)
    {
        var rate = OeeCalculator.CalculateQualityRate(ok, ng);
        Assert.InRange(rate, 0.0, 1.0);
    }

    // ──────────── 性能率 ────────────

    [Fact]
    public void PerformanceRate_ZeroTargetCycle_Returns0()
    {
        Assert.Equal(0.0, OeeCalculator.CalculatePerformanceRate(100, 0, 0, 3600));
    }

    [Fact]
    public void PerformanceRate_ZeroRunTime_Returns0()
    {
        Assert.Equal(0.0, OeeCalculator.CalculatePerformanceRate(100, 0, 100, 0));
    }

    [Fact]
    public void PerformanceRate_ExactMatch_Returns1()
    {
        // 目标 100 件/小时，运行 1 小时，恰好生产 100 件
        Assert.Equal(1.0, OeeCalculator.CalculatePerformanceRate(100, 0, 100, 3600));
    }

    [Fact]
    public void PerformanceRate_HalfSpeed_ReturnsHalf()
    {
        // 目标 100 件/小时，运行 1 小时，只生产 50 件
        Assert.Equal(0.5, OeeCalculator.CalculatePerformanceRate(50, 0, 100, 3600));
    }

    [Fact]
    public void PerformanceRate_OverSpeed_ClampedTo1()
    {
        // 目标 100 件/小时，运行 1 小时，生产 150 件 → 1.5 clamp 到 1
        Assert.Equal(1.0, OeeCalculator.CalculatePerformanceRate(150, 0, 100, 3600));
    }

    // ──────────── 可用率 ────────────

    [Fact]
    public void AvailabilityRate_NoAlarm_Returns1()
    {
        Assert.Equal(1.0, OeeCalculator.CalculateAvailabilityRate(3600, 0));
    }

    [Fact]
    public void AvailabilityRate_HalfAlarm_ReturnsHalf()
    {
        // 运行 1800s + 报警 1800s = 3600s, 可用率 = 1800/3600 = 0.5
        Assert.Equal(0.5, OeeCalculator.CalculateAvailabilityRate(1800, 1800));
    }

    [Fact]
    public void AvailabilityRate_ZeroTotal_Returns0()
    {
        Assert.Equal(0.0, OeeCalculator.CalculateAvailabilityRate(0, 0));
    }

    [Fact]
    public void AvailabilityRate_NegativeRunTime_ClampedTo0()
    {
        var rate = OeeCalculator.CalculateAvailabilityRate(-100, 100);
        Assert.InRange(rate, 0.0, 1.0);
    }

    // ──────────── OEE ────────────

    [Fact]
    public void Oee_AllPerfect_Returns1()
    {
        var q = 1.0;
        var p = 1.0;
        var a = 1.0;
        Assert.Equal(1.0, OeeCalculator.CalculateOee(q, p, a));
    }

    [Fact]
    public void Oee_OneZero_Returns0()
    {
        Assert.Equal(0.0, OeeCalculator.CalculateOee(0, 1, 1));
        Assert.Equal(0.0, OeeCalculator.CalculateOee(1, 0, 1));
        Assert.Equal(0.0, OeeCalculator.CalculateOee(1, 1, 0));
    }

    [Fact]
    public void Oee_AllHalf_Returns_OneEighth()
    {
        // 0.5 * 0.5 * 0.5 = 0.125
        Assert.Equal(0.125, OeeCalculator.CalculateOee(0.5, 0.5, 0.5));
    }

    [Fact]
    public void Oee_ClampedTo1()
    {
        // 输入超过 1，结果仍 clamp 到 1
        Assert.Equal(1.0, OeeCalculator.CalculateOee(1.5, 1.5, 1.5));
    }

    [Fact]
    public void Oee_FullVersion_AllPerfect_Returns1()
    {
        // 100 件合格，0 件不良，目标 100 件/小时，运行 1 小时，无报警
        var q = OeeCalculator.CalculateQualityRate(100, 0);
        var p = OeeCalculator.CalculatePerformanceRate(100, 0, 100, 3600);
        var a = OeeCalculator.CalculateAvailabilityRate(3600, 0);
        Assert.Equal(1.0, OeeCalculator.CalculateOee(q, p, a));
    }

    [Fact]
    public void Oee_FullVersion_RealisticCase()
    {
        // 90 件合格，10 件不良，目标 100 件/小时，运行 1 小时（3600s），报警 600s
        // q = 90/100 = 0.9
        // p = 100 / (100 * 1) = 1.0
        // a = 3600 / (3600+600) = 0.857...
        // oee = 0.9 * 1.0 * 0.857... ≈ 0.7714
        var q = OeeCalculator.CalculateQualityRate(90, 10);
        var p = OeeCalculator.CalculatePerformanceRate(90, 10, 100, 3600);
        var a = OeeCalculator.CalculateAvailabilityRate(3600, 600);
        var oee = OeeCalculator.CalculateOee(q, p, a);
        Assert.InRange(oee, 0.77, 0.78);
    }
}
