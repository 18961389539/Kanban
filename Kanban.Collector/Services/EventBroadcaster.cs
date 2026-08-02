using System.Threading.Channels;
using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Services;

/// <summary>
/// 事件广播器：为报警/状态边沿事件分配单调递增 Seq，并维护最近 <see cref="RetentionCount"/> 条
/// 环形缓冲，供断线重连客户端按 LastSeq 补拉。
/// 第 3 步由采集服务边沿检测（迁入）驱动发布。
/// </summary>
public sealed class EventBroadcaster
{
    /// <summary>断线补拉窗口：最多保留最近事件数（覆盖 5s 落库延迟 + 客户端重连间隙）</summary>
    public const int RetentionCount = 4096;

    private readonly object _gate = new();
    private readonly LinkedList<(long Seq, object Payload)> _alarmRing = new();
    private readonly LinkedList<(long Seq, object Payload)> _statusRing = new();
    private readonly Channel<AlarmEventDto> _alarmChannel = Channel.CreateUnbounded<AlarmEventDto>();
    private readonly Channel<StatusEventDto> _statusChannel = Channel.CreateUnbounded<StatusEventDto>();
    private readonly ILogger<EventBroadcaster> _logger;
    private long _nextSeq = 1;

    public EventBroadcaster(ILogger<EventBroadcaster> logger)
    {
        _logger = logger;
    }

    /// <summary>发布报警事件（分配 Seq，写入环形缓冲 + 广播）</summary>
    public void PublishAlarmEvent(AlarmEventDto evt)
    {
        var withSeq = evt with { Seq = NextSeq() };
        lock (_gate)
        {
            _alarmRing.AddLast((withSeq.Seq, withSeq));
            while (_alarmRing.Count > RetentionCount)
                _alarmRing.RemoveFirst();
        }
        _alarmChannel.Writer.TryWrite(withSeq);
    }

    /// <summary>发布状态事件（分配 Seq，写入环形缓冲 + 广播）</summary>
    public void PublishStatusEvent(StatusEventDto evt)
    {
        var withSeq = evt with { Seq = NextSeq() };
        lock (_gate)
        {
            _statusRing.AddLast((withSeq.Seq, withSeq));
            while (_statusRing.Count > RetentionCount)
                _statusRing.RemoveFirst();
        }
        _statusChannel.Writer.TryWrite(withSeq);
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
        // 实时转发
        await foreach (var evt in _alarmChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// 订阅状态事件流：先补发 LastSeq 之后的环形缓冲事件，再实时转发。
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
        await foreach (var evt in _statusChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }
}
