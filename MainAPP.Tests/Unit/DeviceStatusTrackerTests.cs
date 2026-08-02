using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// DeviceStatusTracker 单元测试：覆盖状态转换检测、首次写入、离线转换、
/// 写失败重试、设备删除清理、班次切换重置等核心路径。
///
/// DeviceStatusTracker 是 PlcDataAcquisitionService 拆分出的协作组件，
/// 拥有 _prevStatusWords 字典及专用锁。此测试文件独立验证其行为。
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DeviceStatusTrackerTests
{
    private static readonly ILogger<DeviceStatusTracker> Logger =
        NullLogger<DeviceStatusTracker>.Instance;

    private static Device BuildDevice(string id = "dev-001", string name = "测试设备1")
        => new() { Id = id, Name = name };

    // ──────────── ReadAndUpdate：首次写入 ────────────

    [Fact]
    public void ReadAndUpdate_FirstRead_LogsInitialTransition()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        var ok = tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);

        Assert.True(ok);
        var evt = Assert.Single(history.StatusTransitions);
        Assert.Equal(0, evt.PreviousState);
        Assert.Equal((int)DeviceStatus.Running, evt.CurrentState);
        Assert.Equal("白班", evt.ShiftName);
        Assert.Equal((int)DeviceStatus.Running, tracker.GetPrevStatusWordsSnapshot()[device.Id]);
    }

    [Fact]
    public void ReadAndUpdate_SameStatus_NoTransitionLogged()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);
        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);

        Assert.Single(history.StatusTransitions);
    }

    // ──────────── ReadAndUpdate：状态转换 ────────────

    [Fact]
    public void ReadAndUpdate_StatusChange_LogsTransitionWithPreviousState()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);
        tracker.ReadAndUpdate(device, (int)DeviceStatus.Alarm, history, "白班", Logger);

        Assert.Equal(2, history.StatusTransitions.Count);
        Assert.Equal((int)DeviceStatus.Running, history.StatusTransitions[1].PreviousState);
        Assert.Equal((int)DeviceStatus.Alarm, history.StatusTransitions[1].CurrentState);
    }

    [Theory]
    [InlineData((int)DeviceStatus.Running, (int)DeviceStatus.Alarm)]
    [InlineData((int)DeviceStatus.Alarm, (int)DeviceStatus.Paused)]
    [InlineData((int)DeviceStatus.Paused, (int)DeviceStatus.Running)]
    [InlineData((int)DeviceStatus.Running, (int)DeviceStatus.Unknown)]
    public void ReadAndUpdate_VariousTransitions_AllLogged(int from, int to)
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        tracker.ReadAndUpdate(device, from, history, "白班", Logger);
        tracker.ReadAndUpdate(device, to, history, "白班", Logger);

        Assert.Equal(2, history.StatusTransitions.Count);
        Assert.Equal(from, history.StatusTransitions[1].PreviousState);
        Assert.Equal(to, history.StatusTransitions[1].CurrentState);
    }

    // ──────────── ReadAndUpdate：写失败重试 ────────────

    [Fact]
    public void ReadAndUpdate_FirstWriteFails_StateNotUpdated()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();
        history.ShouldFailStatusTransitionWrite = true;

        var ok = tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);

        Assert.False(ok);
        Assert.Empty(history.StatusTransitions);
        // 写失败时 _prevStatusWords 不应更新
        Assert.False(tracker.GetPrevStatusWordsSnapshot().ContainsKey(device.Id));
    }

    [Fact]
    public void ReadAndUpdate_TransitionWriteFails_StateNotUpdated()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        // 首次写入成功
        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);
        Assert.Single(history.StatusTransitions);

        // 配置写入失败 + 状态转换
        history.ShouldFailStatusTransitionWrite = true;
        var ok = tracker.ReadAndUpdate(device, (int)DeviceStatus.Alarm, history, "白班", Logger);

        Assert.False(ok);
        // _prevStatusWords 仍为 Running，下次会重新尝试 Alarm 转换
        Assert.Equal((int)DeviceStatus.Running, tracker.GetPrevStatusWordsSnapshot()[device.Id]);

        // 解除失败后下一轮应成功写入
        history.ShouldFailStatusTransitionWrite = false;
        tracker.ReadAndUpdate(device, (int)DeviceStatus.Alarm, history, "白班", Logger);
        Assert.Equal(2, history.StatusTransitions.Count);
        Assert.Equal((int)DeviceStatus.Alarm, tracker.GetPrevStatusWordsSnapshot()[device.Id]);
    }

    // ──────────── LogOfflineTransition ────────────

    [Fact]
    public void LogOfflineTransition_FromRunning_LogsOfflineTransition()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        // 设备处于运行状态
        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);

        // 标记离线
        tracker.LogOfflineTransition(device, history, "白班", Logger);

        Assert.Equal(2, history.StatusTransitions.Count);
        Assert.Equal((int)DeviceStatus.Running, history.StatusTransitions[1].PreviousState);
        Assert.Equal((int)DeviceStatus.Unknown, history.StatusTransitions[1].CurrentState);
        Assert.Equal((int)DeviceStatus.Unknown, tracker.GetPrevStatusWordsSnapshot()[device.Id]);
    }

    [Fact]
    public void LogOfflineTransition_AlreadyOffline_NoEventWritten()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        // 设备已离线（首次写入 0）
        tracker.ReadAndUpdate(device, (int)DeviceStatus.Unknown, history, "白班", Logger);
        Assert.Single(history.StatusTransitions);

        tracker.LogOfflineTransition(device, history, "白班", Logger);

        // 已离线，不应重复写入
        Assert.Single(history.StatusTransitions);
    }

    [Fact]
    public void LogOfflineTransition_NoStateRecorded_NoEventWritten()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        // 设备从未被读取过（_prevStatusWords 中无记录）
        tracker.LogOfflineTransition(device, history, "白班", Logger);

        Assert.Empty(history.StatusTransitions);
    }

    [Fact]
    public void LogOfflineTransition_WriteFails_StateNotUpdated()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);
        history.ShouldFailStatusTransitionWrite = true;

        tracker.LogOfflineTransition(device, history, "白班", Logger);

        // 写失败时 _prevStatusWords 不应被置 0
        Assert.Equal((int)DeviceStatus.Running, tracker.GetPrevStatusWordsSnapshot()[device.Id]);
    }

    [Fact]
    public void LogOfflineTransition_AfterOffline_ReconnectWritesNewTransition()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        // 运行 → 离线
        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);
        tracker.LogOfflineTransition(device, history, "白班", Logger);
        Assert.Equal(2, history.StatusTransitions.Count);

        // 重连后真实状态读取：0 → Running，不应重复刷写
        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);

        Assert.Equal(3, history.StatusTransitions.Count);
        Assert.Equal((int)DeviceStatus.Unknown, history.StatusTransitions[2].PreviousState);
        Assert.Equal((int)DeviceStatus.Running, history.StatusTransitions[2].CurrentState);
    }

    // ──────────── RemoveDevice / ResetAll ────────────

    [Fact]
    public void RemoveDevice_ClearsStateRecord()
    {
        var tracker = new DeviceStatusTracker();
        var device = BuildDevice();
        var history = new InMemoryHistoryService();

        tracker.ReadAndUpdate(device, (int)DeviceStatus.Running, history, "白班", Logger);
        Assert.True(tracker.GetPrevStatusWordsSnapshot().ContainsKey(device.Id));

        tracker.RemoveDevice(device.Id);

        Assert.False(tracker.GetPrevStatusWordsSnapshot().ContainsKey(device.Id));
    }

    [Fact]
    public void RemoveDevice_UnknownDevice_NoThrow()
    {
        var tracker = new DeviceStatusTracker();
        tracker.RemoveDevice("non-existent");
    }

    [Fact]
    public void ResetAll_ClearsAllStateRecords()
    {
        var tracker = new DeviceStatusTracker();
        var history = new InMemoryHistoryService();

        for (int i = 0; i < 3; i++)
        {
            var d = BuildDevice($"dev-{i}", $"设备{i}");
            tracker.ReadAndUpdate(d, (int)DeviceStatus.Running, history, "白班", Logger);
        }
        Assert.Equal(3, tracker.GetPrevStatusWordsSnapshot().Count);

        tracker.ResetAll();

        Assert.Empty(tracker.GetPrevStatusWordsSnapshot());
    }

    // ──────────── 多设备 ────────────

    [Fact]
    public void ReadAndUpdate_MultipleDevices_StatesTrackedIndependently()
    {
        var tracker = new DeviceStatusTracker();
        var history = new InMemoryHistoryService();
        var d1 = BuildDevice("dev-1", "设备1");
        var d2 = BuildDevice("dev-2", "设备2");

        tracker.ReadAndUpdate(d1, (int)DeviceStatus.Running, history, "白班", Logger);
        tracker.ReadAndUpdate(d2, (int)DeviceStatus.Alarm, history, "白班", Logger);
        tracker.ReadAndUpdate(d1, (int)DeviceStatus.Paused, history, "白班", Logger);

        Assert.Equal(3, history.StatusTransitions.Count);
        var snapshot = tracker.GetPrevStatusWordsSnapshot();
        Assert.Equal((int)DeviceStatus.Paused, snapshot["dev-1"]);
        Assert.Equal((int)DeviceStatus.Alarm, snapshot["dev-2"]);
    }
}
