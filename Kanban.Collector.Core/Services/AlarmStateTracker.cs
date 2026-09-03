using System.Collections.Generic;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 报警状态追踪器：拥有 <see cref="_prevAlarmStates"/> 与 <see cref="_shiftChangeFailedAlarms"/>
/// 两个并发字典及其专用锁。职责：
/// - <see cref="ScanAlarms"/>：按连续地址块批量读取 PLC 位状态，再检测上升沿/下降沿，边沿事件同步入库
/// - <see cref="LogShiftChangeForActiveAlarms"/>：班次切换前为活跃报警写 EventType=3 事件
/// - <see cref="RemoveAlarmState"/>/<see cref="RemoveDeviceAlarms"/>：设备/报警删除时清理内存状态
/// - <see cref="ResetAll"/>：班次切换时清空全部报警状态字典
/// 加锁保护与 PLC 轮询线程及 UI 删除操作的并发访问。
/// </summary>
internal sealed class AlarmStateTracker
{
    /// <summary>记录每个报警地址上一次读取的 bool 状态，用于检测上升沿/下降沿。</summary>
    private readonly Dictionary<string, bool> _prevAlarmStates = new();

    /// <summary>
    /// 活跃状态快照服务（ActiveAlarmStates 表）：触发边沿 Upsert、恢复边沿删除、
    /// 重启/重连状态重建（取代旧的"从历史事件推断 + 重建窗口"补丁）。
    /// </summary>
    private readonly IActiveAlarmStateService? _activeState;
    private readonly PlcBatchReadPlanCache _batchPlanCache = new();

    /// <summary>
    /// 保护 _prevAlarmStates 并发访问的锁对象。
    /// 轮询线程读写，UI 线程（RemoveDeviceState/RemoveAlarmState）也会读写，
    /// 必须加锁避免 Dictionary/HashSet 并发访问导致 InvalidOperationException 或数据错乱。
    /// </summary>
    private readonly object _lock = new();

    public AlarmStateTracker(IActiveAlarmStateService? activeState = null)
    {
        _activeState = activeState;
    }

