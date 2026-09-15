using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Hubs;

/// <summary>
/// 管理域 Hub：仅挂载配置、工单、配方、采集设置和远程审计写入。
/// 与只读 KanbanHub 使用不同路径，监控连接不会暴露这些方法。
/// </summary>
public sealed class KanbanAdminHub : Hub<IKanbanHubClient>, IKanbanAdminServer
{
    private readonly ConfigSyncHandler _configSyncHandler;
    private readonly IAuditService _auditService;
    private readonly ILogger<KanbanAdminHub> _logger;
    private readonly IPlcDataAcquisitionService? _dataAcquisitionService;

    public KanbanAdminHub(
        ConfigSyncHandler configSyncHandler,
        IAuditService auditService,
        ILogger<KanbanAdminHub> logger,
        IPlcDataAcquisitionService? dataAcquisitionService = null)
    {
        _configSyncHandler = configSyncHandler;
        _auditService = auditService;
        _logger = logger;
        _dataAcquisitionService = dataAcquisitionService;
    }

    public Task RecordAuditAsync(AuditLogRecordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _auditService.Record(
            request.Action,
            request.TargetType,
            request.TargetId,
            request.Succeeded,
            request.Detail,
            ResolveOperator(),
            request.BeforeJson,
            request.AfterJson);
        return Task.CompletedTask;
    }

