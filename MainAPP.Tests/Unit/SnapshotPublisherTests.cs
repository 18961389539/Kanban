using System.IO;
using Kanban.Collector.Services;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using DeviceStatus = Kanban.Contracts.Enums.DeviceStatus;
using AlarmLevel = Kanban.Contracts.Enums.AlarmLevel;

namespace MainAPP.Tests.Unit;

/// <summary>
/// 串行集合：本集合内的测试修改**进程级** KANBAN_DATA_DIR 环境变量，
/// 与依赖默认 DataRoot 的测试（DefectHistoryStoreTests 等）并行会污染彼此路径
/// （曾导致 'no such table: DefectSnapshots' 偶发失败）。禁用并行隔离。
/// </summary>
[CollectionDefinition("EnvIsolation", DisableParallelization = true)]
public class EnvIsolationCollection;

/// <summary>
/// SnapshotPublisher 测试：锁住**增量快照发布**（第七轮扩展性改进的核心逻辑）。
/// - SameSnapshot：业务字段等价比较（排除 Timestamp/Seq、ActiveAlarms 按内容比）——判定正确则
///   静止设备不推、运行设备照推
/// - PublishAll：首帧全量、无变化不推、单设备变化只推该设备（端到端行为）
/// 此前 Collector 服务端零测试，此文件是补盲区第一块。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Collection("EnvIsolation")]
public class SnapshotPublisherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DeviceRepository _repo;
    private readonly SnapshotAggregator _aggregator;
    private readonly SnapshotPublisher _publisher;

    public SnapshotPublisherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KanbanTests", "Publisher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _tempDir);
        _appSettings = new AppSettings();
        _repo = new DeviceRepository(_appSettings);
        _aggregator = new SnapshotAggregator();
        _publisher = new SnapshotPublisher(_repo, _aggregator, Substitute.For<ILogger<SnapshotPublisher>>());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    private static Device MakeDevice(string id, string name = "测试设备")
        => new()
        {
            Id = id,
            Name = name,
            OkCountAddress = "D100",
            NgCountAddress = "D101",
            StatusCountAddress = "D102",
            TargetCycle = 100,
        };

    private static DeviceSnapshotDto MakeSnapshot(string id, int totalOk = 10, DateTime? ts = null)
        => new()
        {
            DeviceId = id,
            DeviceName = "测试设备",
            Status = DeviceStatus.Running,
            TotalOkProduction = totalOk,
            TotalNgProduction = 2,
            Timestamp = ts ?? DateTime.Now,
            Seq = 1,
        };

    // ──────────── SameSnapshot 纯函数 ────────────

    [Fact]
    public void SameSnapshot_IdenticalBusinessFields_ReturnsTrue_IgnoringTimestampAndSeq()
    {
        var a = MakeSnapshot("dev-1", totalOk: 10, ts: new DateTime(2026, 1, 1));
        var b = MakeSnapshot("dev-1", totalOk: 10, ts: new DateTime(2026, 1, 2)); // Timestamp/Seq 不同
        Assert.True(SnapshotPublisher.SameSnapshot(a, b));
    }

    [Theory]
    [InlineData(11, 2)]   // TotalOkProduction 变
    [InlineData(10, 3)]   // TotalNgProduction 变
    [InlineData(10, 2)]   // 其他字段变化（见下方单独用例）
    public void SameSnapshot_ProductionChanged_ReturnsFalse(int ok, int ng)
    {
        var a = MakeSnapshot("dev-1", totalOk: 10);
        var b = a with { TotalOkProduction = ok, TotalNgProduction = ng };
        Assert.Equal(a.TotalOkProduction != ok || a.TotalNgProduction != ng, !SnapshotPublisher.SameSnapshot(a, b));
    }

    [Fact]
    public void SameSnapshot_StatusChanged_ReturnsFalse()
    {
        var a = MakeSnapshot("dev-1");
        var b = a with { Status = DeviceStatus.Alarm };
        Assert.False(SnapshotPublisher.SameSnapshot(a, b));
    }

    [Fact]
    public void SameSnapshot_TimingChanged_ReturnsFalse()
    {
        var a = MakeSnapshot("dev-1");
        var b = a with { RunTime = 100 };
        Assert.False(SnapshotPublisher.SameSnapshot(a, b));
    }

    [Fact]
    public void SameSnapshot_ActiveAlarmsComparedByContent_NotReference()
    {
        var alarm = new ActiveAlarmDto { AlarmId = "A1", Name = "报警A", PlcAddress = "M100", Description = "", Level = AlarmLevel.High, StartTime = DateTime.Now };
        var a = MakeSnapshot("dev-1") with { ActiveAlarms = new List<ActiveAlarmDto> { alarm } };
        var b = MakeSnapshot("dev-1") with { ActiveAlarms = new List<ActiveAlarmDto> { alarm with { } } }; // 同内容、不同列表实例
        // 内容相同（不同引用）→ true（排除引用比较陷阱）
        Assert.True(SnapshotPublisher.SameSnapshot(a, b));
        // 内容不同 → false
        var c = MakeSnapshot("dev-1") with { ActiveAlarms = new List<ActiveAlarmDto>() };
        Assert.False(SnapshotPublisher.SameSnapshot(a, c));
    }

    [Fact]
    public void SameSnapshot_RecipeChanged_ReturnsFalse()
    {
        var a = MakeSnapshot("dev-1");
        // 配方名变化 → 必须触发增量发布（Web 首页配方行依赖此判定）
        Assert.False(SnapshotPublisher.SameSnapshot(a, a with { RecipeName = "配方A" }));
        // 配方值变化 → 同样触发
        Assert.False(SnapshotPublisher.SameSnapshot(a, a with { RecipeName = "配方A", RecipeValue = 99 }));
        // 配方名相同仅 Timestamp/Seq 不同 → true（静止判定不受影响）
        Assert.True(SnapshotPublisher.SameSnapshot(a, a with { RecipeName = "" }));
    }

    // ──────────── PublishAll 端到端增量行为 ────────────

    [Fact]
    public async Task PublishAll_FirstCall_PushesAllDevices()
    {
        _repo.ReplaceAll(new[] { MakeDevice("dev-1"), MakeDevice("dev-2") });
        var reader = await _aggregator.SubscribeAsync(CancellationToken.None);

        _publisher.PublishAll();

        var first = await reader.ReadAsync(CancellationToken.None);
        var second = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal("dev-1", first.DeviceId);
        Assert.Equal("dev-2", second.DeviceId);
    }

    [Fact]
    public async Task PublishAll_NoChange_SecondCall_PushesNothing()
    {
        _repo.ReplaceAll(new[] { MakeDevice("dev-1") });
        var reader = await _aggregator.SubscribeAsync(CancellationToken.None);

        _publisher.PublishAll(); // 首帧全量
        await reader.ReadAsync(CancellationToken.None);

        // 第二次发布（设备运行时无变化——Runtimes 为空，设备配置未变）→ 增量跳过
        _publisher.PublishAll();
        await Task.Delay(300); // 给可能的错误推送留窗口
        Assert.False(reader.TryRead(out _), "无变化的第二轮不应推送任何快照（增量跳过）");
    }

    [Fact]
    public async Task PublishAll_DeviceRuntimeChanged_PushesOnlyThatDevice()
    {
        _repo.ReplaceAll(new[] { MakeDevice("dev-1"), MakeDevice("dev-2") });
        var reader = await _aggregator.SubscribeAsync(CancellationToken.None);

        _publisher.PublishAll(); // 首帧
        await reader.ReadAsync(CancellationToken.None);
        await reader.ReadAsync(CancellationToken.None);

        // 设备 dev-1 产量变化（模拟运行中）
        var device = _repo.GetDevicesSnapshot().First(d => d.Id == "dev-1");
        var runtime = new DeviceRuntime(device) { TotalOkProduction = 999 };
        _repo.Runtimes.Clear();
        _repo.Runtimes.Add(runtime);

        _publisher.PublishAll();

        var pushed = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal("dev-1", pushed.DeviceId);
        Assert.Equal(999, pushed.TotalOkProduction);

        // dev-2 未变化 → 不应有第二条
        await Task.Delay(300); // 给可能的错误推送留窗口
        Assert.False(reader.TryRead(out _), "只有变化的设备应被推送");
    }
}
