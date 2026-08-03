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
}
