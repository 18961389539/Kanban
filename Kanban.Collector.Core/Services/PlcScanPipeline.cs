using System.Collections.Generic;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Microsoft.Extensions.Logging;
using AlarmEventType = Kanban.Collector.Core.Entities.AlarmEventType;
using AlarmLevel = Kanban.Collector.Core.Models.AlarmLevel;

namespace Kanban.Collector.Core.Services;

public readonly record struct DataSourceCycleSample(DataSourceRuntimeValue Value, DateTime SampledAt);

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
    private readonly IDataSourceReaderRegistry _dataSourceReaderRegistry;
    private readonly IAlarmNotificationChannel? _alarmNotificationChannel;
    private readonly Action<AlarmEventDto>? _onAlarmEdge;
    private readonly AppSettings _appSettings;
    private readonly ILogger _logger;
    private readonly Func<string> _shiftNameProvider;

    private readonly AlarmStateTracker _alarmTracker = new();

    /// <summary>数据源采集源告警状态机（数值越限/预期偏离 判定，随 ScanSources 驱动）。</summary>
    private readonly DataSourceAlarmTracker _dataSourceTracker;

    /// <summary>序列号事件存储（可空：未注入时 SN 采集仅读值不落库，不影响主链路）。</summary>
    private readonly ISnEventStore? _snEventStore;

    /// <summary>设备当前 Running 工单 Id 解析（可空：测试场景/未注入时不关联工单）。</summary>
    private readonly Func<string, int?>? _runningWorkOrderIdProvider;

    /// <summary>SN 去重键 → 最近一次已记录的 SN（防无触发源/PLC 未清缓冲导致重复记录）。</summary>
    private readonly Dictionary<string, string> _lastRecordedSnBySource = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> _cycleInt32Values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cycleBatchAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initializedCounterAlarmIds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本轮 ScanSources 成功采样的源值（键 = {deviceId}:{sourceId}），供主服务快照落盘过滤（只写本轮成功的源）。</summary>
    private readonly Dictionary<string, DataSourceCycleSample> _cycleSourceValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly PlcBatchReadPlanCache _dwordPlanCache = new();
    private bool _dwordBatchPrepared;
    private int _cycleBatchReadRequests;
    private int _cycleBatchReadSuccesses;
    private int _cycleBatchReadValues;
    private int _cycleBatchReadFallbacks;
    private readonly HashSet<string> _lastCommunicationFailureProfileIds = new(StringComparer.OrdinalIgnoreCase);

    public PlcScanPipeline(
        IDeviceAdapterResolver adapterResolver,
        IDeviceRepository deviceRepository,
        IAlarmHistoryService alarmHistory,
        AppSettings appSettings,
        Func<string> shiftNameProvider,
        ILogger logger,
        IAlarmNotificationChannel? alarmNotificationChannel = null,
        Action<AlarmEventDto>? onAlarmEdge = null,
        IDataSourceReaderRegistry? dataSourceReaderRegistry = null,
        ISnEventStore? snEventStore = null,
        Func<string, int?>? runningWorkOrderIdProvider = null)
    {
        _adapterResolver = adapterResolver;
        _deviceRepository = deviceRepository;
        _alarmHistory = alarmHistory;
        _dataSourceReaderRegistry = dataSourceReaderRegistry ?? new DataSourceReaderRegistry([new PlcDataSourceReader()]);
        _appSettings = appSettings;
        _shiftNameProvider = shiftNameProvider;
        _logger = logger;
        _alarmNotificationChannel = alarmNotificationChannel;
        _onAlarmEdge = onAlarmEdge;
        _snEventStore = snEventStore;
        _runningWorkOrderIdProvider = runningWorkOrderIdProvider;
        _dataSourceTracker = new DataSourceAlarmTracker(_alarmHistory, _alarmNotificationChannel, _onAlarmEdge, _logger);
    }

    // ──────────── 诊断指标（只读，供主类 GetDiagnosticsSnapshot 聚合） ────────────
    public int BatchReadRequests => _cycleBatchReadRequests;
    public int BatchReadSuccesses => _cycleBatchReadSuccesses;
    public int BatchReadValues => _cycleBatchReadValues;
    public int BatchReadFallbacks => _cycleBatchReadFallbacks;
    public int BatchPlanRebuilds => _dwordPlanCache.RebuildCount;
    public long BatchPlanBuildMilliseconds => _dwordPlanCache.LastBuildMilliseconds;
    public IReadOnlyCollection<string> LastCommunicationFailureProfileIds
        => _lastCommunicationFailureProfileIds.ToArray();
    public IReadOnlyList<DataSourceReaderDiagnosticsSnapshot> GetDataSourceReaderDiagnostics() =>
        _dataSourceReaderRegistry.GetDiagnosticsSnapshot();

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

    /// <summary>
    /// 构建本轮 DWord 批量读计划并执行，填充轮内缓存（同地址一轮只读一次）。
    /// </summary>
    /// <param name="cooledDownProfileIds">
    /// 故障冷却中的连接档案：跳过其块读（避免每轮为故障 PLC 付一次完整超时），
    /// 冷却结束后该档案自然重试。null/空集合 = 无跳过。
    /// </param>
    public void PrepareDWordBatchValues(IReadOnlySet<string>? cooledDownProfileIds = null)
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
            if (cooledDownProfileIds != null && cooledDownProfileIds.Contains(planGroup.Adapter.ConnectionProfileId))
                continue; // 故障冷却中：本轮跳过块读，冷却结束后自动重试
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
        $"{adapter.ConnectionProfileId}|{adapter.Brand}|{adapter.AddressCodec.CanonicalKey(address)}";

    // ──────────── 三组扫描 ────────────

    /// <summary>
    /// 遍历所有报警，读取 PLC 位状态并检测边沿（用 Alarm.Id 作为状态字典 key）。
    /// 边沿事件同步入库，防止 PLC 断线期间内存状态丢失导致事件遗漏。
    /// 返回 false 表示至少一次读取失败。
    /// </summary>
    public bool ScanAlarms()
    {
        _lastCommunicationFailureProfileIds.Clear();
        var devices = _deviceRepository.GetDevicesSnapshot();
        if (devices.Count == 0) return true;
        var allSuccessful = true;
        foreach (var group in devices.GroupBy(_adapterResolver.Resolve))
        {
            try
            {
                if (!_alarmTracker.ScanAlarms(
                        group,
                        group.Key,
                        _alarmHistory,
                        _shiftNameProvider(),
                        _logger,
                        _alarmNotificationChannel,
                        _appSettings.PlcBatchReadMaxLength,
                        _appSettings.PlcBatchReadMaxGapSlots,
                        _onAlarmEdge))
                    allSuccessful = false;
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                _lastCommunicationFailureProfileIds.Add(
                    PlcRuntimeSession.NormalizeProfileId(group.Key.ConnectionProfileId));
                throw;
            }
        }
        return allSuccessful;
    }

    /// <summary>
    /// 遍历所有缺陷，从 PLC 读取缺陷计数（直接来自 PLC，不累加）。
    /// 返回 false 表示至少一次读取失败。
    /// </summary>
    public bool ScanDefects()
    {
        _lastCommunicationFailureProfileIds.Clear();
        var allSuccessful = true;
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        foreach (var defect in device.Defects.ToList())
        {
            var addr = defect.PlcAddress;
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var adapter = _adapterResolver.Resolve(device);
            try
            {
                if (adapter.AddressCodec.Parse(addr) is not { IsValid: true, Type: PlcAddressType.DWord })
                {
                    _logger.LogWarning("缺陷 {Defect} 地址格式无效: {Address}", defect.Name, addr);
                    continue;
                }

                var result = ReadInt32Value(device, addr);
                if (result.IsSuccess)
                    defect.Count = result.Content;
                else
                    allSuccessful = false;
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                _lastCommunicationFailureProfileIds.Add(
                    PlcRuntimeSession.NormalizeProfileId(adapter.ConnectionProfileId));
                throw;
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
        _lastCommunicationFailureProfileIds.Clear();
        var configuredAlarmKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        foreach (var ca in device.CounterAlarms.ToList())
        {
            var key = $"{device.Id}:{ca.Id}";
            configuredAlarmKeys.Add(key);
            if (!ca.Enabled)
            {
                _initializedCounterAlarmIds.Remove(key);
                ca.StartTime = default;
                continue;
            }
            var addr = ca.PlcAddress;
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var adapter = _adapterResolver.Resolve(device);
            try
            {
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
                    {
                        ca.StartTime = DateTime.Now;
                        NotifyAlarm(device, ca.Id, ca.Name, AlarmLevel.Medium);
                    }
                    else if (wasTriggered && !ca.IsTriggered)
                    {
                        ca.StartTime = default;
                    }
                }
                else
                {
                    // 持续性条件：每轮都会触发，降为 Debug 避免日志泛滥。
                    _logger.LogDebug("计数报警 {Alarm} 读取失败: {Address}", ca.Name, addr);
                }
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                _lastCommunicationFailureProfileIds.Add(
                    PlcRuntimeSession.NormalizeProfileId(adapter.ConnectionProfileId));
                throw;
            }
        }

        _initializedCounterAlarmIds.RemoveWhere(key => !configuredAlarmKeys.Contains(key));
    }

    /// <summary>
    /// 扫描设备数据采集源（设计稿 §1/§3/§4，多值重构版）：
    /// - 触发判定在源级（每轮读触发寄存器一次）：配置了触发地址 → 值 == TriggerValue 时采集全部值项，
    ///   完成后向同一地址写回执值（AckValue）。未配置触发地址 → 每轮无条件采集全部值项（定时）。
    /// - 值项逐个读取（各自 PlcAddress），读取成功后驱动 DataSourceAlarmTracker 按值项做越限/偏离判定。
    /// - 触发命中但值项全部读取失败时不写回执（数据未捕获，PLC 端按超时重发）。
    /// 采集地址与触发地址均参与 DWord 批量读（见 DWordAddressBatchCollector），不增加额外轮询开销。
    /// </summary>
    public void ScanSources()
    {
        _lastCommunicationFailureProfileIds.Clear();
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        foreach (var source in device.Sources.ToList())
        {
            if (!source.Enabled) continue;
            var adapter = _adapterResolver.Resolve(device);
            try
            {
                var reader = _dataSourceReaderRegistry.Resolve(adapter, source);
                var readerContext = new DataSourceReaderContext(
                    adapter,
                    address => DataSourceReaderResultMapper.FromPlc(ReadInt32Value(device, address)));

            // 触发判定（源级一次）
            if (!string.IsNullOrWhiteSpace(source.TriggerAddress))
            {
                var triggerAddressValidation = reader.ValidateTriggerAddress(readerContext, source.TriggerAddress);
                if (!triggerAddressValidation.IsSuccess)
                {
                    _logger.LogWarning("数据源 {Source} 触发地址无效: {Address}; {Message}",
                        source.Name, source.TriggerAddress, triggerAddressValidation.Message);
                    continue;
                }
                var trigger = reader.ReadTrigger(readerContext, source.TriggerAddress);
                if (!trigger.IsSuccess)
                {
                    _logger.LogDebug("数据源 {Source} 触发寄存器读取失败: {Address}; {ErrorKind}; {Message}",
                        source.Name, source.TriggerAddress, trigger.ErrorKind, trigger.Message);
                    continue;
                }
                if (trigger.Content != source.TriggerValue) continue; // 未触发（含回执态）
            }

            // 采集全部值项（各自独立读取，失败不影响其它值项）
            var anyValueRead = false;
            // SN 事件采集：Type=SN 的源在触发采集后记录序列号事件（值项约定见 SnEventConventions）
            var isSnSource = string.Equals(source.Type, SnEventConventions.SourceType, StringComparison.OrdinalIgnoreCase);
            string? snValue = null;
            int? snResult = null;
            foreach (var value in source.Values.ToList())
            {
                if (!value.Enabled) continue; // 值项独立启用开关（修复 2026-08-17）
                if (string.IsNullOrWhiteSpace(value.PlcAddress)) continue;
                var valueAddressValidation = reader.ValidateValueAddress(readerContext, value);
                if (!valueAddressValidation.IsSuccess)
                {
                    _logger.LogWarning("数据源 {Source} 值项 {Value} 地址无效: {Address}; {Message}",
                        source.Name, value.Name, value.PlcAddress, valueAddressValidation.Message);
                    continue;
                }

                var result = reader.ReadValue(readerContext, value);
                var runtimeValue = result.Content;
                if (!result.IsSuccess || !runtimeValue.IsValid)
                {
                    // 保留最后一次成功值供诊断，但显式标记本轮无效；失败读取不进入告警状态机。
                    if (runtimeValue.Type != value.DataType)
                        runtimeValue = new DataSourceRuntimeValue(value.DataType, IsValid: false);
                    value.SetRuntimeValue(runtimeValue);
                    _logger.LogDebug("数据源 {Source} 值项 {Value} 采集读取失败: {Address}; {ErrorKind}; {Message}",
                        source.Name, value.Name, value.PlcAddress, result.ErrorKind, result.Message);
                    continue;
                }

                var sampledAt = DateTime.Now;
                _dataSourceTracker.Observe(device, source, value, runtimeValue, _shiftNameProvider(), sampledAt);
                _cycleSourceValues[$"{device.Id}:{source.Id}:{value.Id}"] = new DataSourceCycleSample(runtimeValue, sampledAt);
                if (isSnSource)
                {
                    if (string.IsNullOrWhiteSpace(snValue) && value.DataType == DataSourceValueType.String)
                        snValue = runtimeValue.StringValue;
                    else if (!snResult.HasValue && value.DataType == DataSourceValueType.Int32 && IsSnResultValueItem(value))
                        snResult = runtimeValue.Int32Value;
                }
                anyValueRead = true;
            }

            // SN 事件落库：触发采集命中且读到 SN 时记录；同源同 SN 去重防重复（PLC 未清缓冲兜底）。
            if (isSnSource && _snEventStore != null && source.HasTrigger && !string.IsNullOrWhiteSpace(snValue))
            {
                var snDedupeKey = $"{device.Id}:{source.Id}";
                var sn = snValue.Trim();
                if (!_lastRecordedSnBySource.TryGetValue(snDedupeKey, out var lastRecorded)
                    || !string.Equals(lastRecorded, sn, StringComparison.Ordinal))
                {
                    _lastRecordedSnBySource[snDedupeKey] = sn;
                    _snEventStore.Append(new SnEventRecord
                    {
                        Sn = sn,
                        DeviceId = device.Id,
                        DeviceName = device.Name,
                        WorkOrderId = _runningWorkOrderIdProvider?.Invoke(device.Id),
                        ShiftName = _shiftNameProvider(),
                        Result = snResult.HasValue && snResult.Value != 0 ? 1 : 0,
                        Source = SnEventConventions.SourceName,
                        SourceId = source.Id,
                        Timestamp = DateTime.Now,
                    });
                }
            }

            // 完成后回执：向同一触发地址写回执值（下一轮读到回执值不再触发，等 PLC 再次置位）。
            // 写入失败仅记日志：下一轮会再次读到触发值重试（同命令至多每轮一次）。
            // 值项全部读取失败时不写回执（数据未捕获，PLC 端超时重发，避免假确认）。
                if (source.HasTrigger && anyValueRead)
                {
                    var ack = reader.WriteAcknowledgement(readerContext, source.TriggerAddress, source.AckValue);
                    if (!ack.IsSuccess)
                    {
                        _logger.LogWarning("数据源 {Source} 回执写入失败（触发地址 {Address}）: {Message}",
                            source.Name, source.TriggerAddress, ack.Message);
                    }
                }
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                _lastCommunicationFailureProfileIds.Add(
                    PlcRuntimeSession.NormalizeProfileId(adapter.ConnectionProfileId));
                throw;
            }
        }
    }

    /// <summary>SN 结果值项识别：名称含 "Result"（或中文"结果"）的 Int32 值项 = 判定结果（0=OK，非 0=NG）。</summary>
    private static bool IsSnResultValueItem(DataSourceValue value)
        => value.Name.Contains("result", StringComparison.OrdinalIgnoreCase)
            || value.Name.Contains("结果", StringComparison.Ordinal);

    private static bool IsCommunicationException(Exception exception)
        => exception is IOException
            or System.Net.Sockets.SocketException
            or ObjectDisposedException
            or TimeoutException
            or System.Net.WebException;

    /// <summary>本轮 ScanSources 成功采样的源值（键 = {deviceId}:{sourceId}:{valueId}）。</summary>
    public IReadOnlyDictionary<string, int> GetCycleSourceValues() =>
        _cycleSourceValues.ToDictionary(pair => pair.Key, pair => pair.Value.Value.Int32Value, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, DataSourceRuntimeValue> GetCycleSourceRuntimeValues() =>
        _cycleSourceValues.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, DataSourceCycleSample> GetCycleSourceSamples() => _cycleSourceValues;

    /// <summary>
    /// 清空自上次落盘以来累积的源采样值（由主服务在快照落盘后调用）。
    /// 触发采集是稀疏事件（25~45s 一次），若按轮清空会赶不上 5s 落盘节拍；
    /// 因此采用「自上次落盘以来最新一次成功采样值」语义，落盘后清空。
    /// </summary>
    public void ClearCycleSourceValues() => _cycleSourceValues.Clear();

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
            // 数据源：清零值项 CurrentValue + 重置告警状态（重连后由下一轮真实采样重新判定，不写恢复事件）
            foreach (var source in device.Sources.ToList())
            {
                foreach (var value in source.Values.ToList())
                    value.CurrentValue = 0;
            }
            _dataSourceTracker.RecoverAllOnDisconnect(shiftName);
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
    public void ResetAll()
    {
        _alarmTracker.ResetAll();
        _dataSourceTracker.ResetAll();
    }

    /// <summary>清理指定设备的报警边沿状态与报警时间戳（设备删除/单设备清零）。</summary>
    public void RemoveDeviceAlarms(Device device)
    {
        _alarmTracker.RemoveDeviceAlarms(device);
        _dataSourceTracker.RemoveDevice(device.Id);
    }

    /// <summary>移除单个报警的边沿状态（设备管理删除报警后调用，防内存泄漏）。</summary>
    public void RemoveAlarmState(string alarmId) => _alarmTracker.RemoveAlarmState(alarmId);

    /// <summary>移除单个数据源的告警状态（设备管理删除数据源后调用，防内存泄漏）。</summary>
    public void RemoveDataSourceState(string deviceId, string sourceId)
    {
        _dataSourceTracker.RemoveSource(deviceId, sourceId);
        _lastRecordedSnBySource.Remove($"{deviceId}:{sourceId}");
    }

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
