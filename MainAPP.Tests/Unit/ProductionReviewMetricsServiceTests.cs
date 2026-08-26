using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ProductionReviewMetricsServiceTests
{
    private readonly ProductionReviewMetricsService _service =
        new(new EmptyProductionReviewDataService());

    [Fact]
    public void BuildBuckets_AlignsToConfiguredBucketSize()
    {
        var from = new DateTime(2026, 1, 2, 10, 7, 30);
        var to = new DateTime(2026, 1, 2, 10, 17, 30);

        var buckets = _service.BuildBuckets(from, to, ProductionReviewBucketSize.Minute5);

        Assert.Equal(
            [
                new DateTime(2026, 1, 2, 10, 5, 0),
                new DateTime(2026, 1, 2, 10, 10, 0),
                new DateTime(2026, 1, 2, 10, 15, 0),
            ],
            buckets);
    }

    [Fact]
    public void BuildProductionDeltas_WindowStartSnapshotIsBaseline_NoOuterIncrement()
    {
        // 窗口起点（10:00）恰有快照：其累计值即实例基线，窗口前(-5min)到窗口起点之间的增量
        // 发生在窗口外，不再计入趋势图首桶（2026-08-26 P1 修复，与 SumWindowProduction 口径一致）。
        var from = new DateTime(2026, 1, 2, 10, 0, 0);
        var logs = new List<ProductionLog>
        {
            new() { Timestamp = from.AddMinutes(-5), ShiftName = "白班", OkProduction = 100, NgProduction = 2 },
            new() { Timestamp = from, ShiftName = "白班", OkProduction = 110, NgProduction = 3 },
            new() { Timestamp = from.AddMinutes(5), ShiftName = "白班", OkProduction = 125, NgProduction = 4 },
        };
        var buckets = _service.BuildBuckets(from, from.AddMinutes(5), ProductionReviewBucketSize.Minute5);

        var (ok, ng) = _service.BuildProductionDeltas(
            logs,
            from,
            buckets,
            ProductionReviewBucketSize.Minute5);

        // 110-110=0（基线自身，跨窗口边界增量不计入首桶）；125-110=15
        Assert.Equal([0, 15], ok);
        Assert.Equal([0, 1], ng);
    }

    [Fact]
    public void BuildProductionDeltas_Sum_Equals_SumWindowProduction()
    {
        // 核心恒等保证（2026-08-26 P1）：趋势图各桶之和 == 总产量。
        // 覆盖：跨窗口延续实例（窗口前同实例）→ 基线取窗口前末条；
        // 数据缺口/更早班次实例（累计回落切组）→ 不污染基线；
        // 孤立单条实例组 → 差分记 0（非累计值）。
        var from = new DateTime(2026, 1, 2, 10, 0, 0);
        var logs = new List<ProductionLog>
        {
            // 更早班次实例（数据缺口模拟）：累计 999 > 0 回落 → 切组，不得污染白班基线
            new() { Timestamp = from.AddDays(-3), ShiftName = "白班", OkProduction = 999, NgProduction = 9 },
            // 跨窗口延续实例：窗口前 08:00 起，09:30=90（窗口起点无快照）
            new() { Timestamp = from.AddHours(-2), ShiftName = "白班", OkProduction = 0, NgProduction = 0 },
            new() { Timestamp = from.AddHours(-0.5), ShiftName = "白班", OkProduction = 90, NgProduction = 1 },
            // 窗口内同实例续产
            new() { Timestamp = from.AddHours(0.5), ShiftName = "白班", OkProduction = 120, NgProduction = 2 },
            new() { Timestamp = from.AddHours(1), ShiftName = "白班", OkProduction = 150, NgProduction = 3 },
            // 班次切换（夜班累计回落）→ 新实例；窗口前无夜班日志 → 孤立单条组
            new() { Timestamp = from.AddHours(2), ShiftName = "夜班", OkProduction = 5, NgProduction = 0 },
        };
        var buckets = _service.BuildBuckets(from, from.AddHours(2), ProductionReviewBucketSize.Hour);

        var (trendOk, trendNg) = _service.BuildProductionDeltas(
            logs, from, buckets, ProductionReviewBucketSize.Hour);
        var windowLogs = logs.Where(l => l.Timestamp >= from).ToList();
        var baseline = logs.Where(l => l.Timestamp < from).ToList();
        var (totalOk, totalNg) = HistoryQueryHelper.SumWindowProduction(windowLogs, baseline, from);

        Assert.Equal(totalOk, trendOk.Sum()); // 150-90=60；999 组不入窗口；夜班单条组记 0
        Assert.Equal(totalNg, trendNg.Sum()); // 3-1=2
    }

    private sealed class EmptyProductionReviewDataService : IProductionReviewDataService
    {
        public ProductionReviewDataSnapshot QueryWindow(
            DateTime from,
            DateTime to,
            IReadOnlyList<string> deviceIds)
            => new([], [], []);

        public DeviceReviewRangeData QueryDeviceRange(string deviceId, DateTime from, DateTime to)
            => new([], [], null, []);

        public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before)
            => null;

        public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string deviceId)
            => [];
    }
}
