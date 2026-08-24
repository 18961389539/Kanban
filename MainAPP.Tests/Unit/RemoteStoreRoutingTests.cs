using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class RemoteStoreRoutingTests
{
    [Fact]
    public async Task DeviceRepository_RemoteSave_UsesExplicitStore()
    {
        IReadOnlyList<Device>? received = null;
        var remote = new FakeRemoteDeviceConfigurationStore
        {
            SaveHandler = devices =>
            {
                received = devices;
                return Task.CompletedTask;
            },
        };
        var repository = new DeviceRepository(new AppSettings(), remote);
        repository.Devices.Add(new Device { Name = "远程设备" });

        await repository.SaveAllAsync();

        Assert.NotNull(received);
        Assert.Equal("远程设备", Assert.Single(received!).Name);
        Assert.Throws<InvalidOperationException>(() => repository.SaveAll());
    }

    [Fact]
    public async Task RecipeStore_RemoteSave_UsesExplicitStore()
    {
        IReadOnlyList<Recipe>? received = null;
        var remote = new FakeRemoteRecipeStore
        {
            SaveHandler = recipes =>
            {
                received = recipes;
                return Task.CompletedTask;
            },
        };
        var store = new RecipeStore(new AppSettings(), remote);
        store.Recipes.Add(new Recipe { Name = "远程配方" });

        await store.SaveAllAsync();

        Assert.NotNull(received);
        Assert.Equal("远程配方", Assert.Single(received!).Name);
        Assert.Throws<InvalidOperationException>(() => store.SaveAll());
    }

    [Fact]
    public async Task WorkOrderRepository_RemoteWrites_UseExplicitStore()
    {
        var saved = new WorkOrder { Id = 42, OrderNo = "远程工单" };
        WorkOrder? received = null;
        var remote = new FakeRemoteWorkOrderStore
        {
            UpsertHandler = workOrder =>
            {
                received = workOrder;
                return Task.FromResult(saved);
            },
            DeleteHandler = id => Task.FromResult(id == saved.Id),
        };
        var repository = new WorkOrderRepository(
            new DatabaseProvider(new AppSettings()),
            MainAPP.Tests.TestMapper.Instance,
            remote);

        var result = await repository.UpsertAsync(new WorkOrder { OrderNo = "待保存" });
        await repository.DeleteAsync(saved.Id);

        Assert.NotNull(received);
        Assert.Equal("待保存", received!.OrderNo);
        Assert.Equal(saved.Id, result.Id);
        Assert.Empty(repository.WorkOrders);
        Assert.Throws<InvalidOperationException>(() => repository.Upsert(new WorkOrder()));
        Assert.Throws<InvalidOperationException>(() => repository.Delete(saved.Id));
    }
}