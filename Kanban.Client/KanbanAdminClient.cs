using Kanban.Contracts;
using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Microsoft.Extensions.Logging;

namespace Kanban.Client;

/// <summary>
/// Collector 管理域客户端。使用独立 SignalR 连接和管理 Hub 路径，
/// 与只读监控连接分离，避免展示端连接意外获得配置/PLC 写入能力。
/// </summary>
public sealed class KanbanAdminClient : IKanbanAdminClient, IAsyncDisposable
{
    private readonly KanbanDataClient _client;

    public KanbanAdminClient(string monitoringHubUrl, ILogger<KanbanDataClient> logger, bool useMessagePack = true)
    {
        _client = new KanbanDataClient(BuildAdminHubUrl(monitoringHubUrl), logger, useMessagePack);
    }

    public string HubUrl => _client.HubUrl;

    public bool IsConnected => _client.IsConnected;

    public event EventHandler? Reconnected
    {
        add => _client.Reconnected += value;
        remove => _client.Reconnected -= value;
    }

    public event EventHandler<bool>? ConnectionStateChanged
    {
        add => _client.ConnectionStateChanged += value;
        remove => _client.ConnectionStateChanged -= value;
    }

    public string OperatorName
    {
        get => _client.OperatorName;
        set => _client.OperatorName = value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _client.ConnectAsync(cancellationToken);

    public void EnsureConnectionEstablished()
        => _client.EnsureConnectionEstablished();

    public IDisposable OnRecipeApplyProgress(Action<RecipeApplyProgressDto> handler)
        => _client.OnRecipeApplyProgress(handler);

    public Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices, CancellationToken ct = default)
        => _client.SaveDevicesAsync(devices, ct);

    public Task<bool> HasDeviceBackupAsync(CancellationToken ct = default)
        => _client.HasDeviceBackupAsync(ct);

    public Task<IReadOnlyList<DeviceConfigDto>?> RollbackDevicesAsync(CancellationToken ct = default)
        => _client.RollbackDevicesAsync(ct);

    public Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder, CancellationToken ct = default)
        => _client.UpsertWorkOrderAsync(workOrder, ct);

    public Task DeleteWorkOrderAsync(int workOrderId, CancellationToken ct = default)
        => _client.DeleteWorkOrderAsync(workOrderId, ct);

    public Task SaveCollectorSettingsAsync(CollectorSettingsDto settings, CancellationToken ct = default)
        => _client.SaveCollectorSettingsAsync(settings, ct);

    public Task SaveRecipesAsync(List<RecipeDto> recipes, CancellationToken ct = default)
        => _client.SaveRecipesAsync(recipes, ct);

    public Task<IReadOnlyList<RecipeDto>> GetRecipesAsync(CancellationToken ct = default)
        => _client.GetRecipesAsync(ct);

    public Task<RecipeApplyResultDto> ApplyRecipeAsync(string deviceId, string recipeId, CancellationToken ct = default)
        => _client.ApplyRecipeAsync(deviceId, recipeId, ct);

    public Task RecordAuditAsync(AuditLogRecordRequest request, CancellationToken ct = default)
        => _client.RecordAuditAsync(request, ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private static string BuildAdminHubUrl(string monitoringHubUrl)
    {
        var builder = new UriBuilder(monitoringHubUrl);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith(KanbanHubPaths.AdminHubPath, StringComparison.OrdinalIgnoreCase))
            return builder.Uri.ToString();

        var monitoringPath = KanbanHubPaths.HubPath.TrimEnd('/');
        if (path.EndsWith(monitoringPath, StringComparison.OrdinalIgnoreCase))
            path = path[..^monitoringPath.Length];
        builder.Path = path.TrimEnd('/') + KanbanHubPaths.AdminHubPath;
        return builder.Uri.ToString();
    }
}
