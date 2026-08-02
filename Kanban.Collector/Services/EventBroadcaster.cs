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
    private long _nextSeq = 1;

    public EventBroadcaster(ILogger<EventBroadcaster> logger)
    {
        _logger = logger;
    }

    /// <summary>发布报警事件（分配 Seq，写入环形缓冲 + 扇出广播）</summary>
    public void PublishAlarmEvent(AlarmEventDto evt)
    {
        var withSeq = evt with { Seq = NextSeq() };
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

    /// <summary>发布状态事件（分配 Seq，写入环形缓冲 + 扇出广播）</summary>
    public void PublishStatusEvent(StatusEventDto evt)
    {
        var withSeq = evt with { Seq = NextSeq() };
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

    private long NextSeq()
    {
        lock (_gate)
        {
            return _nextSeq++;
        }
    }

    /// <summary>
    /// 订阅报警事件流：先补发 LastSeq 之后的环形缓冲事件，再实时转发。
    /// 订阅时注册专属 channel；连接断开（cancellationToken 触发）时自动退订。
    /// </summary>
    public async IAsyncEnumerable<AlarmEventDto> WatchAlarmEventsAsync(
        long afterSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 补发窗口内未消费事件
        lock (_gate)
        {
            foreach (var (seq, payload) in _alarmRing)
            {
                if (seq > afterSeq)
                    yield return (AlarmEventDto)payload;
            }
        }
        var channel = RegisterAlarmSubscriber(cancellationToken);
        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// 订阅状态事件流：先补发 LastSeq 之后的环形缓冲事件，再实时转发。
    /// 订阅时注册专属 channel；连接断开（cancellationToken 触发）时自动退订。
    /// </summary>
    public async IAsyncEnumerable<StatusEventDto> WatchStatusEventsAsync(
        long afterSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var (seq, payload) in _statusRing)
            {
                if (seq > afterSeq)
                    yield return (StatusEventDto)payload;
            }
        }
        var channel = RegisterStatusSubscriber(cancellationToken);
        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }

    private Channel<AlarmEventDto> RegisterAlarmSubscriber(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<AlarmEventDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        lock (_gate)
        {
            _alarmSubscribers.Add(channel);
        }
        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _alarmSubscribers.Remove(channel);
            }
        });
        return channel;
    }

    private Channel<StatusEventDto> RegisterStatusSubscriber(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<StatusEventDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        lock (_gate)
        {
            _statusSubscribers.Add(channel);
        }
        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _statusSubscribers.Remove(channel);
            }
        });
        return channel;
    }
}
