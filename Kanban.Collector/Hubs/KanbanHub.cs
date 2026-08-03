using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Hubs;

/// <summary>
/// SignalR 强类型 Hub：实现 <see cref="IKanbanHubServer"/>（监控域）与 <see cref="IKanbanAdminServer"/>（管理域），
/// 客户端回调走 <see cref="IKanbanHubClient"/> 强类型接口。
/// SignalR 方法签名不含 CancellationToken（见契约说明），取消用 Context.ConnectionAborted。
/// </summary>
public sealed class KanbanHub : Hub<IKanbanHubClient>, IKanbanHubServer, IKanbanAdminServer
{
    private readonly SnapshotAggregator _snapshotAggregator;
    private readonly EventBroadcaster _eventBroadcaster;
    private readonly HistoryQueryHandler _historyQueryHandler;
    private readonly CollectorDiagnosticsProvider _diagnosticsProvider;
    private readonly ConfigSyncHandler _configSyncHandler;
    private readonly ShiftProgressProvider _shiftProgressProvider;
    private readonly MetaPublisher _metaPublisher;
    private readonly ILogger<KanbanHub> _logger;

    public KanbanHub(
        SnapshotAggregator snapshotAggregator,
        EventBroadcaster eventBroadcaster,
        HistoryQueryHandler historyQueryHandler,
        CollectorDiagnosticsProvider diagnosticsProvider,
        ConfigSyncHandler configSyncHandler,
        ShiftProgressProvider shiftProgressProvider,
        MetaPublisher metaPublisher,
        ILogger<KanbanHub> logger)
    {
        _snapshotAggregator = snapshotAggregator;
        _eventBroadcaster = eventBroadcaster;
        _historyQueryHandler = historyQueryHandler;
        _diagnosticsProvider = diagnosticsProvider;
        _configSyncHandler = configSyncHandler;
        _shiftProgressProvider = shiftProgressProvider;
        _metaPublisher = metaPublisher;
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
        => _historyQueryHandler.QueryAsync(request, Context.ConnectionAborted);

    /// <summary>运行监控页 Remote 模式：拉取 Collector 采集/历史/连接诊断快照。</summary>
    public Task<CollectorDiagnosticsDto> GetDiagnosticsAsync()
        => Task.FromResult(_diagnosticsProvider.GetSnapshot());

    /// <inheritdoc />
    public Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices)
        => _configSyncHandler.SaveDevicesAsync(devices);

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync()
        => _configSyncHandler.GetDevicesAsync();

    /// <inheritdoc />
    public Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder)
        => _configSyncHandler.UpsertWorkOrderAsync(workOrder);

    /// <inheritdoc />
    public Task DeleteWorkOrderAsync(int workOrderId)
        => _configSyncHandler.DeleteWorkOrderAsync(workOrderId);

    /// <inheritdoc />
    public Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId)
        => _configSyncHandler.GetCurrentWorkOrderAsync(deviceId);

    /// <inheritdoc />
    public Task<ShiftProgressDto> GetShiftProgressAsync()
        => Task.FromResult(_shiftProgressProvider.GetProgress());

    /// <inheritdoc />
    public async Task SubscribeMetaAsync()
    {
        var channel = await _metaPublisher.SubscribeAsync(Context.ConnectionAborted);

        // 逐条转发：客户端回调 OnMeta
        await foreach (var meta in channel.ReadAllAsync(Context.ConnectionAborted))
        {
            await Clients.Caller.OnMeta(meta);
        }
    }

    /// <inheritdoc />
    public Task SaveCollectorSettingsAsync(CollectorSettingsDto settings)
        => _configSyncHandler.SaveCollectorSettingsAsync(settings);

    /// <inheritdoc />
    public Task<string> GetServerVersionAsync()
        => Task.FromResult(_configSyncHandler.GetServerVersion());

    /// <inheritdoc />
    public Task<string> GetTitleAsync()
        => Task.FromResult(_configSyncHandler.GetTitle());

    /// <inheritdoc />
    public Task<int> GetLanguageAsync()
        => Task.FromResult(_configSyncHandler.GetLanguage());
}
