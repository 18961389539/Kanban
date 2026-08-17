using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Contracts.Dtos;
using Microsoft.Extensions.Logging;
using AlarmEventType = Kanban.Collector.Core.Entities.AlarmEventType;
using AlarmLevel = Kanban.Collector.Core.Models.AlarmLevel;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 数据源采集源告警状态机（挂在 PlcScanPipeline 内，随扫描轮询驱动）。
/// 负责"进入越限/偏离 → 延时确认 → 触发 → 滞回恢复"的边沿判定与告警事件写入，
/// 与 <see cref="AlarmStateTracker"/>（M 位报警）职责对等，但基于数值判定而非位边沿。
///
/// 规则（设计稿 §4 定稿）：
/// - 数值型（配置上下限）：值越出 [Min, Max] 且持续 ≥ ConfirmSeconds 才触发；回落「限值∓滞回」以内才恢复。
/// - 非数值型（配置预期值）：当前值 ≠ 预期值立即触发；回到预期值即恢复。
/// - 首次有效采样只建立基线，不触发报警（启动时已处于越限不算"进入"）。
/// - 告警事件复用 alarm_events（AlarmId 以 "src:" 前缀与 PLC 设备报警区分来源）；
///   数据源告警不参与设备状态机、不污染 OEE。
/// </summary>
public sealed class DataSourceAlarmTracker
{
    private readonly IAlarmHistoryService _alarmHistory;
    private readonly ILogger _logger;
    private readonly IAlarmNotificationChannel? _notificationChannel;
    private readonly Func<DateTime> _nowProvider;

    /// <summary>api 键：{deviceId}:{sourceId} → 状态。</summary>
    private readonly Dictionary<string, SourceAlarmState> _states = new(StringComparer.OrdinalIgnoreCase);

    private sealed class SourceAlarmState
    {
        /// <summary>是否已见过首个有效采样（基线）。</summary>
        public bool FirstSampleSeen;

        /// <summary>数值型：是否已进入越限、等待延时确认。</summary>
        public bool PendingOutOfRange;

        /// <summary>数值型：pending 起始时刻（用于延时确认累计）。</summary>
        public DateTime PendingSince;

        /// <summary>是否已确认触发告警（等待恢复）。</summary>
        public bool Confirmed;
    }

    public DataSourceAlarmTracker(
        IAlarmHistoryService alarmHistory,
        IAlarmNotificationChannel? notificationChannel = null,
        Action<AlarmEventDto>? onAlarmEdge = null,
        ILogger? logger = null,
        Func<DateTime>? nowProvider = null)
    {
        _alarmHistory = alarmHistory;
        _notificationChannel = notificationChannel;
        _onAlarmEdge = onAlarmEdge;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    private readonly Action<AlarmEventDto>? _onAlarmEdge;

    /// <summary>
    /// 注入一次采样（成功读取后调用）。更新 source.CurrentValue 并驱动告警状态机。
    /// 调用方需保证仅对启用中的源且读取成功后调用（读取失败不驱动状态机，避免瞬时故障误判恢复）。
    /// </summary>
    public void Observe(Device device, DataSource source, int value, string shiftName)
    {
        source.CurrentValue = value;
        var now = _nowProvider();
        var key = GetKey(device.Id, source.Id);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new SourceAlarmState();
            _states[key] = state;
        }

        // 首个有效采样只建立基线：应用启动时已处于越限/偏离不算新告警。
        if (!state.FirstSampleSeen)
        {
            state.FirstSampleSeen = true;
            return;
        }

        if (source.HasLimits)
        {
            EvaluateNumeric(device, source, state, now, shiftName);
        }
        else if (source.HasExpectedValue)
        {
            EvaluateExpected(device, source, state, now, shiftName);
        }
    }

    private void EvaluateNumeric(Device device, DataSource source, SourceAlarmState state, DateTime now, string shiftName)
    {
        if (source.IsOutOfRange)
        {
            // 越出 [Min, Max]：开始/保持 pending，累计延时
            if (!state.PendingOutOfRange)
            {
                state.PendingOutOfRange = true;
                state.PendingSince = now;
            }
            else if (!state.Confirmed
                     && (now - state.PendingSince).TotalSeconds >= Math.Max(0, source.ConfirmSeconds))
            {
                Trigger(device, source, now, shiftName);
                state.Confirmed = true;
            }
        }
        else if (source.IsBackInRange)
        {
            // 回到「限值 ∓ 滞回」以内：解除告警；未确认的 pending 一并取消
            if (state.Confirmed)
            {
                Recover(device, source, now, shiftName);
            }
            state.PendingOutOfRange = false;
            state.Confirmed = false;
        }
        else
        {
            // 滞回带内（已回限内但未回恢复线）：
            // - 已确认的告警保持触发，直到真正回到恢复线；
            // - 未确认的 pending 视为尖峰消退，取消延时累计（不触发）。
            state.PendingOutOfRange = false;
        }
    }

