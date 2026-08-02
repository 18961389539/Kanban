using System.IO;
using MainAPP.Data;
using MainAPP.Entities;
using MainAPP.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class DefectHistoryStoreTests
{
    [Fact]
    public void CleanupOldSnapshots_RemovesOnlyExpiredRecords()
    {
        var configDirectory = "KanbanDefectHistoryTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using (var context = databaseProvider.CreateDefectHistoryContext())
                context.Database.EnsureCreated();

            var store = new DefectHistoryStore(databaseProvider);
            store.Append(
            [
                CreateSnapshot(DateTime.Now.AddDays(-366), 10),
                CreateSnapshot(DateTime.Now.AddDays(-1), 20),
            ]);

            var removed = store.CleanupOldSnapshots(365);
            var remaining = store.Query(DateTime.Now.AddDays(-400), DateTime.Now, "device-1");

            Assert.Equal(1, removed);
            var snapshot = Assert.Single(remaining);
            Assert.Equal(20, snapshot.Count);
        }
        finally
        {
            var root = Path.Combine(AppSettings.DataRoot, configDirectory);
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static DefectSnapshotRecord CreateSnapshot(DateTime timestamp, int count)
        => new()
        {
            DeviceId = "device-1",
            DeviceName = "Device 1",
            DefectId = "defect-1",
            DefectName = "Defect 1",
            ShiftName = "day",
            Count = count,
            Timestamp = timestamp,
        };
}
