using System.Collections.Generic;
using Kanban.Contracts.Dtos;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;
using AlarmEventType = Kanban.Core.Entities.AlarmEventType;
using AlarmLevel = Kanban.Core.Models.AlarmLevel;

namespace Kanban.Core.Services;

/// <summary>
/// PLC 扫描子系统（从 PlcDataAcquisitionService 拆出的协作组件之一）：
/// 承担"批量读缓存 + 三组扫描"职责——
/// - DWord 批量读计划与轮内缓存（_cycleInt32Values / _dwordPlanCache）：一轮采集内同地址只读一次
/// - 报警边沿扫描（ScanAlarms，委托 AlarmStateTracker）
/// - 缺陷扫描（ScanDefects）与计数报警扫描（ScanCounterAlarms）
/// - 断线清理（ClearAlarmsOnDisconnect）
///
/// 轮询编排/产量基线（班次语义）/状态机/OEE/历史快照留在 PlcDataAcquisitionService。
/// 新增采集项（新报警类型/新传感器）优先在这里加 scanner 方法，不动轮询主循环。
/// 诊断指标（BatchRead*）经只读属性暴露给主类的 GetDiagnosticsSnapshot。
/// </summary>
public sealed class PlcScanPipeline
{
    private readonly IDeviceAdapterResolver _adapterResolver;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IAlarmHistoryService _alarmHistory;
    private readonly IAlarmNotificationChannel? _alarmNotificationChannel;
    private readonly Action<AlarmEventDto>? _onAlarmEdge;
    private readonly AppSettings _appSettings;
    private readonly ILogger _logger;
    private readonly Func<string> _shiftNameProvider;

    private readonly AlarmStateTracker _alarmTracker = new();

    private readonly Dictionary<string, int> _cycleInt32Values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cycleBatchAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initializedCounterAlarmIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlcBatchReadPlanCache _dwordPlanCache = new();
    private bool _dwordBatchPrepared;
    private int _cycleBatchReadRequests;
    private int _cycleBatchReadSuccesses;
    private int _cycleBatchReadValues;
    private int _cycleBatchReadFallbacks;

    public PlcScanPipeline(
        IDeviceAdapterResolver adapterResolver,
        IDeviceRepository deviceRepository,
        IAlarmHistoryService alarmHistory,
        AppSettings appSettings,
        Func<string> shiftNameProvider,
        ILogger logger,
        IAlarmNotificationChannel? alarmNotificationChannel = null,
        Action<AlarmEventDto>? onAlarmEdge = null)
    {
        _adapterResolver = adapterResolver;
        _deviceRepository = deviceRepository;
        _alarmHistory = alarmHistory;
        _appSettings = appSettings;
        _shiftNameProvider = shiftNameProvider;
        _logger = logger;
        _alarmNotificationChannel = alarmNotificationChannel;
        _onAlarmEdge = onAlarmEdge;
    }

    // ──────────── 诊断指标（只读，供主类 GetDiagnosticsSnapshot 聚合） ────────────
    public int BatchReadRequests => _cycleBatchReadRequests;
    public int BatchReadSuccesses => _cycleBatchReadSuccesses;
    public int BatchReadValues => _cycleBatchReadValues;
    public int BatchReadFallbacks => _cycleBatchReadFallbacks;
    public int BatchPlanRebuilds => _dwordPlanCache.RebuildCount;
    public long BatchPlanBuildMilliseconds => _dwordPlanCache.LastBuildMilliseconds;

    // ──────────── 批量读缓存 ────────────

    /// <summary>读取单个 Int32（命中轮内 DWord 批量缓存则免一次 PLC 读；未命中回退单地址读）。</summary>
    internal PlcOperationResult<int> ReadInt32Value(Models.Device device, string address)
    {
        var adapter = _adapterResolver.Resolve(device);
        var normalizedAddress = adapter.AddressCodec.Normalize(address);
        var cacheKey = GetBatchCacheKey(adapter, normalizedAddress);
        if (_dwordBatchPrepared
            && !string.IsNullOrEmpty(normalizedAddress)
            && _cycleInt32Values.TryGetValue(cacheKey, out var value))
            return PlcOperationResult<int>.Success(value);
        if (_dwordBatchPrepared
            && !string.IsNullOrEmpty(normalizedAddress)
            && _cycleBatchAddresses.Contains(cacheKey))
            _cycleBatchReadFallbacks++;
        return adapter.ReadInt32(address);
    }