    public async Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices)
    {
        try
        {
            await _configSyncHandler.SaveDevicesAsync(devices);
            AuditLog.Record("Device.SaveBatch", "Device", null,
                detail: $"保存 {devices.Count} 台设备配置", @operator: ResolveOperator());
        }
        catch (Exception ex)
        {
            AuditLog.Record("Device.SaveBatch", "Device", null, succeeded: false,
                detail: ex.Message, @operator: ResolveOperator());
            throw;
        }
    }

    public Task<bool> HasDeviceBackupAsync()
        => _configSyncHandler.HasDeviceBackupAsync();

    public async Task<IReadOnlyList<DeviceConfigDto>?> RollbackDevicesAsync()
    {
        try
        {
            var restored = await _configSyncHandler.RollbackDevicesAsync();
            AuditLog.Record("Device.Rollback", "Device", null,
                detail: restored is null ? "没有可用设备备份" : $"恢复 {restored.Count} 台设备配置",
                @operator: ResolveOperator());
            return restored;
        }
        catch (Exception ex)
        {
            AuditLog.Record("Device.Rollback", "Device", null, succeeded: false,
                detail: ex.Message, @operator: ResolveOperator());
            throw;
        }
    }

    public async Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder)
    {
        try
        {
            var saved = await _configSyncHandler.UpsertWorkOrderAsync(workOrder);
            AuditLog.Record("WorkOrder.Upsert", "WorkOrder", saved.Id > 0 ? saved.Id.ToString() : null,
                after: new { saved.OrderNo, saved.DeviceId, saved.TargetQuantity, saved.Status },
                detail: workOrder.Id > 0 ? "更新工单" : "新增工单", @operator: ResolveOperator());
            return saved;
        }
        catch (Exception ex)
        {
            AuditLog.Record("WorkOrder.Upsert", "WorkOrder",
                workOrder.Id > 0 ? workOrder.Id.ToString() : null,
                succeeded: false, detail: ex.Message, @operator: ResolveOperator());
            throw;
        }
    }

    public async Task DeleteWorkOrderAsync(int workOrderId)
    {
        try
        {
            await _configSyncHandler.DeleteWorkOrderAsync(workOrderId);
            AuditLog.Record("WorkOrder.Delete", "WorkOrder", workOrderId.ToString(),
                detail: "删除工单", @operator: ResolveOperator());
        }
        catch (Exception ex)
        {
            AuditLog.Record("WorkOrder.Delete", "WorkOrder", workOrderId.ToString(),
                succeeded: false, detail: ex.Message, @operator: ResolveOperator());
            throw;
        }
    }

    public async Task SaveCollectorSettingsAsync(CollectorSettingsDto settings)
    {
        try
        {
            await _configSyncHandler.SaveCollectorSettingsAsync(settings);
            AuditLog.Record("CollectorSettings.Update", "Settings", null,
                after: new { settings.PollingIntervalMs, settings.HistoryWriteIntervalScans, settings.PlcBrand },
                detail: "远程保存采集设置", @operator: ResolveOperator());
        }
        catch (Exception ex)
        {
            AuditLog.Record("CollectorSettings.Update", "Settings", null,
                succeeded: false, detail: ex.Message, @operator: ResolveOperator());
            throw;
        }
    }

    public async Task SaveRecipesAsync(List<RecipeDto> recipes)
    {
        try
        {
            await _configSyncHandler.SaveRecipesAsync(recipes);
            AuditLog.Record("Recipe.SaveBatch", "Recipe", null,
                detail: $"保存 {recipes.Count} 条配方", @operator: ResolveOperator());
        }
        catch (Exception ex)
        {
            AuditLog.Record("Recipe.SaveBatch", "Recipe", null,
                succeeded: false, detail: ex.Message, @operator: ResolveOperator());
            throw;
        }
    }

    public async Task<RecipeApplyResultDto> ApplyRecipeAsync(string deviceId, string recipeId)
    {
        try
        {
            Action<RecipeApplyProgressDto> progress = item =>
            {
                var send = Clients.Caller.OnRecipeApplyProgress(item);
                _ = send.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        _logger.LogWarning(task.Exception, "配方下发进度推送失败");
                }, TaskContinuationOptions.OnlyOnFaulted);
            };
            var result = await _configSyncHandler.ApplyRecipeAsync(deviceId, recipeId, progress);
            AuditLog.Record("Recipe.Apply", "Recipe", recipeId,
                detail: $"下发配方到设备 {deviceId}",
                after: new { result.Success, result.Message },
                @operator: ResolveOperator());
            return result;
        }
        catch (Exception ex)
        {
            AuditLog.Record("Recipe.Apply", "Recipe", recipeId,
                succeeded: false, detail: ex.Message, @operator: ResolveOperator());
            throw;
        }
    }

    /// <summary>
    /// 一键清零全部设备的 OEE：软件侧（产量/时间/报警累计 + 产量基线）与 PLC 触发位清零都在
    /// Collector 侧完成——只有持有 PLC 连接的进程才能正确清零，故必须经管理 Hub 远程下发。
    /// 危险写操作：二次确认由调用端负责，服务端只做执行与审计留痕。
    /// </summary>
    public Task<OeeResetAllResultDto> ResetAllOeeAsync()
    {
        var service = _dataAcquisitionService
            ?? throw new InvalidOperationException("PLC 采集服务不可用，无法执行全部设备 OEE 清零");

        try
        {
            var (triggered, total) = service.ResetAllDevicesProduction();
            AuditLog.Record("Device.ResetAllOee", "Device", null,
                detail: $"远程一键清零全部设备 OEE（{triggered}/{total} 台 PLC 触发成功）",
                after: new { triggered, total },
                @operator: ResolveOperator());
            return Task.FromResult(new OeeResetAllResultDto(triggered, total));
        }
        catch (Exception ex)
        {
            AuditLog.Record("Device.ResetAllOee", "Device", null, succeeded: false,
                detail: ex.Message, @operator: ResolveOperator());
            throw;
        }
    }

    private string ResolveOperator()
    {
        try
        {
            var identityName = Context.User?.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(identityName)) return identityName;
            var http = Context.GetHttpContext();
            var fromQuery = http?.Request.Query["operator"].ToString();
            return string.IsNullOrWhiteSpace(fromQuery) ? string.Empty : fromQuery;
        }
        catch
        {
            return string.Empty;
        }
    }
}
