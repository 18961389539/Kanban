using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
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
    public void BuildProductionDeltas_UsesPreviousSnapshotAndIgnoresBaselineBucket()
    {
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

        Assert.Equal([10, 15], ok);
        Assert.Equal([1, 1], ng);
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
