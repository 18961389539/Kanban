using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Hubs;

/// <summary>
/// SignalR 强类型 Hub：实现 <see cref="IKanbanHubServer"/> 方法，
/// 客户端回调走 <see cref="IKanbanHubClient"/> 强类型接口。
/// SignalR 方法签名不含 CancellationToken（见契约说明），取消用 Context.ConnectionAborted。
/// </summary>
public sealed class KanbanHub : Hub<IKanbanHubClient>, IKanbanHubServer
{
    private readonly SnapshotAggregator _snapshotAggregator;
    private readonly EventBroadcaster _eventBroadcaster;
    private readonly ILogger<KanbanHub> _logger;

    public KanbanHub(SnapshotAggregator snapshotAggregator, EventBroadcaster eventBroadcaster, ILogger<KanbanHub> logger)
    {
        _snapshotAggregator = snapshotAggregator;
        _eventBroadcaster = eventBroadcaster;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync()
        => _snapshotAggregator.GetCurrentSnapshotsAsync(Context.ConnectionAborted);

    /// <inheritdoc />
    public async Task SubscribeSnapshotsAsync()
    {
        var channel = await _snapshotAggregator.SubscribeAsync(Context.ConnectionAborted);

        // 逐条转发：客户端回调 OnSnapshot
        await foreach (var snapshot in channel.ReadAllAsync(Context.ConnectionAborted))
        {
            await Clients.Caller.OnSnapshot(snapshot);
        }
    }

    /// <inheritdoc />
    public async Task SubscribeAlarmEventsAsync(long afterSeq)
    {
        await foreach (var evt in _eventBroadcaster.WatchAlarmEventsAsync(afterSeq, Context.ConnectionAborted))
        {
            await Clients.Caller.OnAlarmEvent(evt);
        }
    }

    /// <inheritdoc />
    public async Task SubscribeStatusEventsAsync(long afterSeq)
    {
        await foreach (var evt in _eventBroadcaster.WatchStatusEventsAsync(afterSeq, Context.ConnectionAborted))
        {
            await Clients.Caller.OnStatusEvent(evt);
        }
    }

    /// <inheritdoc />
    public Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request)
        => _eventBroadcaster.QueryHistoryAsync(request, Context.ConnectionAborted);
}