    /// <summary>
    /// 遍历所有报警，读取 PLC 位状态并检测边沿（用 Alarm.Id 作为状态字典 key）。
    /// 边沿事件同步入库，防止 PLC 断线期间内存状态丢失导致事件遗漏。
    /// 返回 false 表示至少一次读取失败。
    /// </summary>
    internal bool ScanAlarms(
        IEnumerable<Device> devices,
        IDeviceAdapter adapter,
        IAlarmHistoryService historyService,
        string shiftName,
        ILogger logger,
        IAlarmNotificationChannel? notificationChannel = null,
        int maxBatchReadLength = 64,
        int maxGapSlots = 1,
        Action<AlarmEventDto>? onAlarmEdge = null)
    {
        var deviceList = devices.ToList();
        var batchValues = PrepareBatchValues(deviceList, adapter, logger, maxBatchReadLength, maxGapSlots);
        var allSuccessful = true;
        // 设备级活跃状态缓存：本进程首轮扫描（重启/重连后 _prevAlarmStates 为空）需要重建 prevState，
        // 每设备只查一次状态表，避免逐条 AlarmId 查询。
        var activeStateCache = LoadActiveStateCache(deviceList);
        foreach (var device in deviceList)
        foreach (var alarm in device.Alarms.ToList())
        {
            var addr = alarm.PlcAddress;
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var parsedAddress = adapter.AddressCodec.Parse(addr);
            if (parsedAddress is not { IsValid: true, Type: PlcAddressType.MBit })
            {
                logger.LogWarning("报警 {Alarm} 地址格式无效（需要M位地址）: {Address}", alarm.Name, addr);
                continue;
            }

            bool currentState;
            if (!batchValues.TryGetValue(GetBatchCacheKey(adapter, parsedAddress.Original), out currentState))
            {
                var result = adapter.ReadBool(addr);
                if (!result.IsSuccess)
                {
                    allSuccessful = false;
                    continue;
                }
                currentState = result.Content;
            }

            // 锁内仅做"读取 prev 状态 + 决定动作"，DB 查询/写入移到锁外避免持锁阻塞 UI 删除操作。
            // DB 完成后再加锁更新 _prevAlarmStates，期间若 UI 删除了该报警，状态更新会被
            // RemoveAlarmState 的后续逻辑安全处理（key 已删除则无需更新）。
            bool prevState;
            bool needRebuild;

            lock (_lock)
            {
                needRebuild = !_prevAlarmStates.TryGetValue(alarm.Id, out prevState);
            }

            // ── 锁外：状态重建（ActiveAlarmStates 快照表） ──
            if (needRebuild)
            {
                // 从状态快照表恢复：行存在且 IsActive → 仍触发并延续 TriggeredAt（持续时长不虚高）；
                // 无行/已恢复 → 未触发（当前 ON 走正常触发沿）。取代旧的"历史事件推断 + 重建窗口"补丁。
                prevState = false;
                if (activeStateCache != null
                    && activeStateCache.TryGetValue(device.Id, out var rows)
                    && rows.TryGetValue(alarm.Id, out var row) && row.IsActive)
                {
                    prevState = true;
                    alarm.StartTime = row.TriggeredAt;
                    alarm.EndTime = default;
                }
            }

            // ── 锁外：边沿检测 + DB 写入 ──
            var edgeType = (prevState, currentState) switch
            {
                (false, true) => (AlarmEventType?)AlarmEventType.Triggered,
                (true, false) => (AlarmEventType?)AlarmEventType.Recovered,
                _ => null
            };

            if (edgeType.HasValue)
            {
                var eventTime = System.DateTime.Now;
                if (edgeType == AlarmEventType.Triggered)
                {
                    alarm.StartTime = eventTime;
                    alarm.EndTime = default;
                }
                else
                {
                    alarm.EndTime = eventTime;
                }

                // DB 写入在锁外执行，避免持锁阻塞 UI 线程的 RemoveDeviceAlarms/RemoveAlarmState。
                // 写入失败时不更新 _prevAlarmStates，下一轮重新尝试，避免 DB 无记录但内存标记已变更。
                var writeOk = historyService.LogAlarmEvent(
                    device.Id, device.Name, alarm.Id, alarm.Name, addr, edgeType.Value, eventTime, shiftName);
                if (!writeOk)
                {
                    logger.LogWarning("报警 {Alarm} {Edge} 事件写入失败，下一轮重试",
                        alarm.Name, edgeType == AlarmEventType.Triggered ? "触发" : "恢复");
                    continue;
                }

                logger.LogInformation("报警 {Alarm} {Edge}（设备={Device}）",
                    alarm.Name, edgeType == AlarmEventType.Triggered ? "触发" : "恢复", device.Name);

                // 同步维护活跃状态快照表：触发 Upsert（IsActive=true）、恢复删除行。
                // 写失败无碍：Upsert/删除均幂等，下轮边沿或重建自动覆盖。
                if (edgeType == AlarmEventType.Triggered)
                {
                    _activeState?.UpsertActive(device.Id, device.Name, alarm.Id, alarm.Name,
                        addr, isActive: true, triggeredAt: eventTime, shiftName);
                }
                else
                {
                    _activeState?.RemoveByAlarm(device.Id, alarm.Id);
                }

                // 边沿事件广播：成功落库后向 EventBroadcaster 推送（修复 Remote 事件流缺口——
                // 此前 AlarmStateTracker 仅写 DB + 报警铃，从未喂 EventBroadcaster）。
                // 包 try/catch：广播失败不能破坏采集循环。
                if (onAlarmEdge != null)
                {
                    try
                    {
                        onAlarmEdge(new AlarmEventDto
                        {
                            DeviceId = device.Id,
                            DeviceName = device.Name,
                            AlarmId = alarm.Id,
                            AlarmName = alarm.Name,
                            PlcAddress = addr,
                            EventType = (Kanban.Contracts.Enums.AlarmEventType)(int)edgeType.Value,
                            Level = (Kanban.Contracts.Enums.AlarmLevel)(int)alarm.Level,
                            EventTime = eventTime,
                            ShiftName = shiftName,
                        });
                    }
                    catch (System.Exception ex)
                    {
                        logger.LogWarning(ex, "报警边沿事件广播失败（设备={Device} 报警={Alarm}）", device.Name, alarm.Name);
                    }
                }

                if (edgeType == AlarmEventType.Triggered)
                {
                    try
                    {
                        notificationChannel?.Enqueue(new AlarmNotification(
                            device.Id, device.Name, alarm.Id, alarm.Name, alarm.Level, eventTime));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "报警通知通道执行失败：设备={Device} 报警={Alarm}", device.Name, alarm.Name);
                    }
                }
            }

            // ── 锁内：更新 _prevAlarmStates ──
            lock (_lock)
            {
                // 仅当 key 仍存在时更新；若 UI 在锁外期间删除了该报警，跳过更新避免复活已删除的 key。
                // needRebuild 场景下 key 不存在，需要新增。
                if (_prevAlarmStates.ContainsKey(alarm.Id) || needRebuild)
                {
                    _prevAlarmStates[alarm.Id] = currentState;
                }
            }
        }
        return allSuccessful;
    }

    internal bool ScanAlarms(
        IEnumerable<Device> devices,
        Func<Device, IDeviceAdapter> adapterResolver,
        IAlarmHistoryService historyService,
        string shiftName,
        ILogger logger,
        IAlarmNotificationChannel? notificationChannel = null,
        int maxBatchReadLength = 64,
        int maxGapSlots = 1,
        Action<AlarmEventDto>? onAlarmEdge = null)
    {
        ArgumentNullException.ThrowIfNull(adapterResolver);
        var allSuccessful = true;
        foreach (var group in devices.ToList().GroupBy(adapterResolver))
        {
            if (!ScanAlarms(group, group.Key, historyService, shiftName, logger,
                    notificationChannel, maxBatchReadLength, maxGapSlots, onAlarmEdge))
                allSuccessful = false;
        }
        return allSuccessful;
    }

    private Dictionary<string, bool> PrepareBatchValues(
        IReadOnlyList<Device> devices,
        IDeviceAdapter adapter,
        ILogger logger,
        int maxBatchReadLength,
        int maxGapSlots)
    {
        var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var addresses = devices
            .SelectMany(device => device.Alarms.ToList())
            .Select(alarm => adapter.AddressCodec.Parse(alarm.PlcAddress))
            .Where(parsed => parsed is { IsValid: true, Type: PlcAddressType.MBit })
            .Select(parsed => parsed.Original)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var capabilities = adapter.BatchReadCapabilities;
        if (addresses.Count == 0 || !capabilities.SupportsBool || capabilities.MaxBoolLength == 0)
            return values;

        var configuredMax = Math.Clamp(maxBatchReadLength, 1, ushort.MaxValue);
        var batchLength = (ushort)Math.Min(configuredMax, capabilities.MaxBoolLength);
        var signature = string.Join("|",
            adapter.Brand,
            adapter.AddressCodec.GetType().FullName,
            batchLength,
            maxGapSlots,
            string.Join(",", addresses.OrderBy(address => address, StringComparer.OrdinalIgnoreCase)));
        var plan = _batchPlanCache.GetOrBuild(signature, () =>
        [
            new PlcBatchReadPlanGroup(
                adapter,
                PlcBatchReadPlanner.Plan(
                    addresses,
                    PlcAddressType.MBit,
                    batchLength,
                    capabilities.BoolAddressStride,
                    adapter.AddressCodec,
                    maxGapSlots),
                addresses.ToHashSet(StringComparer.OrdinalIgnoreCase)),
        ]);

        foreach (var block in plan.SelectMany(group => group.Blocks))
        {
            PlcOperationResult<bool[]> result;
            try
            {
                result = adapter.ReadBoolBatch(block.StartAddress, block.Length);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "批量读取报警地址失败（地址={Address}, 长度={Length}），将回退单地址读取",
                    block.StartAddress, block.Length);
                continue;
            }

            if (!result.IsSuccess || result.Content.Length < block.Length)
            {
                logger.LogWarning("批量读取报警地址失败（地址={Address}, 长度={Length}），将回退单地址读取：{Message}",
                    block.StartAddress, block.Length, result.Message);
                continue;
            }

            for (var index = 0; index < block.Length; index++)
            {
                var address = adapter.AddressCodec.Add(block.StartAddress, index);
                values[GetBatchCacheKey(adapter, address)] = result.Content[index];
            }
        }

        return values;
    }

    private static string GetBatchCacheKey(IDeviceAdapter adapter, string address) =>
        $"{adapter.Brand}|{adapter.AddressCodec.CanonicalKey(address)}";

    internal bool ScanAlarms(
        IEnumerable<Device> devices,
        IPlcDriver plc,
        IAlarmHistoryService historyService,
        string shiftName,
        ILogger logger,
        IAlarmNotificationChannel? notificationChannel = null,
        int maxBatchReadLength = 64,
        int maxGapSlots = 1,
        Action<AlarmEventDto>? onAlarmEdge = null)
        => ScanAlarms(devices, new PlcDeviceAdapter(plc), historyService, shiftName, logger,
            notificationChannel, maxBatchReadLength, maxGapSlots, onAlarmEdge);

    /// <summary>
    /// 同步遍历当前仍触发中的报警（_prevAlarmStates 值为 true），
    /// 记录 EventType=3"班次切换"事件到 AlarmEvents 表。
    /// 这些报警在新班次中会被当作"未触发"处理，下次 PLC 读取到 ON 时重新触发上升沿
    /// （状态表行由 PlcScanPipeline.ResetAll 按设备清除，新班次重新落表）。
    /// 必须在 ResetAll 清空 _prevAlarmStates 之前调用。
    /// </summary>
    internal void LogShiftChangeForActiveAlarms(
        IEnumerable<Device> devices,
        IAlarmHistoryService historyService,
        string shiftName,
        ILogger logger)
    {
        foreach (var device in devices.ToList())
        foreach (var alarm in device.Alarms.ToList())
        {
            bool isActive;
            lock (_lock)
            {
                if (!_prevAlarmStates.TryGetValue(alarm.Id, out isActive) || !isActive)
                    continue;
            }

            try
            {
                historyService.LogAlarmEvent(
                    device.Id, device.Name, alarm.Id, alarm.Name,
                    alarm.PlcAddress, AlarmEventType.ShiftChange, System.DateTime.Now, shiftName);
            }
            catch (System.Exception ex)
            {
                // 事件写入失败不阻断班次切换：状态表由调用方在 ResetAll 后按需清除，
                // 无需失败补偿集合（Upsert 幂等，新班次若仍触发会重新落表）。
                logger.LogError(ex, "报警 {Alarm} 班次切换事件写入异常", alarm.Name);
            }
        }
    }

    /// <summary>
    /// 为状态重建预取各设备的活跃报警状态（本进程首轮扫描使用）。
    /// 返回 null 表示未注入状态服务（跳过重建，prevState 一律 false，行为与旧"无历史"一致）。
    /// </summary>
    private Dictionary<string, Dictionary<string, ActiveAlarmStateRecord>>? LoadActiveStateCache(IReadOnlyList<Device> devices)
    {
        if (_activeState == null) return null;
        var cache = new Dictionary<string, Dictionary<string, ActiveAlarmStateRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var rows = _activeState.QueryActive(device.Id);
            if (rows.Count == 0) continue;
            cache[device.Id] = rows.ToDictionary(r => r.AlarmId, StringComparer.OrdinalIgnoreCase);
        }
        return cache;
    }

    /// <summary>清理已删除报警的残留内存状态（key 为 Alarm.Id）。</summary>
    internal void RemoveAlarmState(string alarmId)
    {
        lock (_lock)
        {
            _prevAlarmStates.Remove(alarmId);
        }
    }

    /// <summary>清理已删除设备的所有报警状态（key 为 Alarm.Id）。</summary>
    internal void RemoveDeviceAlarms(Device device)
    {
        if (device == null) return;
        var alarmIds = device.Alarms.ToList().Select(a => a.Id).ToList();
        lock (_lock)
        {
            foreach (var alarmId in alarmIds)
            {
                _prevAlarmStates.Remove(alarmId);
            }
        }
    }

    /// <summary>班次切换时清空全部报警边沿状态字典（状态表行由 PlcScanPipeline.ResetAll 按设备清除）。</summary>
    internal void ResetAll()
    {
        lock (_lock)
        {
            _prevAlarmStates.Clear();
        }
    }

    // ──────────── 测试访问助手（仅 internal，由 PlcDataAcquisitionService.TestAccess 转发） ────────────

    internal System.Collections.Generic.IReadOnlyDictionary<string, bool> GetPrevAlarmStatesSnapshot()
    {
        lock (_lock)
            return _prevAlarmStates.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    internal void SetPrevAlarmStateForTest(string alarmId, bool state)
    {
        lock (_lock)
            _prevAlarmStates[alarmId] = state;
    }

    /// <summary>
    /// 仅从 _prevAlarmStates 移除指定报警，精确模拟 ResetShift 对单条报警状态的影响
    /// （ResetShift 内 _prevAlarmStates.Clear()），便于班次切换场景的单测。
    /// </summary>
    internal void ClearPrevAlarmStateForTest(string alarmId)
    {
        lock (_lock)
            _prevAlarmStates.Remove(alarmId);
    }
}
