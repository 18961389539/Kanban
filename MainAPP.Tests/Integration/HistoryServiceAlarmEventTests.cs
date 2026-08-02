using System;
using System.IO;
using System.Linq;
using MainAPP.Data;
using MainAPP.Entities;
using MainAPP.Models;
using MainAPP.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// HistoryService 集成测试：AlarmEvents 持久化与查询
/// 验证真实 SQLite 数据库的写入/查询/清理流程
/// </summary>
[Trait("Category","Integration")]
[Trait("Speed","Slow")]
[Trait("Requires","None")]
public class HistoryServiceAlarmEventTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _db;
    private readonly HistoryService _historyService;

    public HistoryServiceAlarmEventTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanHistTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_appSettings);

        // 确保三个数据库均已建表
        using (var ctx = _db.CreateAlarmEventContext()) ctx.Database.EnsureCreated();
        using (var ctx = _db.CreateProductionLogContext()) ctx.Database.EnsureCreated();
        using (var ctx = _db.CreateStatusTransitionContext()) ctx.Database.EnsureCreated();

        _historyService = new HistoryService(_db, NullLogger<HistoryService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            _historyService.Dispose();
            Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void LogAlarmEvent_TriggerEvent_WritesSyncAndQueryable()
    {
        // EventType=Triggered（触发）走同步写入路径
        var eventTime = new DateTime(2026, 7, 22, 10, 30, 0);
        var ok = _historyService.LogAlarmEvent(
            "dev-001", "测试设备", "alm-001", "高温报警", "M100", AlarmEventType.Triggered, eventTime);

        Assert.True(ok);

        var results = _historyService.QueryAlarmEvents(
            eventTime.AddMinutes(-1), eventTime.AddMinutes(1));
        Assert.Single(results);
        var record = results[0];
        Assert.Equal("dev-001", record.DeviceId);
        Assert.Equal("测试设备", record.DeviceName);
        Assert.Equal("alm-001", record.AlarmId);
        Assert.Equal("高温报警", record.AlarmName);
        Assert.Equal("M100", record.PlcAddress);
        Assert.Equal(AlarmEventType.Triggered, record.EventType);
        Assert.Equal(eventTime, record.EventTime);
    }

    [Fact]
    public void QueryAlarmEvents_FilterByDeviceId()
    {
        var t = new DateTime(2026, 7, 22, 10, 0, 0);
        _historyService.LogAlarmEvent("dev-A", "设备A", "alm-A1", "A报警", "M100", AlarmEventType.Triggered, t);
        _historyService.LogAlarmEvent("dev-B", "设备B", "alm-B1", "B报警", "M200", AlarmEventType.Triggered, t);

        var allResults = _historyService.QueryAlarmEvents(t.AddMinutes(-1), t.AddMinutes(1));
        Assert.Equal(2, allResults.Count);

        var devAResults = _historyService.QueryAlarmEvents(
            t.AddMinutes(-1), t.AddMinutes(1), "dev-A");
        Assert.Single(devAResults);
        Assert.Equal("dev-A", devAResults[0].DeviceId);
    }

    [Fact]
    public void QueryAlarmEventsByAlarmId_ReturnsMatchingRecords()
    {
        var t1 = new DateTime(2026, 7, 22, 10, 0, 0);
        var t2 = new DateTime(2026, 7, 22, 11, 0, 0);
        // EventType=Triggered（触发）和 EventType=ShiftChange（班次切换）走同步写入，确保查询时已落库
        _historyService.LogAlarmEvent("dev-1", "设备1", "alm-001", "报警", "M100", AlarmEventType.Triggered, t1);
        _historyService.LogAlarmEvent("dev-1", "设备1", "alm-001", "报警", "M100", AlarmEventType.ShiftChange, t2);
        _historyService.LogAlarmEvent("dev-1", "设备1", "alm-002", "其他报警", "M200", AlarmEventType.Triggered, t1);

        var results = _historyService.QueryAlarmEvents(t1.AddMinutes(-1), t2.AddMinutes(1))
            .Where(r => r.AlarmId == "alm-001")
            .ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("alm-001", r.AlarmId));
        Assert.Equal(t1, results[0].EventTime);
        Assert.Equal(t2, results[1].EventTime);
    }

    [Fact]
    public void QueryAlarmEvents_TimeRangeFilter()
    {
        var t1 = new DateTime(2026, 7, 22, 10, 0, 0);
        var t2 = new DateTime(2026, 7, 22, 12, 0, 0);
        var t3 = new DateTime(2026, 7, 22, 14, 0, 0);
        _historyService.LogAlarmEvent("d", "n", "a", "x", "M1", AlarmEventType.Triggered, t1);
        _historyService.LogAlarmEvent("d", "n", "a", "x", "M1", AlarmEventType.Triggered, t2);
        _historyService.LogAlarmEvent("d", "n", "a", "x", "M1", AlarmEventType.Triggered, t3);

        // 只查 11:00-13:00
        var results = _historyService.QueryAlarmEvents(
            new DateTime(2026, 7, 22, 11, 0, 0),
            new DateTime(2026, 7, 22, 13, 0, 0));
        Assert.Single(results);
        Assert.Equal(t2, results[0].EventTime);
    }

    [Fact]
    public void QueryAlarmEvents_NoMatch_ReturnsEmptyList()
    {
        var results = _historyService.QueryAlarmEvents(
            new DateTime(2020, 1, 1), new DateTime(2020, 1, 2));
        Assert.Empty(results);
    }

    [Fact]
    public void QueryAlarmEvents_ReturnsOrderedByEventTime()
    {
        var t1 = new DateTime(2026, 7, 22, 10, 0, 0);
        var t3 = new DateTime(2026, 7, 22, 14, 0, 0);
        var t2 = new DateTime(2026, 7, 22, 12, 0, 0);
        // 乱序写入
        _historyService.LogAlarmEvent("d", "n", "a", "x", "M1", AlarmEventType.Triggered, t3);
        _historyService.LogAlarmEvent("d", "n", "a", "x", "M1", AlarmEventType.Triggered, t1);
        _historyService.LogAlarmEvent("d", "n", "a", "x", "M1", AlarmEventType.Triggered, t2);

        var results = _historyService.QueryAlarmEvents(
            new DateTime(2026, 7, 22, 0, 0, 0),
            new DateTime(2026, 7, 23, 0, 0, 0));
        Assert.Equal(3, results.Count);
        Assert.Equal(t1, results[0].EventTime);
        Assert.Equal(t2, results[1].EventTime);
        Assert.Equal(t3, results[2].EventTime);
    }
}
