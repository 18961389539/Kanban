using Kanban.Client;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Mapping;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;

namespace MainAPP.Services;

public sealed class RemoteDeviceConfigurationStore(
    IKanbanAdminClient client,
    IRuntimeMode runtimeMode) : IRemoteDeviceConfigurationStore
{
    public bool IsEnabled => runtimeMode.IsRemote;

    public async Task SaveDevicesAsync(
        IReadOnlyList<Device> devices,
        CancellationToken cancellationToken = default)
        => await client.SaveDevicesAsync(DeviceMapper.ToDtos(devices), cancellationToken);

    public async Task<IReadOnlyList<Device>?> RollbackDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await client.RollbackDevicesAsync(cancellationToken);
        return devices is null ? null : DeviceMapper.ToEntities(devices).ToList();
    }

    public Task<bool> HasDeviceBackupAsync(CancellationToken cancellationToken = default)
        => client.HasDeviceBackupAsync(cancellationToken);
}

public sealed class RemoteRecipeStore(
    IKanbanAdminClient client,
    IRuntimeMode runtimeMode) : IRemoteRecipeStore
{
    public bool IsEnabled => runtimeMode.IsRemote;

    public Task SaveRecipesAsync(
        IReadOnlyList<Recipe> recipes,
        CancellationToken cancellationToken = default)
        => client.SaveRecipesAsync(RecipeMapper.ToDtos(recipes).ToList(), cancellationToken);
}

public sealed class RemoteWorkOrderStore(
    IKanbanAdminClient client,
    IRuntimeMode runtimeMode) : IRemoteWorkOrderStore
{
    public bool IsEnabled => runtimeMode.IsRemote;

    public async Task<WorkOrder> UpsertAsync(
        WorkOrder workOrder,
        CancellationToken cancellationToken = default)
    {
        var saved = await client.UpsertWorkOrderAsync(WorkOrderMapper.ToDto(workOrder), cancellationToken);
        return WorkOrderMapper.ToEntity(saved);
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await client.DeleteWorkOrderAsync(id, cancellationToken);
        return true;
    }
}