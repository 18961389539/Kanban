using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Services;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Mapping;
using Kanban.Core.Services;
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
    private readonly WorkOrderRepository _workOrderRepository;
    private readonly AppSettings _appSettings;
    private readonly IAuditService _auditService;
    private readonly ILogger<KanbanHub> _logger;

    public KanbanHub(
        SnapshotAggregator snapshotAggregator,
        EventBroadcaster eventBroadcaster,
        HistoryQueryHandler historyQueryHandler,
        CollectorDiagnosticsProvider diagnosticsProvider,
        ConfigSyncHandler configSyncHandler,
        ShiftProgressProvider shiftProgressProvider,
        MetaPublisher metaPublisher,
        WorkOrderRepository workOrderRepository,
        AppSettings appSettings,
        IAuditService auditService,
        ILogger<KanbanHub> logger)
    {
        _snapshotAggregator = snapshotAggregator;
        _eventBroadcaster = eventBroadcaster;
        _historyQueryHandler = historyQueryHandler;
        _diagnosticsProvider = diagnosticsProvider;
        _configSyncHandler = configSyncHandler;
        _shiftProgressProvider = shiftProgressProvider;
        _metaPublisher = metaPublisher;
        _workOrderRepository = workOrderRepository;
        _appSettings = appSettings;
        _auditService = auditService;
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
    public Task<BatchHistoryQueryResponse> QueryHistoryBatchAsync(BatchHistoryQueryRequest request)
        => _historyQueryHandler.QueryBatchAsync(request, Context.ConnectionAborted);

    /// <summary>运行监控页 Remote 模式：拉取 Collector 采集/历史/连接诊断快照。</summary>
    public Task<CollectorDiagnosticsDto> GetDiagnosticsAsync()
        => Task.FromResult(_diagnosticsProvider.GetSnapshot());

    /// <inheritdoc />
    public async Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices)
    {
        try
        {
            await _configSyncHandler.SaveDevicesAsync(devices);
            AuditLog.Record("Device.SaveBatch", "Device", null,
                detail: $"保存 {devices.Count} 台设备配置");
        }
        catch (Exception ex)
        {
            AuditLog.Record("Device.SaveBatch", "Device", null, succeeded: false, detail: ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync()
        => _configSyncHandler.GetDevicesAsync();

    /// <inheritdoc />
    public async Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder)
    {
        try
        {
            var saved = await _configSyncHandler.UpsertWorkOrderAsync(workOrder);
            AuditLog.Record("WorkOrder.Upsert", "WorkOrder", saved.Id > 0 ? saved.Id.ToString() : null,
                after: new { saved.OrderNo, saved.DeviceId, saved.TargetQuantity, saved.Status },
                detail: workOrder.Id > 0 ? "更新工单" : "新增工单");
            return saved;
        }
        catch (Exception ex)
        {
            AuditLog.Record("WorkOrder.Upsert", "WorkOrder",
                workOrder.Id > 0 ? workOrder.Id.ToString() : null, succeeded: false, detail: ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteWorkOrderAsync(int workOrderId)
    {
        try
        {
            await _configSyncHandler.DeleteWorkOrderAsync(workOrderId);
            AuditLog.Record("WorkOrder.Delete", "WorkOrder", workOrderId.ToString(), detail: "删除工单");
        }
        catch (Exception ex)
        {
            AuditLog.Record("WorkOrder.Delete", "WorkOrder", workOrderId.ToString(), succeeded: false, detail: ex.Message);
            throw;
        }
    }

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
    public async Task SaveCollectorSettingsAsync(CollectorSettingsDto settings)
    {
        try
        {
            await _configSyncHandler.SaveCollectorSettingsAsync(settings);
            AuditLog.Record("CollectorSettings.Update", "Settings", null,
                after: new { settings.PollingIntervalMs, settings.HistoryWriteIntervalScans, settings.PlcBrand },
                detail: "远程保存采集设置");
        }
        catch (Exception ex)
        {
            AuditLog.Record("CollectorSettings.Update", "Settings", null,
                succeeded: false, detail: ex.Message);
            throw;
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
    public Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync()
        => Task.FromResult<IReadOnlyList<WorkOrderDto>>(
            _workOrderRepository.GetSnapshot().Select(WorkOrderMapper.ToDto).ToList());

    /// <inheritdoc />
    public Task<CollectorSettingsDto> GetCollectorSettingsAsync()
        => Task.FromResult(BuildSettingsSnapshot());

    /// <summary>采集设置快照：AppSettings → CollectorSettingsDto（与 SaveCollectorSettingsAsync 的字段一一对应）。</summary>
    private CollectorSettingsDto BuildSettingsSnapshot()
    {
        var plc = _appSettings.PlcConfig;
        return new CollectorSettingsDto
        {
            PollingIntervalMs = _appSettings.PollingIntervalMs,
            HistoryWriteIntervalScans = _appSettings.HistoryWriteIntervalScans,
            PlcBatchReadMaxLength = _appSettings.PlcBatchReadMaxLength,
            PlcBatchReadMaxGapSlots = _appSettings.PlcBatchReadMaxGapSlots,
            PlcBrand = (int)plc.Brand,
            PlcIpAddress = plc.IpAddress,
            PlcPort = plc.Port,
            PlcTimeoutMs = plc.TimeoutMs,
            Siemens = new SiemensSettingsDto
            {
                Model = plc.Siemens.Model,
                Rack = plc.Siemens.Rack,
                Slot = plc.Siemens.Slot,
                DataFormat = (int)plc.Siemens.DataFormat,
                BatchInt32Limit = plc.Siemens.BatchInt32Limit,
            },
            ModbusTcp = new ModbusTcpSettingsDto
            {
                UnitId = plc.ModbusTcp.UnitId,
                AddressStartWithZero = plc.ModbusTcp.AddressStartWithZero,
                RegisterFunction = plc.ModbusTcp.RegisterFunction,
                BitFunction = plc.ModbusTcp.BitFunction,
                DataFormat = (int)plc.ModbusTcp.DataFormat,
                BatchInt32Limit = plc.ModbusTcp.BatchInt32Limit,
            },
            Omron = new OmronFinsSettingsDto
            {
                ReadSplits = plc.Omron.ReadSplits,
            },
            // 锁内快照枚举：与 ConfigSyncHandler 的原地写入互斥（审查修复 2026-08-13）
            Shifts = LockedShifts().Select(s => new ShiftConfigDto
            {
                Name = s.Name,
                StartTime = s.StartTime,
                EndTime = s.EndTime,
            }).ToList(),
        };
    }

    private List<Kanban.Core.Models.ShiftConfig> LockedShifts()
    {
        lock (_appSettings.ShiftsLock)
            return _appSettings.Shifts.ToList();
    }

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
            }).ToList(),
            Total = total,
            Page = request.Page,
            PageSize = request.PageSize,
        });
    }

    /// <inheritdoc />
    public async Task SaveRecipesAsync(List<RecipeDto> recipes)
    {
        try
        {
            await _configSyncHandler.SaveRecipesAsync(recipes);
            AuditLog.Record("Recipe.SaveBatch", "Recipe", null,
                detail: $"保存 {recipes.Count} 条配方");
        }
        catch (Exception ex)
        {
            AuditLog.Record("Recipe.SaveBatch", "Recipe", null, succeeded: false, detail: ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RecipeDto>> GetRecipesAsync()
        => _configSyncHandler.GetRecipesAsync();

    /// <inheritdoc />
    public async Task<RecipeApplyResultDto> ApplyRecipeAsync(string deviceId, string recipeId)
    {
        try
        {
            // 进度经强类型客户端方法逐项推送（fire-and-forget：SignalR 每连接串行发送队列，
            // 不 await 避免拖慢 Apply 循环；最终结果仍由本 Invoke 返回值承载）
            Action<RecipeApplyProgressDto> progress = p =>
            {
                var send = Clients.Caller.OnRecipeApplyProgress(p);
                _ = send.ContinueWith(t =>
                {
                    if (t.IsFaulted) _logger.LogWarning(t.Exception, "配方下发进度推送失败");
                }, TaskContinuationOptions.OnlyOnFaulted);
            };
            var result = await _configSyncHandler.ApplyRecipeAsync(deviceId, recipeId, progress);
            AuditLog.Record("Recipe.Apply", "Recipe", recipeId,
                detail: $"下发配方到设备 {deviceId}",
                after: new { result.Success, result.Message });
            return result;
        }
        catch (Exception ex)
        {
            AuditLog.Record("Recipe.Apply", "Recipe", recipeId, succeeded: false, detail: ex.Message);
            throw;
        }
    }
}
