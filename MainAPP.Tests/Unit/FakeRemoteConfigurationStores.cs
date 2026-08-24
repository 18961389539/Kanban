using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;

namespace MainAPP.Tests.Unit;

internal sealed class FakeRemoteDeviceConfigurationStore : IRemoteDeviceConfigurationStore
{
    public bool IsEnabled { get; set; } = true;

    public Func<IReadOnlyList<Device>, Task>? SaveHandler { get; set; }
    public Func<Task<IReadOnlyList<Device>?>>? RollbackHandler { get; set; }
    public Func<Task<bool>>? BackupHandler { get; set; }

    public Task SaveDevicesAsync(
        IReadOnlyList<Device> devices,
        CancellationToken cancellationToken = default)
        => SaveHandler?.Invoke(devices) ?? Task.CompletedTask;

    public Task<IReadOnlyList<Device>?> RollbackDevicesAsync(
        CancellationToken cancellationToken = default)
        => RollbackHandler?.Invoke() ?? Task.FromResult<IReadOnlyList<Device>?>(null);

    public Task<bool> HasDeviceBackupAsync(CancellationToken cancellationToken = default)
        => BackupHandler?.Invoke() ?? Task.FromResult(false);
}

internal sealed class FakeRemoteRecipeStore : IRemoteRecipeStore
{
    public bool IsEnabled { get; set; } = true;

    public Func<IReadOnlyList<Recipe>, Task>? SaveHandler { get; set; }

    public Task SaveRecipesAsync(
        IReadOnlyList<Recipe> recipes,
        CancellationToken cancellationToken = default)
        => SaveHandler?.Invoke(recipes) ?? Task.CompletedTask;
}

internal sealed class FakeRemoteWorkOrderStore : IRemoteWorkOrderStore
{
    public bool IsEnabled { get; set; } = true;

    public Func<WorkOrder, Task<WorkOrder>>? UpsertHandler { get; set; }
    public Func<int, Task<bool>>? DeleteHandler { get; set; }

    public Task<WorkOrder> UpsertAsync(
        WorkOrder workOrder,
        CancellationToken cancellationToken = default)
        => UpsertHandler?.Invoke(workOrder) ?? Task.FromResult(workOrder);

    public Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
        => DeleteHandler?.Invoke(id) ?? Task.FromResult(false);
}