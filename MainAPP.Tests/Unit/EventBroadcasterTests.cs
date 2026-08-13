using System.Threading.Channels;
using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// EventBroadcaster 测试：锁住事件流核心（P1-4/5 重构成果）——
/// - 报警/状态**分用独立 Seq**（互不占号，各自连续，补拉游标语义干净）
/// - 环形缓冲补发（afterSeq 之后的事件不丢）+ 原子订阅（无补发/注册窗口丢事件）
/// - 多订阅者广播
/// 此前服务端事件流 0% 覆盖，本文件直接锁住。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class EventBroadcasterTests
{
    private static EventBroadcaster CreateBroadcaster()
        => new(Substitute.For<ILogger<EventBroadcaster>>());

    private static AlarmEventDto Alarm() => new()
    {
        DeviceId = "dev-1", DeviceName = "注塑机-1", AlarmId = "A1", AlarmName = "报警A",
        PlcAddress = "M100", Level = AlarmLevel.High, ShiftName = "白班",
        EventType = AlarmEventType.Triggered, EventTime = DateTime.Now,
    };

    private static StatusEventDto Status() => new()
    {
        DeviceId = "dev-1", DeviceName = "注塑机-1",
        PreviousState = DeviceStatus.Running, CurrentState = DeviceStatus.Alarm,
        EventTime = DateTime.Now, ShiftName = "白班",
    };

    /// <summary>订阅报警流并把 yield 事件转发到测试 channel（afterSeq 补发随首次 MoveNext 同步完成）。</summary>
    private static async Task<ChannelReader<AlarmEventDto>> SubscribeAlarmAsync(EventBroadcaster bc, long afterSeq, CancellationTokenSource? cts = null)
    {
        cts ??= new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<AlarmEventDto>();
        var enumerator = bc.WatchAlarmEventsAsync(afterSeq, cts.Token).GetAsyncEnumerator();
        _ = Task.Run(async () =>
        {
            try
            {
                while (await enumerator.MoveNextAsync())
                    channel.Writer.TryWrite(enumerator.Current);
            }
            catch (OperationCanceledException) { }
            finally { channel.Writer.TryComplete(); }
        });
        await Task.Yield(); // 让枚举任务启动（补发在首次 MoveNext 完成）
        return channel.Reader;
    }

    private static async Task<ChannelReader<StatusEventDto>> SubscribeStatusAsync(EventBroadcaster bc, long afterSeq, CancellationTokenSource? cts = null)
    {
        cts ??= new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<StatusEventDto>();
        var enumerator = bc.WatchStatusEventsAsync(afterSeq, cts.Token).GetAsyncEnumerator();
        _ = Task.Run(async () =>
        {
            try
            {
                while (await enumerator.MoveNextAsync())
                    channel.Writer.TryWrite(enumerator.Current);
            }
            catch (OperationCanceledException) { }
            finally { channel.Writer.TryComplete(); }
        });
        await Task.Yield();
        return channel.Reader;
    }

    // ──────────── Seq 分计 ────────────

    [Fact]
    public async Task AlarmAndStatus_HaveIndependentSequences()
    {
        var bc = CreateBroadcaster();
        var alarmReader = await SubscribeAlarmAsync(bc, 0);
        var statusReader = await SubscribeStatusAsync(bc, 0);

        bc.PublishAlarmEvent(Alarm());   // 报警 seq=1
        bc.PublishStatusEvent(Status()); // 状态 seq=1（不占用报警号）
        bc.PublishAlarmEvent(Alarm());   // 报警 seq=2

        Assert.Equal(1, (await alarmReader.ReadAsync(CancellationToken.None)).Seq);
        Assert.Equal(1, (await statusReader.ReadAsync(CancellationToken.None)).Seq); // 状态从 1 起，未被报警跳号
        Assert.Equal(2, (await alarmReader.ReadAsync(CancellationToken.None)).Seq); // 报警连续 1,2
    }

    // ──────────── 环形缓冲补发 ────────────

    [Fact]
    public async Task SubscribeAfterEvents_ReplaysFromAfterSeq()
    {
        var bc = CreateBroadcaster();
        bc.PublishAlarmEvent(Alarm()); // seq=1（订阅前发布）
        bc.PublishAlarmEvent(Alarm()); // seq=2
        bc.PublishAlarmEvent(Alarm()); // seq=3

        var reader = await SubscribeAlarmAsync(bc, afterSeq: 1); // 补发 2,3
        Assert.Equal(2, (await reader.ReadAsync(CancellationToken.None)).Seq);
        Assert.Equal(3, (await reader.ReadAsync(CancellationToken.None)).Seq);
    }

    [Fact]
    public async Task SubscribeAfterSeq0_ReplaysAll()
    {
        var bc = CreateBroadcaster();
        bc.PublishAlarmEvent(Alarm()); // seq=1
        bc.PublishAlarmEvent(Alarm()); // seq=2

        var reader = await SubscribeAlarmAsync(bc, afterSeq: 0);
        Assert.Equal(1, (await reader.ReadAsync(CancellationToken.None)).Seq);
        Assert.Equal(2, (await reader.ReadAsync(CancellationToken.None)).Seq);
    }

    // ──────────── 原子订阅：无窗口丢事件 ────────────

    [Fact]
    public async Task PublishWhileSubscribing_NoEventLost()
    {
        var bc = CreateBroadcaster();
        var reader = await SubscribeAlarmAsync(bc, 0);

        bc.PublishAlarmEvent(Alarm()); // seq=1
        bc.PublishAlarmEvent(Alarm()); // seq=2

        Assert.Equal(1, (await reader.ReadAsync(CancellationToken.None)).Seq);
        Assert.Equal(2, (await reader.ReadAsync(CancellationToken.None)).Seq);
    }

    // ──────────── 多订阅者广播 ────────────

    [Fact]
    public async Task MultipleSubscribers_AllReceiveSameEvents()
    {
        var bc = CreateBroadcaster();
        var r1 = await SubscribeAlarmAsync(bc, 0);
        var r2 = await SubscribeAlarmAsync(bc, 0);

        bc.PublishAlarmEvent(Alarm());

        Assert.Equal(1, (await r1.ReadAsync(CancellationToken.None)).Seq);
        Assert.Equal(1, (await r2.ReadAsync(CancellationToken.None)).Seq);
    }

    // ──────────── 状态流补拉 ────────────

    [Fact]
    public async Task StatusFlow_AfterSeqResume()
    {
        var bc = CreateBroadcaster();
        bc.PublishStatusEvent(Status()); // seq=1
        bc.PublishStatusEvent(Status()); // seq=2

        var reader = await SubscribeStatusAsync(bc, afterSeq: 1);
        Assert.Equal(2, (await reader.ReadAsync(CancellationToken.None)).Seq);
    }

    // ──────────── ServerEpoch（Collector 重启后客户端游标重置依据） ────────────

    [Fact]
    public async Task PublishedAlarmEvent_CarriesServerEpoch()
    {
        var bc = CreateBroadcaster();
        var reader = await SubscribeAlarmAsync(bc, 0);

        bc.PublishAlarmEvent(Alarm());

        var evt = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(bc.ServerEpoch, evt.ServerEpoch);
        Assert.NotEqual(0, bc.ServerEpoch); // TickCount64 进程级纪元非零
    }

    [Fact]
    public async Task PublishedStatusEvent_CarriesServerEpoch()
    {
        // 回归：状态流此前未 stamp ServerEpoch（半成品），Collector 重启后客户端
        // 无法区分"新进程低 Seq 事件"，旧游标会把新进程事件全部过滤（漏报）。
        var bc = CreateBroadcaster();
        var reader = await SubscribeStatusAsync(bc, 0);

        bc.PublishStatusEvent(Status());

        var evt = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(bc.ServerEpoch, evt.ServerEpoch);
        Assert.NotEqual(0, bc.ServerEpoch);
    }

    [Fact]
    public async Task StatusReplay_AlsoCarriesServerEpoch()
    {
        // 补拉路径（环形缓冲补发）同样必须带 epoch，否则断线重连补发的事件
        // 无法参与客户端纪元判断。
        var bc = CreateBroadcaster();
        bc.PublishStatusEvent(Status()); // seq=1（订阅前）

        var reader = await SubscribeStatusAsync(bc, afterSeq: 0);
        var evt = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(bc.ServerEpoch, evt.ServerEpoch);
        Assert.Equal(1, evt.Seq);
    }

    // ──────────── 背压语义：有界 Channel + DropOldest（#3 重构成果回归） ────────────

    /// <summary>
    /// 慢订阅者（注册后暂停消费）：发布量超过 channel 容量（4096）时丢最旧保最新，
    /// 恢复消费后收到的是**连续无缺号**的最新 4096 条——既不 OOM（有界）也不出现空洞。
    /// 回归背景：曾有实现用无界缓冲（慢/停流客户端可 OOM 拖垮采集进程）。
    /// </summary>
    [Fact]
    public async Task SlowSubscriber_BoundedChannel_DropsOldest_KeepsLatest()
    {
        const int capacity = EventBroadcaster.RetentionCount; // 4096
        var bc = CreateBroadcaster();
        var enumerator = bc.WatchAlarmEventsAsync(0, CancellationToken.None).GetAsyncEnumerator();

        // 首次 MoveNext：完成订阅注册（补发为空），并消费 seq=1
        var first = enumerator.MoveNextAsync();
        bc.PublishAlarmEvent(Alarm()); // seq=1
        Assert.True(await first);
        Assert.Equal(1, enumerator.Current.Seq);

        // 暂停消费（模拟慢/停流客户端）：快速发布直至远超容量
        for (var i = 2; i <= 5000; i++)
            bc.PublishAlarmEvent(Alarm());

        // 恢复消费：channel 容量 4096，丢最旧（seq 2..904 被丢弃），剩余 905..5000 连续无缺号
        // prev 初值 = 首条序号 905 的前一条（904 = 5000-4096），否则第一条连续性断言必失败
        long prev = 5000 - capacity;
        for (var i = 0; i < capacity; i++)
        {
            Assert.True(await enumerator.MoveNextAsync());
            var seq = enumerator.Current.Seq;
            Assert.Equal(prev + 1, seq); // 连续性：不丢中间、不重号
            prev = seq;
        }
        Assert.Equal(5000, prev);                                     // 最新一条保留
        Assert.Equal(5000 - capacity + 1, 905);                      // 首条序号 = 5000-4096+1 = 905
        Assert.Equal(5000 - capacity + 1, prev - (capacity - 1));     // 与收到的第一条一致
        await enumerator.DisposeAsync();
    }

    /// <summary>
    /// 环形缓冲保留最近 4096 条：晚订阅（afterSeq=0）补拉只含保留段（最旧已淘汰），
    /// 与 channel DropOldest 语义对齐——补拉窗口 = 实时容量的覆盖范围。
    /// </summary>
    [Fact]
    public async Task Ring_RetainsLatestRetentionCount_ReplayGetsOnlyRetainedWindow()
    {
        const int capacity = EventBroadcaster.RetentionCount; // 4096
        var bc = CreateBroadcaster();
        for (var i = 1; i <= 4200; i++)
            bc.PublishAlarmEvent(Alarm()); // seq=1..4200（无订阅者，仅入 ring）

        // 晚订阅 afterSeq=0：补发 ring 保留的最近 4096 条（seq 105..4200）
        var reader = await SubscribeAlarmAsync(bc, afterSeq: 0);
        var first = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(4200 - capacity + 1, first.Seq); // 105：最旧一条已被淘汰

        long prev = first.Seq;
        for (var i = 1; i < capacity; i++)
        {
            var evt = await reader.ReadAsync(CancellationToken.None);
            Assert.Equal(prev + 1, evt.Seq);
            prev = evt.Seq;
        }
        Assert.Equal(4200, prev); // 最新一条在补拉尾部
    }
}
