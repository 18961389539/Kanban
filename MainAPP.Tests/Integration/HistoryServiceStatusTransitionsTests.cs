using System;
using System.IO;
using System.Linq;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// HistoryService 状态转换记录集成测试（status_transitions.db）。
/// 覆盖：LogStatusTransition + QueryStatusTransitions 往返、按设备过滤、
/// 按时间范围过滤、按班次名称过滤、按 EventTime 升序排序。
/// 使用临时 SQLite 数据库，不依赖真实 PLC 数据。
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","None")]
public class HistoryServiceStatusTransitionsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _db;
    private readonly HistoryService _historyService;

    public HistoryServiceStatusTransitionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanStatusTransTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_appSettings);
        using (var ctx = _db.CreateStatusTransitionContext()) ctx.Database.EnsureCreated();
        _historyService = new HistoryService(_db, NullLogger<HistoryService>.Instance);
    }

    public void Dispose()
    {
        try { _historyService.Dispose(); } catch { }
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void LogAndQuery_RoundTrip_ReturnsSameRecord()
    {
        var t = new DateTime(2026, 7, 23, 8, 0, 0);
        _historyService.LogStatusTransition("dev-001", "设备A", 0, 1, t, "白班");

        var results = _historyService.QueryStatusTransitions("dev-001", t.AddMinutes(-1), t.AddMinutes(1));
        Assert.Single(results);
        Assert.Equal("dev-001", results[0].DeviceId);
        Assert.Equal(1, results[0].CurrentState);
        Assert.Equal(0, results[0].PreviousState);
        Assert.Equal("白班", results[0].ShiftName);
        Assert.Equal(0, results[0].OfflineCause);
    }

    [Fact]
    public void LogAndQuery_PersistsOfflineCause()
    {
        var t = new DateTime(2026, 7, 23, 8, 0, 0);
        _historyService.LogStatusTransition(
            "dev-001", "设备A", 1, 0, t, "白班", (int)Kanban.Contracts.Enums.OfflineCause.CommsLost);

        var results = _historyService.QueryStatusTransitions("dev-001", t.AddMinutes(-1), t.AddMinutes(1));
        Assert.Single(results);
        Assert.Equal(0, results[0].CurrentState);
        Assert.Equal((int)Kanban.Contracts.Enums.OfflineCause.CommsLost, results[0].OfflineCause);
    }

    [Fact]
    public void Query_FiltersByDevice()
    {
        var t = new DateTime(2026, 7, 23, 8, 0, 0);
        _historyService.LogStatusTransition("dev-001", "设备A", 0, 1, t, "白班");
        _historyService.LogStatusTransition("dev-002", "设备B", 0, 2, t, "白班");

        var dev1 = _historyService.QueryStatusTransitions("dev-001", t.AddMinutes(-1), t.AddMinutes(1));
        Assert.Single(dev1);
        Assert.Equal("dev-001", dev1[0].DeviceId);

        var dev2 = _historyService.QueryStatusTransitions("dev-002", t.AddMinutes(-1), t.AddMinutes(1));
        Assert.Single(dev2);
        Assert.Equal("dev-002", dev2[0].DeviceId);
    }

    [Fact]
    public void Query_FiltersByTimeRange()
    {
        var t = new DateTime(2026, 7, 23, 8, 0, 0);
        _historyService.LogStatusTransition("dev-001", "设备A", 0, 1, t);
        _historyService.LogStatusTransition("dev-001", "设备A", 1, 2, t.AddHours(2));

        // 仅查 8:00 附近 → 只返回第一条
        var near = _historyService.QueryStatusTransitions("dev-001", t.AddMinutes(-1), t.AddMinutes(1));
        Assert.Single(near);
        Assert.Equal(1, near[0].CurrentState);

        // 查全天 → 两条
        var all = _historyService.QueryStatusTransitions("dev-001", t.AddMinutes(-1), t.AddHours(3));
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Query_FiltersByShiftName()
    {
        var t = new DateTime(2026, 7, 23, 8, 0, 0);
        _historyService.LogStatusTransition("dev-001", "设备A", 0, 1, t, "白班");
        _historyService.LogStatusTransition("dev-001", "设备A", 1, 2, t.AddHours(1), "夜班");

        var day = _historyService.QueryStatusTransitions("dev-001", t.AddMinutes(-1), t.AddHours(3), shiftName: "白班");
        Assert.Single(day);
        Assert.Equal("白班", day[0].ShiftName);

        var night = _historyService.QueryStatusTransitions("dev-001", t.AddMinutes(-1), t.AddHours(3), shiftName: "夜班");
        Assert.Single(night);
        Assert.Equal("夜班", night[0].ShiftName);
    }

    [Fact]
    public void Query_OrderedByEventTimeAscending()
    {
        var t0 = new DateTime(2026, 7, 23, 8, 0, 0);
        // 乱序写入
        _historyService.LogStatusTransition("dev-001", "设备A", 1, 2, t0.AddMinutes(20));
        _historyService.LogStatusTransition("dev-001", "设备A", 0, 1, t0);
        _historyService.LogStatusTransition("dev-001", "设备A", 2, 3, t0.AddMinutes(40));

        var results = _historyService.QueryStatusTransitions("dev-001", t0.AddMinutes(-1), t0.AddMinutes(60));
        Assert.Equal(3, results.Count);
        Assert.Equal(t0, results[0].EventTime);
        Assert.Equal(t0.AddMinutes(20), results[1].EventTime);
        Assert.Equal(t0.AddMinutes(40), results[2].EventTime);
    }

    [Fact]
    public void Query_NoMatch_ReturnsEmptyList()
    {
        var results = _historyService.QueryStatusTransitions("dev-001",
            new DateTime(2020, 1, 1), new DateTime(2020, 1, 2));
        Assert.Empty(results);
    }
}
