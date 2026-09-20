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
    private readonly ISnEventStore _snEventStore;
    private readonly IActiveAlarmStateService _activeAlarmStateService;
    private readonly ILogger? _logger;

    /// <summary>SN 追溯单页上限（防客户端传超大 PageSize 一次拉全表；工单明细页 20/页远低于此）。</summary>
    private const int MaxSnPageSize = 200;

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
        IAuditService auditService,
        ISnEventStore snEventStore,
        IActiveAlarmStateService activeAlarmStateService,
        ILogger<KanbanHub>? logger = null)
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
        _snEventStore = snEventStore;
        _activeAlarmStateService = activeAlarmStateService;
        _logger = logger;
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
    public Task<ProductionWindowAnalysisDto> QueryProductionWindowAnalysisAsync(HistoryQueryRequest request)
        => _historyQueryHandler.AnalyzeProductionWindowAsync(request, Context.ConnectionAborted);

    /// <inheritdoc />
    public Task<AlarmWindowStatsDto> QueryAlarmWindowStatsAsync(HistoryQueryRequest request)
        => _historyQueryHandler.AnalyzeAlarmWindowAsync(request, Context.ConnectionAborted);

    /// <inheritdoc />
    public Task<StatusWindowAnalysisDto> QueryStatusWindowAnalysisAsync(HistoryQueryRequest request)
        => _historyQueryHandler.AnalyzeStatusWindowAsync(request, Context.ConnectionAborted);

    /// <inheritdoc />
    public Task<ReviewWindowAnalysisDto> QueryReviewAnalysisAsync(HistoryQueryRequest request)
        => _historyQueryHandler.AnalyzeReviewWindowAsync(request, Context.ConnectionAborted);

    /// <inheritdoc />
    public Task<BatchHistoryQueryResponse> QueryHistoryBatchAsync(BatchHistoryQueryRequest request)
        => _historyQueryHandler.QueryBatchAsync(request, Context.ConnectionAborted);

    /// <inheritdoc />
    public Task<SnEventQueryResponse> QuerySnEventsAsync(SnEventQueryRequest request)
    {
        try
        {
            // 归一化分页参数：负数/零 Page 与超大 PageSize 由客户端异常或恶意构造产生，
            // Skip((page-1)*pageSize) 负偏移会抛异常、超大页会一次拉全表（审查修复 2026-08-30）。
            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 1, MaxSnPageSize);

            if (!string.IsNullOrWhiteSpace(request.Sn))
            {
                // SN 精确查询：一个 SN 通常一条记录，全量返回（不翻页）
                var items = _snEventStore.QueryBySn(request.Sn.Trim())
                    .Select(ToDto)
                    .ToList();
                return Task.FromResult(new SnEventQueryResponse
                {
                    Total = items.Count,
                    Page = page,
                    PageSize = pageSize,
                    Items = items,
                });
            }

            if (request.WorkOrderId.HasValue)
            {
                var (total, events) = _snEventStore.QueryByWorkOrder(
                    request.WorkOrderId.Value, page, pageSize);
                return Task.FromResult(new SnEventQueryResponse
                {
                    Total = total,
                    Page = page,
                    PageSize = pageSize,
                    Items = events.Select(ToDto).ToList(),
                });
            }

            var (rangeTotal, rangeEvents) = _snEventStore.QueryByTimeRange(
                request.DeviceId,
                request.From ?? DateTime.MinValue,
                request.To ?? DateTime.MaxValue,
                page,
                pageSize);
            return Task.FromResult(new SnEventQueryResponse
            {
                Total = rangeTotal,
                Page = page,
                PageSize = pageSize,
                Items = rangeEvents.Select(ToDto).ToList(),
            });
        }
        catch (Exception ex)
        {
            // 查询失败不中断连接：记录日志并返回空结果（客户端按空态提示，与历史查询 Error 语义一致）。
            _logger?.LogError(ex, "SN 追溯查询失败 Sn={Sn} WorkOrderId={WorkOrderId}", request.Sn, request.WorkOrderId);
            return Task.FromResult(new SnEventQueryResponse { Page = request.Page, PageSize = request.PageSize });
        }
    }

    private static SnEventRecordDto ToDto(SnEventRecord record) => new()
    {
        Id = record.Id,
        Sn = record.Sn,
        DeviceId = record.DeviceId,
        DeviceName = record.DeviceName,
        WorkOrderId = record.WorkOrderId,
        ShiftName = record.ShiftName,
        Result = record.Result,
        Source = record.Source,
        SourceId = record.SourceId,
        BatchNo = record.BatchNo,
        Timestamp = record.Timestamp,
    };

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
    public Task<bool> GetDisplayCarouselEnabledAsync()
        => Task.FromResult(_configSyncHandler.GetDisplayCarouselEnabled());

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

    /// <inheritdoc />
    public Task<IReadOnlyList<ActiveAlarmStateDto>> QueryActiveAlarmStatesAsync(string? deviceId = null)
    {
        var rows = _activeAlarmStateService.QueryActive(deviceId);
        var dtos = rows.Select(r => new ActiveAlarmStateDto
        {
            DeviceId = r.DeviceId,
            DeviceName = r.DeviceName,
            AlarmId = r.AlarmId,
            AlarmName = r.AlarmName,
            PlcAddress = r.PlcAddress,
            IsActive = r.IsActive,
            TriggeredAt = r.TriggeredAt,
            ShiftName = r.ShiftName,
            UpdatedAt = r.UpdatedAt,
        }).ToList();
        return Task.FromResult<IReadOnlyList<ActiveAlarmStateDto>>(dtos);
    }
}
