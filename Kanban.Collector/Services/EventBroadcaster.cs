using System.Threading.Channels;
using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// 事件广播器：为报警/状态边沿事件分配单调递增 Seq，并维护最近 <see cref="RetentionCount"/> 条
/// 环形缓冲，供断线重连客户端按 LastSeq 补拉。
/// 采用"订阅者列表 + 扇出"模型：每个订阅者持有独立 channel，Publish 时写入全部订阅者，
/// 保证**每个**屏端都收到全部事件（多屏广播，非竞争消费）。
/// </summary>
public sealed class EventBroadcaster
{
    /// <summary>断线补拉窗口：最多保留最近事件数（覆盖 5s 落库延迟 + 客户端重连间隙）</summary>
    public const int RetentionCount = 4096;

    private readonly object _gate = new();
    private readonly LinkedList<(long Seq, object Payload)> _alarmRing = new();
    private readonly LinkedList<(long Seq, object Payload)> _statusRing = new();
    private readonly List<Channel<AlarmEventDto>> _alarmSubscribers = new();
    private readonly List<Channel<StatusEventDto>> _statusSubscribers = new();
    private readonly ILogger<EventBroadcaster> _logger;
    // 报警/状态各自独立计数：两条流互不占用对方序号，各自的 Seq 连续（补拉游标语义干净）
    private long _nextAlarmSeq = 1;
    private long _nextStatusSeq = 1;

    public EventBroadcaster(ILogger<EventBroadcaster> logger)
    {
        _logger = logger;
    }

    /// <summary>发布报警事件（分配报警流 Seq，写入环形缓冲 + 扇出广播）。
    /// 单临界区：seq 分配 + ring 写入 + 扇出原子完成——订阅注册（WatchAlarmEventsAsync 内补发 ring）
    /// 要么看到该事件已入 ring（补发路径收到）、要么订阅发生在扇出后（实时路径收到），
    /// 杜绝"两段锁"窗口下同一事件被补发与扇出各投递一次（破坏按 Seq 单次投递语义）。</summary>
    public void PublishAlarmEvent(AlarmEventDto evt)
    {
        lock (_gate)
        {
            var seq = _nextAlarmSeq++;
            var withSeq = evt with { Seq = seq };
            _alarmRing.AddLast((withSeq.Seq, withSeq));
            while (_alarmRing.Count > RetentionCount)
                _alarmRing.RemoveFirst();
            foreach (var subscriber in _alarmSubscribers)
            {
                subscriber.Writer.TryWrite(withSeq);
            }
            CollectorMetrics.TrackSubscriberCount(ref CollectorMetrics.EventSubscriberPeak, _alarmSubscribers.Count);
        }
        Interlocked.Increment(ref CollectorMetrics.AlarmEventPublishCount);
    }

    /// <summary>发布状态事件（分配状态流 Seq，写入环形缓冲 + 扇出广播）。
    /// 单临界区（理由同 <see cref="PublishAlarmEvent"/>）。</summary>
    public void PublishStatusEvent(StatusEventDto evt)
    {
        lock (_gate)
        {
            var seq = _nextStatusSeq++;
            var withSeq = evt with { Seq = seq };
            _statusRing.AddLast((withSeq.Seq, withSeq));
            while (_statusRing.Count > RetentionCount)
                _statusRing.RemoveFirst();
            foreach (var subscriber in _statusSubscribers)
            {
                subscriber.Writer.TryWrite(withSeq);
            }
            CollectorMetrics.TrackSubscriberCount(ref CollectorMetrics.EventSubscriberPeak, _statusSubscribers.Count);
        }
        Interlocked.Increment(ref CollectorMetrics.StatusEventPublishCount);
    }

    /// <summary>
    /// 订阅报警事件流：先补发 afterSeq 之后的环形缓冲事件，再实时转发。
    /// channel 创建 + 环形缓冲补发 + 订阅者注册在**同一个锁内原子完成**：
    /// 消除"补发完成但尚未注册"窗口——否则该窗口内到达的事件既不会被补发扫描到、
    /// 也不会写入本订阅者 channel，直接丢失。
    /// </summary>
    public async IAsyncEnumerable<AlarmEventDto> WatchAlarmEventsAsync(
        long afterSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 有界 + DropOldest：容量对齐 RetentionCount（补发需容纳全部 ring 条目，防补拉丢事件）；
        // 慢/停流客户端不再无界积压（无界缓冲可 OOM 拖垮采集进程），超容量丢最旧、保最新。
        var channel = Channel.CreateBounded<AlarmEventDto>(
            new BoundedChannelOptions(RetentionCount)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

        lock (_gate)
        {
            foreach (var (seq, payload) in _alarmRing)
            {
                if (seq > afterSeq)
                    channel.Writer.TryWrite((AlarmEventDto)payload);
            }
            _alarmSubscribers.Add(channel);
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _alarmSubscribers.Remove(channel);
            }
        });

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// 订阅状态事件流：先补发 afterSeq 之后的环形缓冲事件，再实时转发。
    /// 与报警订阅相同的原子注册语义（补发 + 注册同一临界区，无丢事件窗口）。
    /// </summary>
    public async IAsyncEnumerable<StatusEventDto> WatchStatusEventsAsync(
        long afterSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 有界 + DropOldest（理由同 WatchAlarmEventsAsync：容量对齐 RetentionCount，防无界积压 OOM）
        var channel = Channel.CreateBounded<StatusEventDto>(
            new BoundedChannelOptions(RetentionCount)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

        lock (_gate)
        {
            foreach (var (seq, payload) in _statusRing)
            {
                if (seq > afterSeq)
                    channel.Writer.TryWrite((StatusEventDto)payload);
            }
            _statusSubscribers.Add(channel);
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _statusSubscribers.Remove(channel);
            }
        });

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }
}
