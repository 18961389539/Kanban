using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
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

    [Fact]
    public void QueryWindowBounds_ReturnsOnlyGroupBoundaries()
    {
        // 验证 SQL 分组下推：每组只返回「窗口前最后一条 + 窗口内首末」，不做全量物化
        var configDirectory = "KanbanDefectHistoryTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using (var context = databaseProvider.CreateDefectHistoryContext())
                context.Database.EnsureCreated();

            var store = new DefectHistoryStore(databaseProvider);
            var from = new DateTime(2026, 8, 8, 8, 0, 0);
            var to = from.AddHours(4);
            // defect-1（day 班次）：基线 10 → 窗口内 15 → 18（增量 8）
            store.Append(
            [
                CreateSnapshot(from.AddHours(-2), 10, "defect-1", "day"),
                CreateSnapshot(from.AddHours(1), 15, "defect-1", "day"),
                CreateSnapshot(from.AddHours(3), 18, "defect-1", "day"),
                // defect-1 另一班次：窗口内仅 1 条（无基线，Count 即增量）
                CreateSnapshot(from.AddHours(2), 4, "defect-1", "night"),
                // defect-2：基线 1 → 窗口内 5（增量 4）
                CreateSnapshot(from.AddHours(-1), 1, "defect-2", "day"),
                CreateSnapshot(from.AddHours(2), 5, "defect-2", "day"),
            ]);

            var bounds = store.QueryWindowBounds(from, to, "device-1");

            // 6 条输入 → 分组边界：基线 2 + 窗口首 3 + 窗口末 3 = 8 条（单条窗口首末为同一记录，允许重复返回）
            Assert.Equal(8, bounds.Count);
            // 边界语义：按组差分后增量正确（defect-1/day=8、defect-1/night=4、defect-2/day=4）
            var groups = bounds.GroupBy(s => new { s.DefectId, s.ShiftName });
            var day1 = groups.Single(g => g.Key.DefectId == "defect-1" && g.Key.ShiftName == "day")
                .Select(s => s.Count).OrderBy(c => c).ToList();
            Assert.Equal(new[] { 10, 15, 18 }, day1);
            Assert.Contains(groups, g => g.Key.DefectId == "defect-1" && g.Key.ShiftName == "night"
                && g.All(s => s.Count == 4));
            Assert.Contains(groups, g => g.Key.DefectId == "defect-2" && g.Key.ShiftName == "day"
                && g.Select(s => s.Count).Contains(1) && g.Select(s => s.Count).Contains(5));
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
        => CreateSnapshot(timestamp, count, "defect-1", "day");

    private static DefectSnapshotRecord CreateSnapshot(DateTime timestamp, int count, string defectId, string shiftName)
        => new()
        {
            DeviceId = "device-1",
            DeviceName = "Device 1",
            DefectId = defectId,
            DefectName = "Defect " + defectId,
            ShiftName = shiftName,
            Count = count,
            Timestamp = timestamp,
        };
}
