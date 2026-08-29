using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Services;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Hubs;

/// <summary>
/// SignalR 强类型 Hub：实现 <see cref="IKanbanHubServer"/>（只读监控域），
/// 客户端回调走 <see cref="IKanbanHubClient"/> 强类型接口。
/// SignalR 方法签名不含 CancellationToken（见契约说明），取消用 Context.ConnectionAborted。
/// </summary>
public sealed class KanbanHub : Hub<IKanbanHubClient>, IKanbanHubServer
{
    private readonly SnapshotAggregator _snapshotAggregator;
    private readonly EventBroadcaster _eventBroadcaster;
    private readonly HistoryQueryHandler _historyQueryHandler;
    private readonly CollectorDiagnosticsProvider _diagnosticsProvider;
    private readonly ConfigSyncHandler _configSyncHandler;
    private readonly ShiftProgressProvider _shiftProgressProvider;
    private readonly MetaPublisher _metaPublisher;
    private readonly WorkOrderRepository _workOrderRepository;
    private readonly HistoryService _historyService;
    private readonly AppSettings _appSettings;
    private readonly IAuditService _auditService;

    public KanbanHub(
        SnapshotAggregator snapshotAggregator,
        EventBroadcaster eventBroadcaster,
        HistoryQueryHandler historyQueryHandler,
        CollectorDiagnosticsProvider diagnosticsProvider,
        ConfigSyncHandler configSyncHandler,
        ShiftProgressProvider shiftProgressProvider,
        MetaPublisher metaPublisher,
        WorkOrderRepository workOrderRepository,
        HistoryService historyService,
        AppSettings appSettings,
        IAuditService auditService)
    {
        _snapshotAggregator = snapshotAggregator;
        _eventBroadcaster = eventBroadcaster;
        _historyQueryHandler = historyQueryHandler;
        _diagnosticsProvider = diagnosticsProvider;
        _configSyncHandler = configSyncHandler;
        _shiftProgressProvider = shiftProgressProvider;
        _metaPublisher = metaPublisher;
        _workOrderRepository = workOrderRepository;
        _historyService = historyService;
        _appSettings = appSettings;
        _auditService = auditService;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync()
        => _snapshotAggregator.GetCurrentSnapshotsAsync(Context.ConnectionAborted);

    /// <inheritdoc />
    public async Task SubscribeSnapshotsAsync()
    {
        var channel = await _snapshotAggregator.SubscribeAsync(Context.ConnectionAborted);

        try
        {
            await foreach (var snapshot in channel.ReadAllAsync(Context.ConnectionAborted))
            {
                await Clients.Caller.OnSnapshot(snapshot);
            }
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            // 客户端正常断开（浏览器刷新/关页面），静默退出，避免 ERROR 日志
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

    /// <inheritdoc />
    public Task<BatchHistoryQueryResponse> QueryHistoryBatchAsync(BatchHistoryQueryRequest request)
        => _historyQueryHandler.QueryBatchAsync(request, Context.ConnectionAborted);

    /// <summary>运行监控页 Remote 模式：拉取 Collector 采集/历史/连接诊断快照。</summary>
    public Task<CollectorDiagnosticsDto> GetDiagnosticsAsync()
        => Task.FromResult(_diagnosticsProvider.GetSnapshot());

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync()
        => _configSyncHandler.GetDevicesAsync();

    /// <inheritdoc />
    public Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId)
        => _configSyncHandler.GetCurrentWorkOrderAsync(deviceId);

    /// <inheritdoc />
    public Task<WorkOrderProductionSummaryDto> GetWorkOrderProductionSummaryAsync(int workOrderId)
    {
        var workOrder = _workOrderRepository.GetSnapshot().FirstOrDefault(w => w.Id == workOrderId);
        if (workOrder is null)
            return Task.FromResult(WorkOrderProductionSummaryCalculator.Empty);

        try
        {
            return Task.FromResult(WorkOrderProductionSummaryCalculator.Calculate(workOrder, _historyService));
        }
        catch (Exception ex)
        {
            // 与 MainAPP WorkOrderService 一致：查询失败返回空摘要，由客户端按 0 展示
            Serilog.Log.Warning(ex, "Hub 查询工单产量聚合失败 WorkOrderId={WorkOrderId}", workOrderId);
            return Task.FromResult(WorkOrderProductionSummaryCalculator.Empty);
        }
    }

    /// <inheritdoc />
    public Task<ShiftProgressDto> GetShiftProgressAsync()
        => Task.FromResult(_shiftProgressProvider.GetProgress());

    /// <inheritdoc />
    public async Task SubscribeMetaAsync()
    {
        var channel = await _metaPublisher.SubscribeAsync(Context.ConnectionAborted);

        try
        {
            await foreach (var meta in channel.ReadAllAsync(Context.ConnectionAborted))
            {
                await Clients.Caller.OnMeta(meta);
            }
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            // 客户端正常断开，静默退出
        }
    }

    /// <inheritdoc />
    public Task<string> GetServerVersionAsync()
        => Task.FromResult(_configSyncHandler.GetServerVersion());

    /// <inheritdoc />
    public Task<string> GetTitleAsync()
        => Task.FromResult(_configSyncHandler.GetTitle());

    /// <inheritdoc />
    public Task<int> GetLanguageAsync()
        => Task.FromResult(_configSyncHandler.GetLanguage());

    /// <inheritdoc />
    public Task<string> GetLanguageCodeAsync()
        => Task.FromResult(_configSyncHandler.GetLanguageCode());

    /// <inheritdoc />
    public Task<IReadOnlyList<LocalizationOverrideDto>> GetLocalizationOverridesAsync()
        => Task.FromResult<IReadOnlyList<LocalizationOverrideDto>>(
            Kanban.Collector.Core.Localization.LocalizationOverrideStore.Snapshot()
                .Select(entry => new LocalizationOverrideDto
                {
                    Resource = entry.Resource,
                    Key = entry.Key,
                    CultureName = entry.CultureName,
                    Value = entry.Value,
                })
                .ToList());

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync()
        => Task.FromResult<IReadOnlyList<WorkOrderDto>>(
            _workOrderRepository.GetSnapshot().Select(WorkOrderMapper.ToDto).ToList());

    /// <inheritdoc />
    public Task<CollectorSettingsDto> GetCollectorSettingsAsync()
        => Task.FromResult(CollectorSettingsMapper.ToDto(_appSettings));

    /// <inheritdoc />
    public Task<AuditLogQueryResponse> QueryAuditLogsAsync(AuditLogQueryRequest request)
    {
        var (items, total) = _auditService.QueryPaged(
            request.From ?? DateTime.MinValue,
            request.To ?? DateTime.MaxValue,
            request.Operator,
            request.Action,
            request.TargetType,
            request.Succeeded,
            request.Page,
            request.PageSize);
        return Task.FromResult(new AuditLogQueryResponse
        {
            Items = items.Select(e => new AuditLogEntryDto
            {
                Id = e.Id,
                Timestamp = e.Timestamp,
                Operator = e.Operator,
                Action = e.Action,
                TargetType = e.TargetType,
                TargetId = e.TargetId,
                Succeeded = e.Succeeded,
                Detail = e.Detail,
                BeforeJson = e.BeforeJson,
                AfterJson = e.AfterJson,
            }).ToList(),
            Total = total,
            Page = request.Page,
            PageSize = request.PageSize,
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RecipeDto>> GetRecipesAsync()
        => _configSyncHandler.GetRecipesAsync();
}
