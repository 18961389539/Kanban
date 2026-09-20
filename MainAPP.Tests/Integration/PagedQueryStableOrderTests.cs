using System.IO;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AlarmEventType = Kanban.Collector.Core.Entities.AlarmEventType;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 分页查询稳定排序键回归测试（审查修复 2026-08-13）：
/// 历史四库分页此前仅按时间排序——同一采集轮次的多条记录共享同一 Timestamp/EventTime，
/// SQLite 对等值键无稳定顺序，翻页时这些行可能重复出现或整页丢失。
/// 修复为追加 Id 次级键后，同时间戳行分页必须无重复、无缺行。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","Database")]
public class PagedQueryStableOrderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _db;

    public PagedQueryStableOrderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanPagedStable_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_appSettings);
        _db.EnsureCreatedAll();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ }
    }

    /// <summary>翻页收集全部 Id，断言 9 条同时间戳记录恰好各出现一次（无重复、无缺行）。</summary>
    private static void AssertPagedCoversAllIdsOnce(List<int> ids, string what)
    {
        Assert.Equal(9, ids.Count);
        Assert.Equal(9, ids.Distinct().Count()); // 无重复
        Assert.Equal(Enumerable.Range(1, 9).OrderBy(i => i), ids.OrderBy(i => i)); // 无缺行
        Assert.True(ids.All(i => i >= 1 && i <= 9), $"{what}: 出现越界 Id");
    }

    [Fact]
    public void ProductionLogs_SameTimestamp_PagedWithoutDupOrLoss()
    {
        var t = new DateTime(2026, 7, 22, 10, 0, 0);
        using (var ctx = _db.CreateProductionLogContext())
        {
            for (int i = 1; i <= 9; i++)
                ctx.ProductionLogs.Add(new ProductionLog
                {
                    DeviceId = "dev-1",
                    DeviceName = "设备1",
                    ShiftName = "白班",
                    OkProduction = i,
                    NgProduction = 0,
                    Timestamp = t,
                });
            ctx.SaveChanges();
        }

        var store = new ProductionHistoryStore(_db, NullLogger<ProductionHistoryStore>.Instance);
        var ids = new List<int>();
        for (int page = 1; page <= 3; page++)
        {
            var (items, total) = store.QueryProductionLogsPaged(t.AddMinutes(-1), t.AddMinutes(1), "dev-1", "白班", page, 4);
            if (page == 1) Assert.Equal(9, total);
            ids.AddRange(items.Select(x => x.Id));
        }
        AssertPagedCoversAllIdsOnce(ids, "ProductionLogs");
    }

    [Fact]
    public void ProductionLogs_Sampled15Min_KeepsLastInBucket()
    {
        var t = new DateTime(2026, 7, 22, 10, 0, 0);
        using (var ctx = _db.CreateProductionLogContext())
        {
            ctx.ProductionLogs.AddRange(
                new ProductionLog { DeviceId = "dev-1", DeviceName = "设备1", ShiftName = "白班", OkProduction = 10, Timestamp = t.AddMinutes(1) },
                new ProductionLog { DeviceId = "dev-1", DeviceName = "设备1", ShiftName = "白班", OkProduction = 12, Timestamp = t.AddMinutes(10) },
                new ProductionLog { DeviceId = "dev-1", DeviceName = "设备1", ShiftName = "白班", OkProduction = 20, Timestamp = t.AddMinutes(16) });
            ctx.SaveChanges();
        }

        var store = new ProductionHistoryStore(_db, NullLogger<ProductionHistoryStore>.Instance);
        var sampled = store.QueryProductionLogsSampled15Min(t.AddMinutes(-1), t.AddMinutes(30), "dev-1", "白班");
        Assert.Equal(2, sampled.Count);
        Assert.Equal(12, sampled[0].OkProduction);
        Assert.Equal(20, sampled[1].OkProduction);
    }

    [Fact]
    public void AlarmEvents_SameTimestamp_PagedWithoutDupOrLoss()
    {
        var t = new DateTime(2026, 7, 22, 10, 0, 0);
        using (var ctx = _db.CreateAlarmEventContext())
        {
            for (int i = 1; i <= 9; i++)
                ctx.AlarmEvents.Add(new AlarmEventRecord
                {
                    DeviceId = "dev-1",
                    DeviceName = "设备1",
                    AlarmId = $"alm-{i}",
                    AlarmName = "报警",
                    PlcAddress = $"M{i}",
                    EventType = AlarmEventType.Triggered,
                    EventTime = t,
                    ShiftName = "白班",
                });
            ctx.SaveChanges();
        }

        using var store = new AlarmHistoryStore(_db, NullLogger<AlarmHistoryStore>.Instance);
        var ids = new List<int>();
        for (int page = 1; page <= 3; page++)
        {
            var (items, total) = store.QueryAlarmEventsPaged(t.AddMinutes(-1), t.AddMinutes(1), "dev-1", "白班", page, 4);
            if (page == 1) Assert.Equal(9, total);
            ids.AddRange(items.Select(x => x.Id));
        }
        AssertPagedCoversAllIdsOnce(ids, "AlarmEvents");
    }

    [Fact]
    public void StatusTransitions_SameTimestamp_PagedWithoutDupOrLoss()
    {
        var t = new DateTime(2026, 7, 22, 10, 0, 0);
        using (var ctx = _db.CreateStatusTransitionContext())
        {
            for (int i = 1; i <= 9; i++)
                ctx.StatusTransitions.Add(new StatusTransitionRecord
                {
                    DeviceId = $"dev-{i}",
                    DeviceName = "设备",
                    PreviousState = 0,
                    CurrentState = 1,
                    EventTime = t,
                    ShiftName = "白班",
                });
            ctx.SaveChanges();
        }

        using var store = new StatusTransitionHistoryStore(_db, NullLogger<StatusTransitionHistoryStore>.Instance);
        var ids = new List<int>();
        for (int page = 1; page <= 3; page++)
        {
            var (items, total) = store.QueryStatusTransitionsPaged(null!, t.AddMinutes(-1), t.AddMinutes(1), "白班", page, 4);
            if (page == 1) Assert.Equal(9, total);
            ids.AddRange(items.Select(x => x.Id));
        }
        AssertPagedCoversAllIdsOnce(ids, "StatusTransitions");
    }

    [Fact]
    public void DefectSnapshots_SameTimestamp_PagedWithoutDupOrLoss()
    {
        var t = new DateTime(2026, 7, 22, 10, 0, 0);
        using (var ctx = _db.CreateDefectHistoryContext())
        {
            for (int i = 1; i <= 9; i++)
                ctx.DefectSnapshots.Add(new DefectSnapshotRecord
                {
                    DeviceId = "dev-1",
                    DeviceName = "设备1",
                    DefectId = $"def-{i}",
                    DefectName = "缺陷",
                    Count = i,
                    Timestamp = t,
                    ShiftName = "白班",
                });
            ctx.SaveChanges();
        }

        using var store = new DefectHistoryStore(_db, NullLogger<DefectHistoryStore>.Instance);
        var ids = new List<int>();
        for (int page = 1; page <= 3; page++)
        {
            var (items, total) = store.QueryDefectSnapshotsPaged(t.AddMinutes(-1), t.AddMinutes(1), "dev-1", page, 4);
            if (page == 1) Assert.Equal(9, total);
            ids.AddRange(items.Select(x => x.Id));
        }
        AssertPagedCoversAllIdsOnce(ids, "DefectSnapshots");
    }
}
