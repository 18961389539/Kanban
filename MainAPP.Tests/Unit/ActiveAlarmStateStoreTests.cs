using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ActiveAlarmStateStoreTests
{
    [Fact]
    public void RemoveByAlarm_WhenRowAlreadyGone_ReturnsTrueWithoutThrowing()
    {
        using var fixture = new StoreFixture();
        var store = fixture.Store;

        Assert.True(store.RemoveByAlarm("device-008", "feea797fccc44068b79e44169b9649a4"));

        Assert.True(store.UpsertActive(
            "device-008", "焊接机B3", "feea797fccc44068b79e44169b9649a4",
            "电流异常", "M174", isActive: true, triggeredAt: DateTime.Now, shiftName: "白班"));
        Assert.True(store.RemoveByAlarm("device-008", "feea797fccc44068b79e44169b9649a4"));
        Assert.True(store.RemoveByAlarm("device-008", "feea797fccc44068b79e44169b9649a4"));
        Assert.Empty(store.QueryActive("device-008"));
    }

    [Fact]
    public void RemoveByDeviceId_AfterPerAlarmDelete_IsIdempotent()
    {
        using var fixture = new StoreFixture();
        var store = fixture.Store;

        store.UpsertActive("device-008", "焊接机B3", "alm-1", "电流异常", "M174",
            true, DateTime.Now, "白班");
        store.RemoveByAlarm("device-008", "alm-1");

        Assert.True(store.RemoveByDeviceId("device-008"));
        Assert.True(store.RemoveByDeviceId("device-008"));
        Assert.Empty(store.QueryActive("device-008"));
    }

    [Fact]
    public void RemoveByAlarm_ConcurrentDeletes_DoNotFail()
    {
        using var fixture = new StoreFixture();
        var store = fixture.Store;
        store.UpsertActive("device-008", "焊接机B3", "alm-1", "电流异常", "M174",
            true, DateTime.Now, "白班");

        var results = new bool[8];
        Parallel.For(0, results.Length, i => results[i] = store.RemoveByAlarm("device-008", "alm-1"));

        Assert.All(results, Assert.True);
        Assert.Empty(store.QueryActive("device-008"));
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _configDirectory;
        private readonly string _root;

        public ActiveAlarmStateStore Store { get; }

        public StoreFixture()
        {
            _configDirectory = "KanbanActiveAlarmTests_" + Guid.NewGuid().ToString("N");
            var settings = new AppSettings { ConfigDirectory = _configDirectory };
            var db = new DatabaseProvider(settings);
            using (var ctx = db.CreateAlarmEventContext())
                ctx.Database.EnsureCreated();
            Store = new ActiveAlarmStateStore(db, NullLogger<ActiveAlarmStateStore>.Instance);
            _root = Path.Combine(AppSettings.DataRoot, _configDirectory);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
