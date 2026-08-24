using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Data;

public interface IRemoteDeviceConfigurationStore
{
    bool IsEnabled { get; }

    Task SaveDevicesAsync(IReadOnlyList<Device> devices, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Device>?> RollbackDevicesAsync(CancellationToken cancellationToken = default);

    Task<bool> HasDeviceBackupAsync(CancellationToken cancellationToken = default);
}

public interface IRemoteRecipeStore
{
    bool IsEnabled { get; }

    Task SaveRecipesAsync(IReadOnlyList<Recipe> recipes, CancellationToken cancellationToken = default);
}

public interface IRemoteWorkOrderStore
{
    bool IsEnabled { get; }

    Task<WorkOrder> UpsertAsync(WorkOrder workOrder, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}