    private void EvaluateExpected(Device device, DataSource source, SourceAlarmState state, DateTime now, string shiftName)
    {
        if (source.IsDeviatingFromExpected)
        {
            // 离散量不做延时：立即触发（设计稿 §4）
            if (!state.Confirmed)
            {
                Trigger(device, source, now, shiftName);
                state.Confirmed = true;
            }
        }
        else if (state.Confirmed)
        {
            Recover(device, source, now, shiftName);
            state.Confirmed = false;
        }
    }

    private void Trigger(Device device, DataSource source, DateTime now, string shiftName)
    {
        var alarmId = SourceAlarmId(source.Id);
        _alarmHistory.LogAlarmEvent(
            device.Id, device.Name, alarmId, source.Name, source.PlcAddress,
            AlarmEventType.Triggered, now, shiftName);
        TryNotify(device, source, now);
        TryPublishEdge(device, source, source.PlcAddress, AlarmEventType.Triggered, AlarmLevel.Medium, now, shiftName);
        _logger.LogInformation("数据源 {Source}（设备 {Device}）触发告警：当前值 {Value}{Unit}",
            source.Name, device.Name, source.CurrentValue, source.Unit);
    }

    private void Recover(Device device, DataSource source, DateTime now, string shiftName)
    {
        var alarmId = SourceAlarmId(source.Id);
        _alarmHistory.LogAlarmEvent(
            device.Id, device.Name, alarmId, source.Name, source.PlcAddress,
            AlarmEventType.Recovered, now, shiftName);
        TryPublishEdge(device, source, source.PlcAddress, AlarmEventType.Recovered, AlarmLevel.Medium, now, shiftName);
        _logger.LogInformation("数据源 {Source}（设备 {Device}）告警恢复：当前值 {Value}{Unit}",
            source.Name, device.Name, source.CurrentValue, source.Unit);
    }

    private void TryNotify(Device device, DataSource source, DateTime now)
    {
        try
        {
            _notificationChannel?.Enqueue(new AlarmNotification(
                device.Id, device.Name, SourceAlarmId(source.Id), source.Name, AlarmLevel.Medium, now));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "数据源告警通知通道执行失败：设备={Device} 源={Source}", device.Name, source.Name);
        }
    }

    private void TryPublishEdge(Device device, DataSource source, string alarmAddress,
        AlarmEventType eventType, AlarmLevel level, DateTime now, string shiftName)
    {
        try
        {
            _onAlarmEdge?.Invoke(new AlarmEventDto
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                AlarmId = SourceAlarmId(source.Id),
                AlarmName = source.Name,
                PlcAddress = alarmAddress,
                EventType = (Kanban.Contracts.Enums.AlarmEventType)(int)eventType,
                Level = (Kanban.Contracts.Enums.AlarmLevel)(int)level,
                EventTime = now,
                ShiftName = shiftName,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "数据源告警边沿事件推送失败：设备={Device} 源={Source}", device.Name, source.Name);
        }
    }

    /// <summary>PLC 断线/停止：为所有已触发的数据源告警写恢复事件并重置状态。</summary>
    public void RecoverAllOnDisconnect(string shiftName)
    {
        // 断线场景拿不到设备配置名，仅重置状态不写恢复事件（恢复事件由重连后下一轮真实采样驱动；
        // 与 CounterAlarm 断线清理口径一致：清零当前值 + 重置内存状态）。
        _states.Clear();
    }

    /// <summary>班次切换：重置全部状态（已触发告警由下一轮采样按新班次重新判定边界，不写班次切换事件）。</summary>
    public void ResetAll() => _states.Clear();

    /// <summary>删除设备：清理该设备全部源状态。</summary>
    public void RemoveDevice(string deviceId)
    {
        var prefix = deviceId + ":";
        foreach (var key in _states.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _states.Remove(key);
    }

    /// <summary>删除单个源的状态。</summary>
    public void RemoveSource(string deviceId, string sourceId) => _states.Remove(GetKey(deviceId, sourceId));

    private static string GetKey(string deviceId, string sourceId) => $"{deviceId}:{sourceId}";

    /// <summary>数据源告警在 alarm_events 中的 AlarmId 命名规则（与 PLC 设备报警区分来源，设计稿 §4"来源区分"）。</summary>
    public static string SourceAlarmId(string sourceId) => $"src:{sourceId}";

    // ──────────── 测试访问 ────────────

    internal IReadOnlyDictionary<string, bool> GetConfirmedStatesForTest()
        => _states.ToDictionary(kv => kv.Key, kv => kv.Value.Confirmed, StringComparer.OrdinalIgnoreCase);

    internal (bool Pending, DateTime Since)? GetPendingForTest(string deviceId, string sourceId)
    {
        return _states.TryGetValue(GetKey(deviceId, sourceId), out var state)
            ? (state.PendingOutOfRange, state.PendingSince)
            : null;
    }
}