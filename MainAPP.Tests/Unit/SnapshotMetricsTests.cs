using Kanban.Contracts.Metrics;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// SnapshotMetrics 单元测试：锁住 WPF/WASM 共用的展示换算单源。
/// 覆盖速度下限保护、零产量/零时长边界、Clamp 边界——这些是两端展示口径的根，
/// 任一公式回归会同时影响 WPF 与 WASM。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class SnapshotMetricsTests
{
    // ──────────── RealtimeSpeed ────────────

    [Theory]
    [InlineData(0, 10, 0)]        // 未运行 → 0
    [InlineData(4.9, 10, 0)]      // 下限内（<5s）→ 0 防启动失真
    [InlineData(5.0, 0, 0)]       // 恰好下限但零产量 → 0
    public void RealtimeSpeed_ShortRuntime_ReturnsZero(double runTimeSeconds, int ok, int ng)
        => Assert.Equal(0, SnapshotMetrics.RealtimeSpeed(runTimeSeconds, ok, ng));

    [Fact]
    public void RealtimeSpeed_NormalRuntime_ComputesPerHour()
    {
        // 5 分钟运行，50 件 → 50 / (300/3600) = 600 件/小时
        var speed = SnapshotMetrics.RealtimeSpeed(300, 40, 10);
        Assert.Equal(600, speed, precision: 6);
    }

    [Fact]
    public void RealtimeSpeed_CountsOkPlusNg()
    {
        // 1 小时运行 30+20 件 → 50 件/小时
        Assert.Equal(50, SnapshotMetrics.RealtimeSpeed(3600, 30, 20), precision: 6);
    }

    // ──────────── TotalOutput / NgRate ────────────

    [Fact]
    public void TotalOutput_SumsOkAndNg() => Assert.Equal(120, SnapshotMetrics.TotalOutput(100, 20));

    [Theory]
    [InlineData(0, 0, 0.0)]     // 无产量 → 0
    [InlineData(100, 0, 0.0)]   // 全 OK → 0
    [InlineData(100, 100, 0.5)] // 一半 NG → 0.5
    [InlineData(0, 100, 1.0)]   // 全 NG → 1.0
    public void NgRate_VariousInputs(int ok, int ng, double expected)
        => Assert.Equal(expected, SnapshotMetrics.NgRate(ok, ng), precision: 6);

    // ──────────── TimeRatio ────────────

    [Fact]
    public void TimeRatio_ZeroTotal_ReturnsZero()
        => Assert.Equal(0, SnapshotMetrics.TimeRatio(100, 0, 0, 0));

    [Fact]
    public void TimeRatio_ComputesShare()
        => Assert.Equal(0.5, SnapshotMetrics.TimeRatio(30, 30, 20, 10), precision: 6);

    // ──────────── CycleSeconds ────────────

    [Theory]
    [InlineData(0, 0.0)]        // 速度 0 → 0（UI 显示 "—"）
    [InlineData(-5, 0.0)]       // 负速度 → 0
    [InlineData(3600, 1.0)]     // 3600 件/小时 → 1 秒/件
    [InlineData(720, 5.0)]      // 720 件/小时 → 5 秒/件
    public void CycleSeconds_VariousSpeeds(double perHour, double expected)
        => Assert.Equal(expected, SnapshotMetrics.CycleSeconds(perHour), precision: 6);

    // ──────────── AchievementRate ────────────

    [Theory]
    [InlineData(100, 0, 0.0)]     // 无目标 → 0
    [InlineData(50, 100, 0.5)]    // 半速
    [InlineData(150, 100, 1.0)]   // 超速 → Clamp 到 1
    [InlineData(0, 100, 0.0)]     // 静止 → 0
    public void AchievementRate_ClampedTo01(double actual, double target, double expected)
        => Assert.Equal(expected, SnapshotMetrics.AchievementRate(actual, target), precision: 6);
}
