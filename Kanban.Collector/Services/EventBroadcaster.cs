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

    /// <summary>发布报警事件（分配报警流 Seq，写入环形缓冲 + 扇出广播）</summary>
    public void PublishAlarmEvent(AlarmEventDto evt)
    {
        long seq;
        lock (_gate)
        {
            seq = _nextAlarmSeq++;
        }
        var withSeq = evt with { Seq = seq };
        lock (_gate)
        {
            _alarmRing.AddLast((withSeq.Seq, withSeq));
            while (_alarmRing.Count > RetentionCount)
                _alarmRing.RemoveFirst();
            foreach (var subscriber in _alarmSubscribers)
            {
                subscriber.Writer.TryWrite(withSeq);
            }
        }
    }

    /// <summary>发布状态事件（分配状态流 Seq，写入环形缓冲 + 扇出广播）</summary>
    public void PublishStatusEvent(StatusEventDto evt)
    {
        long seq;
        lock (_gate)
        {
            seq = _nextStatusSeq++;
        }
        var withSeq = evt with { Seq = seq };
        lock (_gate)
        {
            _statusRing.AddLast((withSeq.Seq, withSeq));
            while (_statusRing.Count > RetentionCount)
                _statusRing.RemoveFirst();
            foreach (var subscriber in _statusSubscribers)
            {
                subscriber.Writer.TryWrite(withSeq);
            }
        }
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
        var channel = Channel.CreateUnbounded<AlarmEventDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

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
        var channel = Channel.CreateUnbounded<StatusEventDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

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
