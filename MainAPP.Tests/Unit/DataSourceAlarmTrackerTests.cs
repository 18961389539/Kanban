using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// DataSourceAlarmTracker 单元测试：数值型（越限延时确认 / 滞回恢复 / 首采样基线）
/// 与非数值型（预期值偏离立即触发 / 回预期恢复）两条独立判定路径，以及断线清理。
/// 使用单一 tracker 实例 + 可变时钟驱动时间流逝（new tracker 会丢状态，禁止用多实例模拟时序）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class DataSourceAlarmTrackerTests
{
    private sealed class RecordingAlarmHistory : IAlarmHistoryService
    {
        public List<AlarmEventRecord> Events { get; } = [];

        public AlarmEventRecord? GetLatestAlarmEvent(string alarmId) => null;
        public AlarmEventRecord? GetLatestAlarmEventStrict(string alarmId) => throw new NotImplementedException();
        public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId, string alarmName,
            string plcAddress, AlarmEventType eventType, DateTime eventTime, string? shiftName = null)
        {
            Events.Add(new AlarmEventRecord
            {
                DeviceId = deviceId, DeviceName = deviceName, AlarmId = alarmId, AlarmName = alarmName,
                PlcAddress = plcAddress, EventType = eventType, EventTime = eventTime, ShiftName = shiftName ?? "",
            });
            return true;
        }
        public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null) => [];
        public List<AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null) => [];
        public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds) => [];
        public (List<AlarmEventRecord> Items, int Total) QueryAlarmEventsPaged(DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize) => ([], 0);
    }

    private static (Device device, DataSource source, DataSourceValue value) BuildDeviceWithSource(
        string deviceId = "dev-001",
        int? limitMin = null, int? limitMax = null, int? expected = null,
        int hysteresis = 0, int confirmSeconds = 5)
    {
        var device = new Device { Id = deviceId, Name = "测试设备1" };
        var valueItem = new DataSourceValue
        {
            Name = "温度",
            PlcAddress = "D300",
            LimitMin = limitMin ?? 0,
            LimitMax = limitMax ?? 0,
            Hysteresis = hysteresis,
            ConfirmSeconds = confirmSeconds,
            ExpectedValue = expected,
        };
        var source = new DataSource { DeviceId = device.Id, Name = "车间温度" };
        source.Values.Add(valueItem);
        device.Sources.Add(source);
        return (device, source, valueItem);
    }

    /// <summary>可变时钟 tracker：nowProvider 引用捕获的 now 变量，测试中用 advance(秒) 推进时间。</summary>
    private static (DataSourceAlarmTracker Tracker, Action<double> Advance) CreateClockTracker(
        RecordingAlarmHistory history,
        DateTime start)
    {
        var now = start;
        var tracker = new DataSourceAlarmTracker(
            history, notificationChannel: null, onAlarmEdge: null,
            logger: NullLogger.Instance, nowProvider: () => now);
        return (tracker, seconds => now = now.AddSeconds(seconds));
    }

    // ──────────── 数值型：首采样基线 ────────────

    [Fact]
    public void Numeric_FirstSampleAlreadyOutOfRange_DoesNotAlarm()
    {
        var history = new RecordingAlarmHistory();
        var (device, source, value) = BuildDeviceWithSource(limitMin: 200, limitMax: 300);
        var (tracker, _) = CreateClockTracker(history, new DateTime(2026, 8, 17, 8, 0, 0));

        tracker.Observe(device, source, value, 999, "白班"); // 首个采样已越限：只建基线

        // 首采样不产生告警事件（IsTriggered 是静态值判定，值越限时恒 true，不代表已触发告警）
        Assert.Empty(history.Events);
    }

    // ──────────── 数值型：延时确认触发 ────────────

    [Fact]
    public void Numeric_OutOfRangeLongerThanConfirmSeconds_Triggers()
    {
        var history = new RecordingAlarmHistory();
        var (device, source, value) = BuildDeviceWithSource(limitMin: 200, limitMax: 300, confirmSeconds: 5);
        var (tracker, advance) = CreateClockTracker(history, new DateTime(2026, 8, 17, 8, 0, 0));

        tracker.Observe(device, source, value, 250, "白班"); // 基线
        Assert.Empty(history.Events);

        advance(1);
        tracker.Observe(device, source, value, 350, "白班"); // 越限第 1 秒：pending 开始
        Assert.Empty(history.Events);

        advance(3);
        tracker.Observe(device, source, value, 360, "白班"); // 越限第 4 秒：仍 pending
        Assert.Empty(history.Events);

        advance(2);
        tracker.Observe(device, source, value, 365, "白班"); // 越限第 6 秒：≥ ConfirmSeconds 触发
        var evt = Assert.Single(history.Events);
        Assert.Equal(AlarmEventType.Triggered, evt.EventType);
        Assert.StartsWith("src:", evt.AlarmId, StringComparison.Ordinal);
        Assert.Equal("D300", evt.PlcAddress);
        Assert.True(value.IsTriggered);
    }

    // ──────────── 数值型：尖峰短暂越限不误报 ────────────

    [Fact]
    public void Numeric_ShortSpikeBelowConfirmSeconds_DoesNotAlarm()
    {
        var history = new RecordingAlarmHistory();
        var (device, source, value) = BuildDeviceWithSource(limitMin: 200, limitMax: 300, confirmSeconds: 5);
        var (tracker, advance) = CreateClockTracker(history, new DateTime(2026, 8, 17, 8, 0, 0));

        tracker.Observe(device, source, value, 250, "白班"); // 基线
        advance(1);
        tracker.Observe(device, source, value, 350, "白班"); // 短暂越限
        advance(2);
        tracker.Observe(device, source, value, 245, "白班"); // 3s 内回落（< 5s）

        Assert.Empty(history.Events);
        Assert.False(value.IsTriggered);
    }

    // ──────────── 数值型：滞回恢复 ────────────

    [Fact]
    public void Numeric_RecoversOnlyPastHysteresisBand()
    {
        var history = new RecordingAlarmHistory();
        var (device, source, value) = BuildDeviceWithSource(limitMin: 200, limitMax: 300, confirmSeconds: 5, hysteresis: 10);
        var (tracker, advance) = CreateClockTracker(history, new DateTime(2026, 8, 17, 8, 0, 0));

        tracker.Observe(device, source, value, 250, "白班"); // 基线
        advance(5);
        tracker.Observe(device, source, value, 350, "白班"); // 越限第 1 秒：pending 开始
        Assert.Empty(history.Events);

        advance(5);
        tracker.Observe(device, source, value, 360, "白班"); // 越限持续 5 秒：延时确认触发
        Assert.Single(history.Events);

        advance(1);
        tracker.Observe(device, source, value, 305, "白班"); // 回到限内但未过恢复线（>300-10）：告警保持
        Assert.Single(history.Events);

        advance(1);
        tracker.Observe(device, source, value, 285, "白班"); // 过恢复线（≤290）：恢复
        Assert.Equal(2, history.Events.Count);
        Assert.Equal(AlarmEventType.Recovered, history.Events[1].EventType);
        Assert.False(value.IsTriggered);
    }

    // ──────────── 非数值型：预期值偏离立即触发与恢复 ────────────

    [Fact]
    public void ExpectedValue_DeviationTriggersImmediately_AndRecoversOnReturn()
    {
        var history = new RecordingAlarmHistory();
        var (device, source, value) = BuildDeviceWithSource(expected: 1);
        var (tracker, _) = CreateClockTracker(history, new DateTime(2026, 8, 17, 8, 0, 0));

        tracker.Observe(device, source, value, 1, "白班"); // 基线
        Assert.Empty(history.Events);

        tracker.Observe(device, source, value, 0, "白班"); // 偏离预期值 → 立即触发
        var evt = Assert.Single(history.Events);
        Assert.Equal(AlarmEventType.Triggered, evt.EventType);
        Assert.True(value.IsTriggered);

        tracker.Observe(device, source, value, 1, "白班"); // 回到预期 → 恢复
        Assert.Equal(2, history.Events.Count);
        Assert.Equal(AlarmEventType.Recovered, history.Events[1].EventType);
        Assert.False(value.IsTriggered);
    }

    // ──────────── 断线清理：重连后按新基线处理，不残留旧状态 ────────────

    [Fact]
    public void RecoverAllOnDisconnect_ResetsState_SoReconnectSamplingStartsFresh()
    {
        var history = new RecordingAlarmHistory();
        var (device, source, value) = BuildDeviceWithSource(expected: 1);
        var (tracker, _) = CreateClockTracker(history, new DateTime(2026, 8, 17, 8, 0, 0));

        tracker.Observe(device, source, value, 1, "白班"); // 基线
        tracker.Observe(device, source, value, 0, "白班"); // 触发
        Assert.Single(history.Events);

        tracker.RecoverAllOnDisconnect("白班");

        // 断线后重新采样（重连）：按首采样基线处理，旧触发状态不再生效（不产生新事件）
        tracker.Observe(device, source, value, 0, "白班");
        Assert.Single(history.Events);
    }
}