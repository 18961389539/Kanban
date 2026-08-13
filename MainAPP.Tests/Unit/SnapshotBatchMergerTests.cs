using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// SnapshotBatchMerger 测试：RemoteRuntimeSink Dispatcher 节流合并的去重语义
/// （快照是全量状态，同设备只保留最新一帧；不同设备互不覆盖）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class SnapshotBatchMergerTests
{
    private static DeviceSnapshotDto Snapshot(string deviceId, int ok, string? alarmId = null) => new()
    {
        DeviceId = deviceId,
        DeviceName = $"设备-{deviceId}",
        Status = DeviceStatus.Running,
        StatusWord = 1,
        OkProduction = ok,
        NgProduction = 0,
        TotalOkProduction = ok,
        TotalNgProduction = 0,
        RunTime = 0,
        AlarmTime = 0,
        PausedTime = 0,
        Timestamp = DateTime.Now,
        TargetCycle = 30,
        ActiveAlarms = alarmId is null
            ? []
            : [new ActiveAlarmDto { AlarmId = alarmId, Name = alarmId, PlcAddress = "M0", Description = "", Level = AlarmLevel.Low, StartTime = DateTime.Now }],
    };

    [Fact]
    public void SameDevice_MultipleFrames_OnlyLatestRetained()
    {
        var merger = new SnapshotBatchMerger();

        merger.Add(Snapshot("dev-1", ok: 10));
        merger.Add(Snapshot("dev-1", ok: 20));
        merger.Add(Snapshot("dev-1", ok: 30)); // 最新帧

        var drained = merger.Drain();

        var single = Assert.Single(drained);
        Assert.Equal("dev-1", single.DeviceId);
        Assert.Equal(30, single.OkProduction); // 只保留最新一帧
    }

    [Fact]
    public void DifferentDevices_AllFramesRetained()
    {
        var merger = new SnapshotBatchMerger();

        merger.Add(Snapshot("dev-1", ok: 1));
        merger.Add(Snapshot("dev-2", ok: 2));
        merger.Add(Snapshot("dev-3", ok: 3));

        var drained = merger.Drain();

        Assert.Equal(3, drained.Count);
        Assert.Contains(drained, s => s.DeviceId == "dev-1");
        Assert.Contains(drained, s => s.DeviceId == "dev-2");
        Assert.Contains(drained, s => s.DeviceId == "dev-3");
    }

    [Fact]
    public void Drain_ClearsBuffer_NextDrainIsEmpty()
    {
        var merger = new SnapshotBatchMerger();

        merger.Add(Snapshot("dev-1", ok: 1));
        Assert.Single(merger.Drain());
        Assert.Empty(merger.Drain()); // 清空后再取为空
    }

    [Fact]
    public void EmptyBuffer_Drain_ReturnsEmptyList()
    {
        var merger = new SnapshotBatchMerger();

        Assert.Empty(merger.Drain());
    }
}
