using System.IO;
using System.Text.Json;
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
        string deviceId,
        string sourceId,
        int value,
        DateTime timestamp,
        string shiftName = "白班",
        string? valueId = null)
        => new()
        {
            DeviceId = deviceId,
            DeviceName = "设备-" + deviceId,
            SourceId = sourceId,
            ValueId = valueId ?? "value-" + sourceId,
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

    [Fact]
    public void Query_ByDeviceSourceAndValue_IsolatesValueIdentity()
    {
        var configDirectory = "KanbanDataSourceSnapTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                context.Database.EnsureCreated();

            var store = new DataSourceSnapshotStore(databaseProvider);
            var timestamp = new DateTime(2026, 8, 17, 8, 0, 0);
            store.Append(
            [
                CreateSnapshot("device-1", "source-a", 10, timestamp, valueId: "value-1"),
                CreateSnapshot("device-1", "source-b", 20, timestamp, valueId: "value-1"),
                CreateSnapshot("device-1", "source-a", 30, timestamp, valueId: "value-2"),
            ]);

            var rows = store.Query("device-1", "source-a", "value-1", timestamp, timestamp);

            var row = Assert.Single(rows);
            Assert.Equal(10, row.Value);
            Assert.Equal("source-a", row.SourceId);
            Assert.Equal("value-1", row.ValueId);
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
    public void Query_ByDeviceSourceAndValue_ReadsLegacyValueIdInSourceId()
    {
        var configDirectory = "KanbanDataSourceSnapTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                context.Database.EnsureCreated();

            var store = new DataSourceSnapshotStore(databaseProvider);
            var timestamp = new DateTime(2026, 8, 17, 8, 0, 0);
            store.Append(
            [
                new DataSourceSnapshotRecord
                {
                    DeviceId = "device-1",
                    SourceId = "value-legacy",
                    ValueId = string.Empty,
                    Value = 77,
                    Timestamp = timestamp,
                },
            ]);

            var rows = store.Query("device-1", "source-current", "value-legacy", timestamp, timestamp);

            Assert.Single(rows);
            Assert.Equal(77, rows[0].Value);
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
    public async Task Flush_UpdatesDiagnosticsAndClearsPendingRows()
    {
        var configDirectory = "KanbanDataSourceSnapTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        try
        {
            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                context.Database.EnsureCreated();

            await using var store = new DataSourceSnapshotStore(databaseProvider);
            var initial = store.GetDiagnosticsSnapshot();
            Assert.Equal(0, initial.PendingCount);
            Assert.Equal(0, initial.TotalFlushedCount);

            var timestamp = new DateTime(2026, 8, 17, 8, 0, 0);
            store.Append(
            [
                CreateSnapshot("device-1", "source-a", 10, timestamp),
                CreateSnapshot("device-1", "source-a", 20, timestamp.AddMinutes(1)),
            ]);

            await store.FlushAsync(TestContext.Current.CancellationToken);

            var diagnostics = store.GetDiagnosticsSnapshot();
            Assert.Equal(0, diagnostics.PendingCount);
            Assert.Equal(2, diagnostics.TotalFlushedCount);
            Assert.Equal(0, diagnostics.OverflowCount);
            Assert.True(diagnostics.LastFlushAt.HasValue);
            Assert.True(diagnostics.FlushP99Milliseconds >= diagnostics.FlushP95Milliseconds);
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
    public async Task Recovery_ReplaysValidRows_MovesBadRows_AndIsIdempotent()
    {
        var configDirectory = "KanbanDataSourceSnapTests_" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { ConfigDirectory = configDirectory };
        var databaseProvider = new DatabaseProvider(settings);
        var recoveryPath = settings.GetFilePath("datasource_snapshots.recovery.jsonl");
        try
        {
            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                context.Database.EnsureCreated();

            Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);
            var timestamp = new DateTime(2026, 8, 17, 8, 0, 0);
            File.WriteAllLines(
                recoveryPath,
                [
                    JsonSerializer.Serialize(CreateSnapshot("device-1", "source-a", 10, timestamp)),
                    "not valid json {{{",
                    JsonSerializer.Serialize(CreateSnapshot("device-2", "source-b", 20, timestamp)),
                ]);

            await using var store = new DataSourceSnapshotStore(databaseProvider);
            await store.FlushAsync(TestContext.Current.CancellationToken);

            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                Assert.Equal(2, context.DataSourceSnapshots.Count());

            Assert.False(File.Exists(recoveryPath));
            Assert.True(File.Exists(recoveryPath + ".bad"));
            Assert.Single(File.ReadAllLines(recoveryPath + ".bad"));
            Assert.Equal(0, store.GetDiagnosticsSnapshot().RecoveryFileLines);

            await store.FlushAsync(TestContext.Current.CancellationToken);
            using (var context = databaseProvider.CreateDataSourceSnapshotContext())
                Assert.Equal(2, context.DataSourceSnapshots.Count());
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