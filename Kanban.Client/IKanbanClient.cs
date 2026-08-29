using Kanban.Contracts.Dtos;

namespace Kanban.Client;

/// <summary>
/// 客户端监控域接口（展示/查询/订阅）：由 <see cref="KanbanDataClient"/> 实现。
/// 与契约 <c>IKanbanHubServer</c>（监控域）一一对应，按能力域拆分管理接口
/// <see cref="IKanbanAdminClient"/>——调用方按需依赖窄接口，新功能按域落位。
/// </summary>
public interface IKanbanMonitoringClient
{
    Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync(CancellationToken ct = default);
    Task SubscribeSnapshotsAsync(CancellationToken ct = default);
    Task SubscribeAlarmEventsAsync(long afterSeq, CancellationToken ct = default);
    Task SubscribeStatusEventsAsync(long afterSeq, CancellationToken ct = default);
    Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync(CancellationToken ct = default);
    Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId, CancellationToken ct = default);
    Task<WorkOrderProductionSummaryDto> GetWorkOrderProductionSummaryAsync(int workOrderId, CancellationToken ct = default);
    Task<ShiftProgressDto> GetShiftProgressAsync(CancellationToken ct = default);
    Task SubscribeMetaAsync(CancellationToken ct = default);
    Task<string> GetServerVersionAsync(CancellationToken ct = default);
    Task<int> GetLanguageAsync(CancellationToken ct = default);
    Task<string> GetLanguageCodeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LocalizationOverrideDto>> GetLocalizationOverridesAsync(CancellationToken ct = default);
    void OnLocalizationChanged(Action<LocalizationChangedDto> handler);

    /// <summary>工单列表（只读管理页数据源）。</summary>
    Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync(CancellationToken ct = default);

    /// <summary>采集设置快照（只读设置页数据源）。</summary>
    Task<CollectorSettingsDto> GetCollectorSettingsAsync(CancellationToken ct = default);

    /// <summary>审计日志分页查询（只读审计页数据源）。</summary>
    Task<AuditLogQueryResponse> QueryAuditLogsAsync(AuditLogQueryRequest request, CancellationToken ct = default);

    /// <summary>配方列表（只读数据源）。</summary>
    Task<IReadOnlyList<RecipeDto>> GetRecipesAsync(CancellationToken ct = default);
}

/// <summary>
/// 客户端管理域接口（Collector 单写者的管理写入口）：由 <see cref="KanbanAdminClient"/> 实现。
/// 与契约 <c>IKanbanAdminServer</c> 对应。
/// </summary>
public interface IKanbanAdminClient
{
    IDisposable OnRecipeApplyProgress(Action<RecipeApplyProgressDto> handler);
    Task RecordAuditAsync(AuditLogRecordRequest request, CancellationToken ct = default);
    Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices, CancellationToken ct = default);
    Task<bool> HasDeviceBackupAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeviceConfigDto>?> RollbackDevicesAsync(CancellationToken ct = default);
    Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder, CancellationToken ct = default);
    Task DeleteWorkOrderAsync(int workOrderId, CancellationToken ct = default);
    Task SaveCollectorSettingsAsync(CollectorSettingsDto settings, CancellationToken ct = default);
    Task SaveRecipesAsync(List<RecipeDto> recipes, CancellationToken ct = default);
    Task<RecipeApplyResultDto> ApplyRecipeAsync(string deviceId, string recipeId, CancellationToken ct = default);
}
