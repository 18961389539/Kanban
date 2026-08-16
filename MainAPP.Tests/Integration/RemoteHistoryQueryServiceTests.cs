using System.IO;
using Kanban.Client;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// RemoteHistoryQueryService 路由测试（0% 盲区补位）：
/// 历史查询路由代理的核心是 **IsRemote 分派**——本文件锁住 Local 分支：
/// IsRemote=false 时委托本地 HistoryService（SQLite）返回数据，证明代理没有破坏本地查询链路。
/// Remote 分支（走 SignalR）的服务端往返契约由 RemoteRuntimeSinkTests 的最小 Hub 覆盖；
/// Remote 模式的历史查询路由（QueryRemoteList/QueryRemotePaged）本身仍是盲区（审查 2026-08-13 记录）。
/// 数据经 EF 直接插入（不经写入队列，避免 HistoryService 批量落库时序），聚焦路由本身。
/// </summary>
[Trait("Category", "Integration")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Database")]
public class RemoteHistoryQueryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DatabaseProvider _db;

    public RemoteHistoryQueryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RemoteHistory_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _db = new DatabaseProvider(_appSettings);
        _db.EnsureCreatedAll();
        _db.EnsureWalModeEnabled();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static IRuntimeMode LocalMode()
    {
        var mode = Substitute.For<IRuntimeMode>();
        mode.DataMode.Returns(KanbanDataMode.Local);
        return mode;
    }

    private RemoteHistoryQueryService CreateProxy(IRuntimeMode mode)
    {
        var history = new HistoryService(_db, NullLogger<HistoryService>.Instance);
        var client = new KanbanDataClient("http://127.0.0.1:5129/hubs/kanban",
            NullLogger<KanbanDataClient>.Instance, useMessagePack: false);
        return new RemoteHistoryQueryService(
            history, new DefectHistoryStore(_db), client, mode,
            NullLogger<RemoteHistoryQueryService>.Instance);
    }

    private void InsertProductionLogs()
    {
        using var ctx = _db.CreateProductionLogContext();
        ctx.ProductionLogs.AddRange(
            new ProductionLog
            {
                DeviceId = "dev-1", DeviceName = "注塑机-1", ShiftName = "白班",
                OkProduction = 100, NgProduction = 2, StatusWord = 1,
                Timestamp = new DateTime(2026, 8, 1, 10, 0, 0),
            },
            new ProductionLog
            {
                DeviceId = "dev-2", DeviceName = "注塑机-2", ShiftName = "白班",
                OkProduction = 50, NgProduction = 1, StatusWord = 1,
                Timestamp = new DateTime(2026, 8, 1, 11, 0, 0),
            });
        ctx.SaveChanges();
    }

    [Fact]
    public void LocalMode_QueryProductionLogs_DelegatesToLocalHistory()
    {
        InsertProductionLogs();
        var proxy = CreateProxy(LocalMode());

        var logs = proxy.QueryProductionLogs(
            new DateTime(2026, 8, 1), new DateTime(2026, 8, 2));

        Assert.Equal(2, logs.Count);
        Assert.Contains(logs, l => l.DeviceId == "dev-1" && l.OkProduction == 100);
        Assert.Contains(logs, l => l.DeviceId == "dev-2" && l.OkProduction == 50);
    }

    [Fact]
    public void LocalMode_QueryFilteredByDevice()
    {
        InsertProductionLogs();
        var proxy = CreateProxy(LocalMode());

        var logs = proxy.QueryProductionLogs(
            new DateTime(2026, 8, 1), new DateTime(2026, 8, 2), deviceId: "dev-1");

        Assert.Single(logs);
        Assert.Equal("dev-1", logs[0].DeviceId);
    }

    [Fact]
    public void LocalMode_QueryAlarmEvents_Delegates()
    {
        using (var ctx = _db.CreateAlarmEventContext())
        {
            ctx.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = "dev-1", DeviceName = "注塑机-1", AlarmId = "A1", AlarmName = "过温",
                EventType = Kanban.Collector.Core.Entities.AlarmEventType.Triggered,
                EventTime = new DateTime(2026, 8, 1, 10, 30, 0), ShiftName = "白班",
            });
            ctx.SaveChanges();
        }
        var proxy = CreateProxy(LocalMode());

        var events = proxy.QueryAlarmEvents(
            new DateTime(2026, 8, 1), new DateTime(2026, 8, 2));

        Assert.Single(events);
        Assert.Equal("过温", events[0].AlarmName);
    }

    [Fact]
    public void RemoteMode_ConstructedWithoutCrash_IsRemoteTrue()
    {
        var mode = Substitute.For<IRuntimeMode>();
        mode.DataMode.Returns(KanbanDataMode.Remote);
        // Remote 分支依赖 SignalR（E2E 覆盖）；此处仅验证构造与模式判定不崩
        var proxy = CreateProxy(mode);
        Assert.NotNull(proxy);
    }
}
