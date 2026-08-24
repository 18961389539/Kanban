using System.Threading.Channels;
using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// SnapshotAggregator 测试：锁住快照流核心语义（#3 有界化 + DropOldest 背压回归补盲）——
/// - 订阅补发当前最新快照（连接即有数据）
/// - 慢/停流订阅者有界 128 + DropOldest：丢最旧保最新、保留段连续无空洞
///   （回归背景：曾有实现用无界缓冲，慢/停流客户端（如 WASM 切后台）可 OOM 拖垮采集进程）
/// - RemoveDevice 广播 tombstone + 从最新副本移除
/// - 取消订阅退订 + 多订阅者广播 + 同设备 upsert 覆盖
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class SnapshotAggregatorTests
{
    /// <summary>与 SnapshotAggregator.SubscribeAsync 的有界容量保持一致（128）。</summary>
    private const int Capacity = 128;

    private static SnapshotAggregator CreateAggregator() => new();

    private static DeviceSnapshotDto Snapshot(string deviceId, long seq) => new()
    {
        DeviceId = deviceId,
        DeviceName = $"设备-{deviceId}",
        Status = DeviceStatus.Running,
        Seq = seq,
        Timestamp = DateTime.Now,
    };

    // ──────────── 订阅补发（连接即有数据） ────────────

    [Fact]
    public async Task Subscribe_BackfillsLatestSnapshots()
    {
        var agg = CreateAggregator();
        agg.Publish(Snapshot("dev-1", 1));
        agg.Publish(Snapshot("dev-2", 2));

        var reader = await agg.SubscribeAsync(TestContext.Current.CancellationToken);

        Assert.True(reader.TryRead(out var s1));
        Assert.Equal("dev-1", s1.DeviceId);
        Assert.True(reader.TryRead(out var s2));
        Assert.Equal("dev-2", s2.DeviceId);
        Assert.False(reader.TryRead(out _)); // 无更多
    }

    [Fact]
    public async Task Subscribe_BeforeAnyPublish_EmptyReader()
    {
        var agg = CreateAggregator();

        var reader = await agg.SubscribeAsync(TestContext.Current.CancellationToken);

        Assert.False(reader.TryRead(out _));
    }

    // ──────────── 背压语义：有界 Channel + DropOldest（#3 重构成果回归） ────────────

    /// <summary>
    /// 慢订阅者（注册后暂停消费）：发布量超过 channel 容量（128）时丢最旧保最新，
    /// 恢复消费后收到的是**连续无缺号**的最新 128 条——既不 OOM（有界）也不出现空洞。
    /// </summary>
    [Fact]
    public async Task SlowSubscriber_BoundedChannel_DropsOldest_KeepsLatest()
    {
        var agg = CreateAggregator();
        var reader = await agg.SubscribeAsync(TestContext.Current.CancellationToken);

        // 暂停消费：快速发布直至远超容量（同设备：Seq 递增标识先后）
        for (var i = 1; i <= 300; i++)
            agg.Publish(Snapshot("dev-a", i));

        // 恢复消费：channel 容量 128，丢最旧（seq 1..172 被丢弃），剩余 173..300 连续无缺号
        var received = new List<DeviceSnapshotDto>();
        while (reader.TryRead(out var snap))
            received.Add(snap);

        Assert.Equal(Capacity, received.Count);
        Assert.Equal(300 - Capacity + 1, received[0].Seq); // 173：首条保留
        for (var i = 0; i < received.Count; i++)
            Assert.Equal(173 + i, received[i].Seq); // 连续：不丢中间、不重号
        Assert.Equal(300, received[^1].Seq);         // 最新一条保留
    }

    /// <summary>慢订阅者场景下同设备最新状态保留：最后读到的是最新一次发布的快照。</summary>
    [Fact]
    public async Task SlowSubscriber_LatestPublishWins()
    {
        var agg = CreateAggregator();
        var reader = await agg.SubscribeAsync(TestContext.Current.CancellationToken);

        for (var i = 1; i <= 200; i++)
            agg.Publish(Snapshot("dev-a", i));

        var last = default(DeviceSnapshotDto);
        while (reader.TryRead(out var snap))
            last = snap;

        Assert.NotNull(last);
        Assert.Equal(200, last.Seq);
    }

    // ──────────── RemoveDevice：tombstone 广播 + 最新副本移除 ────────────

    [Fact]
    public async Task RemoveDevice_BroadcastsTombstone_AndRemovesFromLatest()
    {
        var agg = CreateAggregator();
        agg.Publish(Snapshot("dev-1", 1));
        var reader = await agg.SubscribeAsync(TestContext.Current.CancellationToken);
        Assert.True(reader.TryRead(out _)); // 补发的 dev-1

        agg.RemoveDevice("dev-1");

        Assert.True(reader.TryRead(out var tomb));
        Assert.True(tomb.Removed);
        Assert.Equal("dev-1", tomb.DeviceId);
        Assert.False(reader.TryRead(out _));

        var current = await agg.GetCurrentSnapshotsAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(current, s => s.DeviceId == "dev-1");
    }

    // ──────────── 退订 / 多订阅者 / upsert ────────────

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit", "xUnit1051",
        Justification = "This test intentionally cancels an independent linked token to verify unsubscription.")]
    [Fact]
    public async Task CancelSubscription_Unsubscribes_NoThrowOnPublish()
    {
        var agg = CreateAggregator();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reader = await agg.SubscribeAsync(cts.Token);
        cts.Cancel();

        agg.Publish(Snapshot("dev-1", 1)); // 不抛（退订后的扇出不触达已取消订阅者）

        // 新订阅者不受影响：补发正常
        var reader2 = await agg.SubscribeAsync(TestContext.Current.CancellationToken);
        Assert.True(reader2.TryRead(out var snap));
        Assert.Equal(1, snap.Seq);
    }

    [Fact]
    public async Task MultipleSubscribers_AllReceiveSameSnapshots()
    {
        var agg = CreateAggregator();
        var r1 = await agg.SubscribeAsync(TestContext.Current.CancellationToken);
        var r2 = await agg.SubscribeAsync(TestContext.Current.CancellationToken);

        agg.Publish(Snapshot("dev-1", 1));

        Assert.True(r1.TryRead(out var s1));
        Assert.Equal("dev-1", s1.DeviceId);
        Assert.True(r2.TryRead(out var s2));
        Assert.Equal("dev-1", s2.DeviceId);
    }

    [Fact]
    public async Task Publish_SameDevice_UpsertsLatest()
    {
        var agg = CreateAggregator();
        agg.Publish(Snapshot("dev-1", 1));
        agg.Publish(Snapshot("dev-1", 2)); // 同设备更新

        var current = await agg.GetCurrentSnapshotsAsync(TestContext.Current.CancellationToken);
        var snap = Assert.Single(current);
        Assert.Equal("dev-1", snap.DeviceId);
        Assert.Equal(2, snap.Seq);
    }
}
