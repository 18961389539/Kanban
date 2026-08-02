using Kanban.Contracts.Dtos;

namespace Kanban.Contracts.Abstractions;

/// <summary>
/// 事件流服务：推送报警/状态边沿事件。
/// 客户端断线重连后带 LastSeq 补拉，保证事件不丢。
/// </summary>
public interface IEventStreamService
{
    /// <summary>
    /// 订阅报警事件流。afterSeq = 客户端已消费的最大序号（0 = 从头订阅）。
    /// </summary>
    IAsyncEnumerable<AlarmEventDto> WatchAlarmEvents(long afterSeq, CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅状态转换事件流。afterSeq = 客户端已消费的最大序号（0 = 从头订阅）。
    /// </summary>
    IAsyncEnumerable<StatusEventDto> WatchStatusEvents(long afterSeq, CancellationToken cancellationToken = default);
}
