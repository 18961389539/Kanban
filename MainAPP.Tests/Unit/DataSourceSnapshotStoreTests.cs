using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// DataSourceSnapshotStore 单元测试：快照按设备聚合查询（单表 + device_id）、
/// 按时间范围过滤、批量追加短事务。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class DataSourceSnapshotStoreTests
{
    private static DataSourceSnapshotRecord CreateSnapshot(
        string deviceId, string sourceId, int value, DateTime timestamp, string shiftName = "白班")
        => new()
        {
            DeviceId = deviceId,
            DeviceName = "设备-" + deviceId,
            SourceId = sourceId,
            SourceName = "温度-" + sourceId,
            SourceType = "温湿度",
            Unit = "℃",
            Value = value,
            ShiftName = shiftName,
            Timestamp = timestamp,
        };

    [Fact]
    public void AppendAndQuery_ByDevice_ReturnsOnlyThatDeviceRows()
    {
        var configDirectory = "KanbanDataSourceSnapTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                context.Database.EnsureCreated();

            var store = new DataSourceSnapshotStore(databaseProvider);
            var t0 = new DateTime(2026, 8, 17, 8, 0, 0);
            store.Append(
            [
                CreateSnapshot("device-1", "src-1", 245, t0),
                CreateSnapshot("device-1", "src-1", 320, t0.AddMinutes(1)), // 越限值
                CreateSnapshot("device-2", "src-2", 550, t0),
            ]);

            var rows = store.Query("device-1", t0.AddMinutes(-1), t0.AddMinutes(2));

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("device-1", r.DeviceId));
            Assert.Equal(245, rows[0].Value);
            Assert.Equal(320, rows[1].Value);
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
    public void Append_EmptyCollection_IsNoOp()
    {
        var configDirectory = "KanbanDataSourceSnapTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                context.Database.EnsureCreated();

            var store = new DataSourceSnapshotStore(databaseProvider);
            store.Append([]);

            var rows = store.Query("device-1", DateTime.MinValue, DateTime.MaxValue);
            Assert.Empty(rows);
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
    public void Query_RespectsTimeRangeBoundaries()
    {
        var configDirectory = "KanbanDataSourceSnapTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                context.Database.EnsureCreated();

            var store = new DataSourceSnapshotStore(databaseProvider);
            var t0 = new DateTime(2026, 8, 17, 8, 0, 0);
            store.Append(
            [
                CreateSnapshot("device-1", "src-1", 100, t0.AddMinutes(-1)),  // 范围前
                CreateSnapshot("device-1", "src-1", 200, t0),                 // 下界含
                CreateSnapshot("device-1", "src-1", 300, t0.AddMinutes(1)),   // 范围内
                CreateSnapshot("device-1", "src-1", 400, t0.AddMinutes(2)),   // 上界含
                CreateSnapshot("device-1", "src-1", 500, t0.AddMinutes(3)),   // 范围后
            ]);

            var rows = store.Query("device-1", t0, t0.AddMinutes(2));

            Assert.Equal(3, rows.Count);
            Assert.Equal([200, 300, 400], rows.Select(r => r.Value).ToArray());
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
}