using Kanban.Collector.Core.Entities;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// ProductionReviewCalculations.CalculateProductionDeltaSorted 回归测试（2026-08-11 性能修复）：
/// 二分定位版必须与旧"全量扫描"语义完全一致（班次实例切分 + 窗口前基线）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class ProductionReviewCalculationsTests
{
    private static ProductionLog Log(DateTime t, int ok, int ng, string shift = "白班")
        => new()
        {
            Timestamp = t,
            OkProduction = ok,
            NgProduction = ng,
            ShiftName = shift,
        };

    /// <summary>旧实现语义的线性参考版（全量扫描 + 班次实例差分）。</summary>
    private static int ReferenceDelta(List<ProductionLog> logs, DateTime from, DateTime to)
    {
        var inWindow = logs.Where(l => l.Timestamp >= from && l.Timestamp <= to)
            .OrderBy(l => l.Timestamp).ToList();
        var baselineCandidates = logs.Where(l => l.Timestamp < from)
            .OrderByDescending(l => l.Timestamp).ToList();
        if (inWindow.Count == 0) return 0;

        var total = 0;
        var current = new List<ProductionLog>();
        foreach (var log in inWindow)
        {
            if (current.Count > 0)
            {
                var prev = current[^1];
                if (log.OkProduction < prev.OkProduction
                    || log.NgProduction < prev.NgProduction
                    || log.ShiftName != prev.ShiftName)
                {
                    total += SumInstance(current, from, baselineCandidates);
                    current = [];
                }
            }
            current.Add(log);
        }
        if (current.Count > 0) total += SumInstance(current, from, baselineCandidates);
        return total;
    }

    private static int SumInstance(List<ProductionLog> instance, DateTime from, List<ProductionLog> baselineCandidates)
    {
        var first = instance[0];
        var last = instance[^1];
        ProductionLog? baseline;
        if (first.Timestamp <= from)
        {
            baseline = first;
        }
        else
        {
            baseline = FindBaselineBeforeWindow(baselineCandidates, first.ShiftName) ?? first;
        }
        return Math.Max(0, last.OkProduction - baseline.OkProduction)
            + Math.Max(0, last.NgProduction - baseline.NgProduction);
    }

    private static ProductionLog? FindBaselineBeforeWindow(List<ProductionLog> candidates, string shiftName)
    {
        var seenDifferentShift = false;
        foreach (var log in candidates)
        {
            if (log.ShiftName == shiftName)
                return seenDifferentShift ? null : log;
            seenDifferentShift = true;
        }
        return null;
    }

    private static int Sorted(List<ProductionLog> logs, DateTime from, DateTime to)
        => ProductionReviewCalculations.CalculateProductionDeltaSorted(
            logs.OrderBy(l => l.Timestamp).ToList(), from, to);

    [Fact]
    public void EmptyLogs_ReturnsZero()
    {
        Assert.Equal(0, Sorted([], new DateTime(2026, 8, 10, 8, 0, 0), new DateTime(2026, 8, 11, 8, 0, 0)));
    }

    [Fact]
    public void NoLogsInWindow_ReturnsZero()
    {
        var logs = new List<ProductionLog> { Log(new DateTime(2026, 8, 9, 0, 0, 0), 10, 0) };
        Assert.Equal(0, Sorted(logs, new DateTime(2026, 8, 10, 8, 0, 0), new DateTime(2026, 8, 11, 8, 0, 0)));
    }

    [Fact]
    public void SimpleIncreasing_ReturnsFirstToLast()
    {
        var from = new DateTime(2026, 8, 10, 8, 0, 0);
        var logs = new List<ProductionLog>
        {
            Log(from.AddMinutes(0), 100, 5),
            Log(from.AddMinutes(10), 110, 6),
            Log(from.AddMinutes(20), 120, 7),
        };
        // 首条在窗口起点 → 基线 = 首条自身：差分 = (120-100) + (7-5) = 22
        Assert.Equal(22, Sorted(logs, from, from.AddHours(1)));
    }

    [Fact]
    public void BaselineFromBeforeWindow_UsedForDifference()
    {
        var from = new DateTime(2026, 8, 10, 8, 0, 0);
        var logs = new List<ProductionLog>
        {
            Log(from.AddHours(-1), 50, 1),       // 窗口前基线（白班）
            Log(from.AddMinutes(10), 80, 4),     // 窗口内（首条 > from → 用基线 50）
            Log(from.AddMinutes(20), 90, 5),
        };
        // (90-50) + (5-1) = 44
        Assert.Equal(44, Sorted(logs, from, from.AddHours(1)));
    }

    [Fact]
    public void ShiftChange_SplitsInstances()
    {
        var from = new DateTime(2026, 8, 10, 8, 0, 0);
        var logs = new List<ProductionLog>
        {
            Log(from.AddMinutes(0), 100, 5, "白班"),
            Log(from.AddMinutes(10), 120, 6, "白班"),
            Log(from.AddMinutes(20), 20, 0, "夜班"),   // 新实例（班次变化）
            Log(from.AddMinutes(30), 40, 1, "夜班"),
        };
        // 实例1：首条在窗口起点 → 基线=首条：120-100 + 6-5 = 21
        // 实例2：首条 > from，窗口前无夜班 → 基线=首条自身：40-20 + 1-0 = 21
        Assert.Equal(42, Sorted(logs, from, from.AddHours(1)));
    }

    [Fact]
    public void CounterReset_SplitsInstances()
    {
        var from = new DateTime(2026, 8, 10, 8, 0, 0);
        var logs = new List<ProductionLog>
        {
            Log(from.AddMinutes(0), 100, 5),
            Log(from.AddMinutes(10), 120, 6),
            Log(from.AddMinutes(20), 5, 0),   // 计数清零 → 新实例
            Log(from.AddMinutes(30), 15, 1),
        };
        // 实例1：120-100 + 6-5 = 21；实例2：15-5 + 1-0 = 11 → 32
        Assert.Equal(32, Sorted(logs, from, from.AddHours(1)));
    }

    [Fact]
    public void Randomized_MatchesReferenceImplementation()
    {
        var rng = new Random(42);
        var baseTime = new DateTime(2026, 8, 9, 0, 0, 0);
        var shifts = new[] { "白班", "夜班" };

        for (var round = 0; round < 30; round++)
        {
            // 随机生成 3 天日志（计数滚动 + 随机班次/清零）
            var logs = new List<ProductionLog>();
            var ok = rng.Next(0, 200);
            var ng = rng.Next(0, 20);
            var shift = shifts[rng.Next(2)];
            var t = baseTime;
            while (t < baseTime.AddDays(3))
            {
                t = t.AddMinutes(rng.Next(5, 45));
                // 30% 概率清零（新班次实例）、20% 概率换班次
                if (rng.NextDouble() < 0.3) { ok = 0; ng = 0; }
                if (rng.NextDouble() < 0.2) shift = shifts[rng.Next(2)];
                ok += rng.Next(1, 15);
                ng += rng.Next(0, 3);
                logs.Add(Log(t, ok, ng, shift));
            }

            // 随机窗口
            var from = baseTime.AddHours(rng.Next(0, 48));
            var to = from.AddHours(rng.Next(1, 30));

            var expected = ReferenceDelta(logs, from, to);
            var actual = Sorted(logs, from, to);
            Assert.Equal(expected, actual);
        }
    }
}