    /// <summary>构建本轮 DWord 批量读计划并执行，填充轮内缓存（同地址一轮只读一次）。</summary>
    public void PrepareDWordBatchValues()
    {
        _cycleInt32Values.Clear();
        _cycleBatchAddresses.Clear();
        _dwordBatchPrepared = false;
        _cycleBatchReadRequests = 0;
        _cycleBatchReadSuccesses = 0;
        _cycleBatchReadValues = 0;
        _cycleBatchReadFallbacks = 0;
        var devices = _deviceRepository.GetDevicesSnapshot();
        var plan = GetDWordReadPlan(devices);
        foreach (var planGroup in plan)
        {
            foreach (var address in planGroup.Addresses)
            {
                _cycleBatchAddresses.Add(GetBatchCacheKey(planGroup.Adapter, address));
            }
        }

        foreach (var planGroup in plan)
        {
            foreach (var block in planGroup.Blocks)
            {
                _cycleBatchReadRequests++;
                PlcOperationResult<int[]> result;
                try { result = planGroup.Adapter.ReadInt32Batch(block.StartAddress, block.Length); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "批量读取 DWord 地址失败（地址={Address}, 长度={Length}），将回退单地址读取",
                        block.StartAddress, block.Length);
                    continue;
                }
                if (!result.IsSuccess || result.Content.Length < block.Length)
                {
                    _logger.LogWarning("批量读取 DWord 地址失败（地址={Address}, 长度={Length}），将回退单地址读取：{Message}",
                        block.StartAddress, block.Length, result.Message);
                    continue;
                }

                _cycleBatchReadSuccesses++;
                _cycleBatchReadValues += block.Length;
                for (var index = 0; index < block.Length; index++)
                {
                    var address = planGroup.Adapter.AddressCodec.Add(block.StartAddress, index);
                    _cycleInt32Values[GetBatchCacheKey(planGroup.Adapter, address)] = result.Content[index];
                }
            }
        }
        _dwordBatchPrepared = true;
    }

    private IReadOnlyList<PlcBatchReadPlanGroup> GetDWordReadPlan(IReadOnlyList<Models.Device> devices)
    {
        var candidates = new List<(IDeviceAdapter Adapter, HashSet<string> Addresses)>();
        var signatureParts = new List<string>
        {
            _appSettings.PlcBatchReadMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _appSettings.PlcBatchReadMaxGapSlots.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        foreach (var device in devices)
        {
            try
            {
                var adapter = _adapterResolver.Resolve(device);
                var addresses = DWordAddressBatchCollector.Collect(device, adapter);
                var capabilities = adapter.BatchReadCapabilities;
                signatureParts.Add(string.Join("|", device.Id, adapter.Brand, adapter.AddressCodec.GetType().FullName,
                    capabilities.SupportsInt32, capabilities.MaxInt32Length, capabilities.Int32AddressStride,
                    string.Join(",", addresses.OrderBy(address => address, StringComparer.OrdinalIgnoreCase))));
                candidates.Add((adapter, addresses));
            }
            catch (Exception ex)
            {
                signatureParts.Add($"{device.Id}|resolve-error");
                _logger.LogWarning(ex, "设备 {Device} 无法建立 DWord 批量读取计划", device.Name);
            }
        }

        var signature = string.Join(";", signatureParts);
        return _dwordPlanCache.GetOrBuild(signature, () =>
        {
            var groups = candidates
                .GroupBy(candidate => candidate.Adapter)
                .Select(group =>
                {
                    var adapter = group.Key;
                    var addresses = group.SelectMany(candidate => candidate.Addresses)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var capabilities = adapter.BatchReadCapabilities;
                    if (!capabilities.SupportsInt32 || capabilities.MaxInt32Length == 0)
                        return new PlcBatchReadPlanGroup(adapter, [], addresses);

                    var maxLength = (ushort)Math.Min(
                        Math.Clamp(_appSettings.PlcBatchReadMaxLength, 1, ushort.MaxValue),
                        capabilities.MaxInt32Length);
                    var blocks = PlcBatchReadPlanner.Plan(
                        addresses,
                        PlcAddressType.DWord,
                        maxLength,
                        capabilities.Int32AddressStride,
                        adapter.AddressCodec,
                        _appSettings.PlcBatchReadMaxGapSlots);
                    return new PlcBatchReadPlanGroup(adapter, blocks, addresses);
                })
                .ToList();
            return (IReadOnlyList<PlcBatchReadPlanGroup>)groups;
        });
    }

    private static string GetBatchCacheKey(IDeviceAdapter adapter, string address) =>
        $"{adapter.Brand}|{adapter.AddressCodec.CanonicalKey(address)}";

    // ──────────── 三组扫描 ────────────

    /// <summary>
    /// 遍历所有报警，读取 PLC 位状态并检测边沿（用 Alarm.Id 作为状态字典 key）。
    /// 边沿事件同步入库，防止 PLC 断线期间内存状态丢失导致事件遗漏。
    /// 返回 false 表示至少一次读取失败。
    /// </summary>
    public bool ScanAlarms()
    {
        var devices = _deviceRepository.GetDevicesSnapshot();
        if (devices.Count == 0) return true;
        return _alarmTracker.ScanAlarms(
            devices,
            _adapterResolver.Resolve(devices[0]), _alarmHistory, _shiftNameProvider(), _logger,
            _alarmNotificationChannel,
            _appSettings.PlcBatchReadMaxLength,
            _appSettings.PlcBatchReadMaxGapSlots,
            _onAlarmEdge);
    }

    /// <summary>
    /// 遍历所有缺陷，从 PLC 读取缺陷计数（直接来自 PLC，不累加）。
    /// 返回 false 表示至少一次读取失败。
    /// </summary>
    public bool ScanDefects()
    {
        var allSuccessful = true;
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        foreach (var defect in device.Defects.ToList())
        {
            var addr = defect.PlcAddress;
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var adapter = _adapterResolver.Resolve(device);
            if (adapter.AddressCodec.Parse(addr) is not { IsValid: true, Type: PlcAddressType.DWord })
            {
                _logger.LogWarning("缺陷 {Defect} 地址格式无效: {Address}", defect.Name, addr);
                continue;
            }

            var result = ReadInt32Value(device, addr);
            if (result.IsSuccess)
            {
                defect.Count = result.Content;
            }
            else
            {
                allSuccessful = false;
            }
        }
        return allSuccessful;
    }

    /// <summary>
    /// 遍历设备的计数报警，按 D 字地址读取 PLC 当前值并回填到 CounterAlarm.CurrentValue。
    /// 计数报警基于数值阈值判断（与 M 位报警的边沿检测不同），不写入 AlarmEvents、不计入 OEE。
    /// 读取失败不影响其他设备/报警的扫描结果。
    /// </summary>
    public void ScanCounterAlarms()
    {
        var configuredAlarmKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        foreach (var ca in device.CounterAlarms.ToList())
        {
            var key = $"{device.Id}:{ca.Id}";
            configuredAlarmKeys.Add(key);
            if (!ca.Enabled)
            {
                _initializedCounterAlarmIds.Remove(key);
                continue;
            }
            var addr = ca.PlcAddress;
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var adapter = _adapterResolver.Resolve(device);
            if (adapter.AddressCodec.Parse(addr) is not { IsValid: true, Type: PlcAddressType.DWord })
            {
                _logger.LogWarning("计数报警 {Alarm} 地址格式无效（需要D字地址）: {Address}", ca.Name, addr);
                continue;
            }

            var result = ReadInt32Value(device, addr);
            if (result.IsSuccess)
            {
                var wasTriggered = ca.IsTriggered;
                ca.CurrentValue = result.Content;   // IsTriggered 由 CurrentValue > MaxValue 自动派生
                // 首次有效采样只建立基线：应用启动时已经超阈值的报警不算新报警。
                var isFirstObservation = _initializedCounterAlarmIds.Add(key);
                if (!isFirstObservation && !wasTriggered && ca.IsTriggered)
                    NotifyAlarm(device, ca.Id, ca.Name, AlarmLevel.Medium);
            }
            else
            {
                // 持续性条件：每轮都会触发，降为 Debug 避免日志泛滥。
                _logger.LogDebug("计数报警 {Alarm} 读取失败: {Address}", ca.Name, addr);
            }
        }

        _initializedCounterAlarmIds.RemoveWhere(key => !configuredAlarmKeys.Contains(key));
    }

    /// <summary>
    /// PLC 断线时清除所有设备的活跃报警（设置 EndTime + 写入恢复事件），
    /// 并重置报警边沿检测状态与计数报警。避免断线后 UI 仍显示遗留报警。
    /// </summary>
    public void ClearAlarmsOnDisconnect()
    {
        var now = DateTime.Now;
        var shiftName = _shiftNameProvider();
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            foreach (var alarm in device.Alarms.ToList())
            {
                if (alarm.StartTime != default && alarm.EndTime == default)
                {
                    alarm.EndTime = now;
                    _alarmHistory.LogAlarmEvent(
                        device.Id, device.Name, alarm.Id, alarm.Name, alarm.PlcAddress ?? "",
                        AlarmEventType.Recovered, now, shiftName);
                }
            }
            // 计数报警：清零 CurrentValue，使 IsTriggered 计算属性返回 false
            foreach (var ca in device.CounterAlarms.ToList())
            {
                ca.CurrentValue = 0;
            }
        }
        _alarmTracker.ResetAll();
    }

    private void NotifyAlarm(Device device, string alarmId, string alarmName, AlarmLevel level)
    {
        try
        {
            _alarmNotificationChannel?.Enqueue(new AlarmNotification(
                device.Id, device.Name, alarmId, alarmName, level, DateTime.Now));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "报警通知通道执行失败：设备={Device} 报警={Alarm}", device.Name, alarmName);
        }
    }

    // ──────────── 报警状态维护（主类 ResetShift/设备删除/班次切换委托） ────────────

    /// <summary>重置全部报警边沿检测状态（班次切换/软件清零）。</summary>
    public void ResetAll() => _alarmTracker.ResetAll();

    /// <summary>清理指定设备的报警边沿状态与报警时间戳（设备删除/单设备清零）。</summary>
    public void RemoveDeviceAlarms(Device device) => _alarmTracker.RemoveDeviceAlarms(device);

    /// <summary>移除单个报警的边沿状态（设备管理删除报警后调用，防内存泄漏）。</summary>
    public void RemoveAlarmState(string alarmId) => _alarmTracker.RemoveAlarmState(alarmId);

    /// <summary>班次切换前为活跃报警写入 EventType=3 事件（须在 ResetAll 清空 _prevAlarmStates 之前调用）。</summary>
    public void LogShiftChangeForActiveAlarms(IReadOnlyList<Device> devices)
        => _alarmTracker.LogShiftChangeForActiveAlarms(devices, _alarmHistory, _shiftNameProvider(), _logger);

    // ──────────── 测试访问（供 PlcDataAcquisitionService.TestAccess 与单元测试断言内存状态） ────────────

    internal IReadOnlyDictionary<string, bool> GetPrevAlarmStatesForTest()
        => _alarmTracker.GetPrevAlarmStatesSnapshot();

    internal IReadOnlyCollection<string> GetShiftChangeFailedAlarmsForTest()
        => _alarmTracker.GetShiftChangeFailedAlarmsSnapshot();

    internal void SetPrevAlarmStateForTest(string alarmId, bool state)
        => _alarmTracker.SetPrevAlarmStateForTest(alarmId, state);

    internal void ClearPrevAlarmStateForTest(string alarmId)
        => _alarmTracker.ClearPrevAlarmStateForTest(alarmId);
}
