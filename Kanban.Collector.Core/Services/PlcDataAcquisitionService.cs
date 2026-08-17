using System.Collections.Concurrent;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Microsoft.Extensions.Logging;
using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// PLC 采集运行诊断快照。
/// </summary>
public sealed record AcquisitionDiagnosticsSnapshot
{
    public int CompletedCycles { get; init; }
    public int FailedCycles { get; init; }
    public long LastCycleMilliseconds { get; init; }
    public double AverageCycleMilliseconds { get; init; }
    public long MaxCycleMilliseconds { get; init; }
    public int LastSuccessfulDevices { get; init; }
    public int ConfiguredDevices { get; init; }
    public DateTime? LastSuccessfulAt { get; init; }
    public int SuccessfulCycles { get; init; }
    public int EstimatedReadOperations { get; init; }
    public int BatchReadRequests { get; init; }
    public int BatchReadSuccesses { get; init; }
    public int BatchReadValues { get; init; }
    public int BatchReadFallbacks { get; init; }
    public int BatchPlanRebuilds { get; init; }
    public long BatchPlanBuildMilliseconds { get; init; }
    public long DWordReadMilliseconds { get; init; }
    public long AlarmReadMilliseconds { get; init; }
    public long DefectReadMilliseconds { get; init; }
    public long CounterAlarmReadMilliseconds { get; init; }
    public long HistoryWriteMilliseconds { get; init; }
    public IReadOnlySet<string> LastSuccessfulDeviceIds { get; init; } = new HashSet<string>();
    public bool LastCycleSucceeded { get; init; }
    public int ConsecutiveFailureCycles { get; init; }
    public DateTime? LastFailureAt { get; init; }
    public string? LastFailureMessage { get; init; }
}

/// <summary>
/// PLC 数据采集服务：应用启动即开始定时轮询设备产量 + 状态 + 报警位 + 缺陷计数。
/// Start/Stop 提供手动控制，服务构造时不启动，避免 DI 解析线程执行网络连接。
/// 设备列表通过 DeviceRepository（DI 单例）访问。
/// </summary>
public partial class PlcDataAcquisitionService : ObservableObject, IPlcDataAcquisitionService
{
    private readonly IPlcDriver _plc;
    private readonly IDeviceAdapterResolver _adapterResolver;
    private readonly PlcConnectionManager _connectionManager;
    private readonly AppSettings _appSettings;
    private readonly IProductionHistoryWriter _productionWriter;
    private readonly IAlarmHistoryService _alarmHistory;
    private readonly IStatusTransitionHistoryService _statusHistory;
    private readonly DeviceRepository _deviceRepository;
    private readonly ILogger<PlcDataAcquisitionService> _logger;
    private readonly ProductionBaselineStore _baselineStore;
    private readonly WorkOrderRepository? _workOrderRepo;
    private readonly IAlarmNotificationChannel? _alarmNotificationChannel;
    private readonly Action<AlarmEventDto>? _onAlarmEdge;
    private readonly Action<StatusEventDto>? _onStatusEdge;
    private readonly DefectHistoryStore? _defectHistoryStore;
    private readonly DataSourceSnapshotStore? _dataSourceSnapshotStore;
    private readonly AcquisitionDiagnosticsStore _diagnostics = new();

    /// <summary>缺陷快照降频：记录上次落库的（Count, ShiftName），无变化不写（键 = 设备Id|缺陷Id）。</summary>
    private readonly Dictionary<string, (int Count, string ShiftName)> _lastDefectSnapshot = new();
    private CancellationTokenSource? _cts;
    /// <summary>
    /// 轮询循环的 Task 引用，用于 StopAsync 中等待循环真正退出，确保后续保存数据时采集线程已停止。
    /// </summary>
    private Task? _pollingTask;

    // ──────────── 拆分出的协作组件（构造时内部创建，不暴露 DI） ────────────
    // 每个组件拥有自己的状态字典与锁，职责独立。本类保留 facade API 委托调用，
    // 维持原有 internal 接口不变（测试与历史调用方零改动）。
    // PlcScanPipeline：批量读缓存 + 报警/缺陷/计数报警三组扫描（见 PlcScanPipeline.cs）
    private readonly PlcScanPipeline _scanPipeline;
    private readonly DeviceStatusTracker _statusTracker = new();
    private readonly BaselineResetCoordinator _baselineCoordinator = new();
    private readonly ShiftContext _shiftContext = new();

    /// <summary>
    /// 保护 Start/Stop 状态变更的锁，避免并发 Start 生成两个轮询任务。
    /// 仅锁 IsRunning 检查与 _cts/_pollingTask 赋值的临界区，await 在锁外执行。
    /// </summary>
    private readonly object _startStopLock = new();

    /// <summary>
    /// 保护 ResetShift/ResetDeviceProduction 多步重置序列的锁。
    /// 两者互斥，避免 UI 线程单设备清零与轮询线程班次重置并发导致状态字典半重置。
    /// 不与轮询循环主体互斥（ResetShift 由轮询线程 DetectShiftChange 调用，同线程无并发）。
    /// </summary>
    private readonly object _resetLock = new();

    /// <summary>
    /// 采集循环是否正在运行
    /// </summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>
    /// 测量两次采集之间的真实时间间隔（包含 PLC 读取耗时 + Task.Delay），
    /// 避免只用 PollingIntervalMs 累计 OEE 时间导致偏小。
    /// </summary>
    private readonly Stopwatch _pollingStopwatch = new();

    /// <summary>
    /// 上一轮采集周期结束时的 PLC 连接状态，用于检测"断线→重连"边沿
    /// </summary>
    private bool _wasConnectedLastCycle;

    /// <summary>
    /// 标记首次启动后是否已完成 OEE 时间（RunTime/AlarmTime/PausedTime）从历史重建（仅执行一次）
    /// </summary>
    private bool _oeeTimeRebuilt;

    private int _scanCount;

    /// <summary>
    /// 获取采集服务当前诊断快照。快照只读且线程安全，不触发 UI 属性通知，
    /// 由运行监控页面在 UI 线程定时读取，避免后台采集线程直接更新 WPF 绑定属性。
    /// </summary>
    public AcquisitionDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        return _diagnostics.Snapshot();
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    /// <summary>报警边沿事件（成功落库后触发）。订阅方：Collector 的 EventBroadcaster.PublishAlarmEvent。</summary>
    public event Action<AlarmEventDto>? AlarmEdgeDetected;

    /// <summary>状态转换边沿事件（成功落库后触发，含离线转换）。订阅方：EventBroadcaster.PublishStatusEvent。</summary>
    public event Action<StatusEventDto>? StatusEdgeDetected;

    public PlcDataAcquisitionService(
        IPlcDriver plc,
        PlcConnectionManager connectionManager,
        AppSettings appSettings,
        IProductionHistoryWriter productionWriter,
        IAlarmHistoryService alarmHistory,
        IStatusTransitionHistoryService statusHistory,
        DeviceRepository deviceRepository,
        ProductionBaselineStore baselineStore,
        ILogger<PlcDataAcquisitionService> logger,
        IDeviceAdapterResolver? adapterResolver = null,
        WorkOrderRepository? workOrderRepo = null,
        IAlarmNotificationChannel? alarmNotificationChannel = null,
        DefectHistoryStore? defectHistoryStore = null,
        Action<AlarmEventDto>? onAlarmEdge = null,
        Action<StatusEventDto>? onStatusEdge = null,
        DataSourceSnapshotStore? dataSourceSnapshotStore = null)
    {
        _plc = plc;
        _connectionManager = connectionManager;
        _appSettings = appSettings;
        _productionWriter = productionWriter;
        _alarmHistory = alarmHistory;
        _statusHistory = statusHistory;
        _deviceRepository = deviceRepository;
        _baselineStore = baselineStore;
        _logger = logger;
        _adapterResolver = adapterResolver ?? new DeviceAdapterResolver([new PlcDeviceAdapter(plc)]);
        _workOrderRepo = workOrderRepo;
        _alarmNotificationChannel = alarmNotificationChannel;
        _defectHistoryStore = defectHistoryStore;
        _onAlarmEdge = onAlarmEdge;
        _onStatusEdge = onStatusEdge;
        _dataSourceSnapshotStore = dataSourceSnapshotStore;
        // 扫描子系统：批量读缓存 + 报警/缺陷/计数报警扫描（班次名经委托取当前值，避免组件间循环依赖）
        // onAlarmEdge 把内部 tracker 回调桥接到本服务的 AlarmEdgeDetected 事件（供 Collector 订阅）。
        _scanPipeline = new PlcScanPipeline(
            _adapterResolver, _deviceRepository, _alarmHistory, _appSettings,
            () => GetCurrentShiftName(), _logger, _alarmNotificationChannel,
            dto => AlarmEdgeDetected?.Invoke(dto));
    }

    public PlcDataAcquisitionService(
        IPlcDriver plc,
        PlcConnectionManager connectionManager,
        AppSettings appSettings,
        IHistoryService historyService,
        DeviceRepository deviceRepository,
        ProductionBaselineStore baselineStore,
        ILogger<PlcDataAcquisitionService> logger,
        IDeviceAdapterResolver? adapterResolver = null,
        WorkOrderRepository? workOrderRepo = null,
        IAlarmNotificationChannel? alarmNotificationChannel = null,
        DefectHistoryStore? defectHistoryStore = null,
        DataSourceSnapshotStore? dataSourceSnapshotStore = null)
        : this(plc, connectionManager, appSettings, historyService, historyService, historyService,
            deviceRepository, baselineStore, logger, adapterResolver, workOrderRepo, alarmNotificationChannel, defectHistoryStore,
            onAlarmEdge: null, onStatusEdge: null, dataSourceSnapshotStore: dataSourceSnapshotStore)
    {
    }

    /// <summary>
    /// 启动数据采集循环。线程安全：并发调用不会生成多个轮询任务。
    /// </summary>
    public void Start()
    {
        lock (_startStopLock)
        {
            if (IsRunning) return;
            IsRunning = true;
            var cts = new CancellationTokenSource();
            _cts = cts;
            // P0-5：注册未观察异常处理器，避免轮询任务抛异常时在 GC 时刷错误日志
            _pollingTask = Task.Run(() => PollingLoopAsync(cts.Token), cts.Token);
            _pollingTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception, "轮询任务未观察异常");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    /// <summary>
    /// 停止数据采集循环（异步）：标记离线 + Cancel + 等待轮询循环退出。
    /// 等待循环退出确保后续保存设备/历史数据时采集线程已停止，避免并发修改 Runtime 状态、入队 HistoryService。
    /// </summary>
    public async Task StopAsync()
    {
        Task? pollingTask;
        CancellationTokenSource? cts;
        lock (_startStopLock)
        {
            if (!IsRunning) return;
            IsRunning = false;
            pollingTask = _pollingTask;
            cts = _cts;
            _cts = null;
            _pollingTask = null;
        }
        // 停机前将所有处于真实状态的设备标记为离线，
        // 避免停机时段被算进上一状态（如运行）导致重启后 OEE 历史虚高/失真。
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
            LogOfflineTransition(device);
        cts?.Cancel();
        // 等待轮询循环真正退出。加 15s 超时保护：若采集线程卡在同步 PLC IO（ReadInt32 等），
        // CancellationToken 无法中断同步调用，避免应用永久挂起无法退出。
        if (pollingTask != null)
        {
            try { await Task.WhenAny(pollingTask, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false); }
            catch { /* Cancel 导致的 OperationCanceledException 等忽略 */ }

            // P0-5：超时后若任务仍未完成，不要立即 Dispose cts（轮询线程可能仍在用 ct）。
            // 让 cts 与 pollingTask 由 GC 回收，避免 ObjectDisposedException。
            // ContinueWith(OnlyOnFaulted) 仍会观察后续异常。
            if (!pollingTask.IsCompleted)
            {
                _logger.LogWarning("停止采集服务超时（15s），轮询任务可能仍在运行，将交由 GC 回收");
            }
        }
        // 仅在任务确实已完成时安全释放 cts
        if (pollingTask?.IsCompleted == true)
        {
            try { cts?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 后台轮询主循环：驱动连接管理器 + 数据采集
    /// </summary>
    private async Task PollingLoopAsync(CancellationToken ct)
    {
        _pollingStopwatch.Start();
        _diagnostics.Start();
        var loopCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                loopCount++;
                // 诊断：每 10 轮记录一次循环状态（Debug 级别，避免污染 INF 日志）
                if (loopCount % 10 == 0)
                {
                    _logger.LogDebug("采集循环 #{Loop} IsConnected={Connected} WasConnectedLastCycle={WasConnected}",
                        loopCount, _connectionManager.IsConnected, _wasConnectedLastCycle);
                }

                // 班次切换检测：在每轮采集前判断当前时刻所属班次，
                // 若与上一班次不同则触发 ResetShift()，让 OEE/产量/报警时间戳按班次重新累计
                DetectShiftChange();

                _connectionManager.EnsureConnected();

                // PLC 重连后处理推迟的产量清零（班次切换时 PLC 未连接的场景）。
                // 仅在"上一轮未连接 + 当前已连接"的边沿触发，避免每轮反复尝试（浪费 IO）。
                List<string>? pendingDevices = null;
                if (_connectionManager.IsConnected && !_wasConnectedLastCycle)
                {
                    pendingDevices = _baselineCoordinator.DrainPendingReconnect();
                }
                if (pendingDevices != null && pendingDevices.Count > 0)
                {
                    _logger.LogInformation("PLC 重连后触发 {Count} 台设备的推迟产量清零", pendingDevices.Count);
                    var devicesSnapshot = _deviceRepository.GetDevicesSnapshot();
                    foreach (var deviceId in pendingDevices)
                    {
                        var device = devicesSnapshot.FirstOrDefault(d => d.Id == deviceId);
                        if (device != null)
                        {
                            TriggerPlcProductionReset(device);
                            _baselineCoordinator.ScheduleClear(deviceId);
                        }
                    }
                }
                _wasConnectedLastCycle = _connectionManager.IsConnected;

                // 延迟清空基线到期清理：PLC 清零信号发出1秒后结束窗口期。
                // 软件基线已在 ResetShift 时经 ProductionBaselineStore.ClearAll 清空，
                // 此处仅清理到期的设备窗口标记，使下一轮读取以当前 PLC 值重建基线。
                _baselineCoordinator.ExpireClears(DateTime.Now, _logger);

                if (_connectionManager.IsConnected)
                {
                    var dwordReadStopwatch = Stopwatch.StartNew();
                    var successDevices = RefreshDeviceData(out var noDevicesToRead);
                    var dwordReadMilliseconds = dwordReadStopwatch.ElapsedMilliseconds;
                    // 使用 Stopwatch 测量的真实间隔累计 OEE 时间（包含 PLC 读取耗时 + 上次 Delay）
                    // Stop 重置再 Start，获取自上次累计以来的真实流逝时间
                    _pollingStopwatch.Stop();
                    var elapsedSeconds = _pollingStopwatch.Elapsed.TotalSeconds;
                    _pollingStopwatch.Restart();

                    // 诊断：记录每轮采集结果（Debug 级别，避免污染 INF 日志）
                    if (loopCount % 10 == 0)
                    {
                        _logger.LogDebug("采集 #{Loop} success={Success} noDevices={NoDevices} elapsed={Elapsed:F2}s",
                            loopCount, successDevices.Count, noDevicesToRead, elapsedSeconds);
                    }

                    AccumulateOeeTime(elapsedSeconds, successDevices);
                    // 报警/缺陷/计数报警扫描：各自独立 try-catch，单类扫描失败不阻断其他扫描和历史快照写入。
                    // 通信异常（SocketException/ObjectDisposedException 等）经 HslPlcDriver try-catch 已转成
                    // PlcOperationResult.Fail，理论上不会逃逸到 TryScan；但为防御其他 IPlcDriver 实现，
                    // TryScan 仍检测已知通信异常类型并触发 MarkDisconnected(ScanException)。
                    // 业务异常（NullReference/集合并发修改等）只记 LogError，不影响 PLC 连接状态。
                    // 返回值保留（ScanAlarms/ScanDefects 返回 false 表示至少一次读取失败），便于运行时感知。
                    var alarmReadStopwatch = Stopwatch.StartNew();
                    _ = TryScan(() => _scanPipeline.ScanAlarms(), nameof(PlcScanPipeline.ScanAlarms));
                    var alarmReadMilliseconds = alarmReadStopwatch.ElapsedMilliseconds;

                    var defectReadStopwatch = Stopwatch.StartNew();
                    _ = TryScan(() => _scanPipeline.ScanDefects(), nameof(PlcScanPipeline.ScanDefects));
                    var defectReadMilliseconds = defectReadStopwatch.ElapsedMilliseconds;

                    var counterAlarmReadStopwatch = Stopwatch.StartNew();
                    TryScan(_scanPipeline.ScanCounterAlarms, nameof(PlcScanPipeline.ScanCounterAlarms));
                    var counterAlarmReadMilliseconds = counterAlarmReadStopwatch.ElapsedMilliseconds;

                    // 数据源扫描（第四组）：温湿度/能耗等。触发位判定/回执写入/定时采集 + 越限/偏离告警状态机。
                    // 与 ScanCounterAlarms 同级独立 try-catch：单源扫描失败不阻断其他扫描与历史快照写入。
                    TryScan(_scanPipeline.ScanSources, nameof(PlcScanPipeline.ScanSources));

                    // 首次启动后从 StatusTransitions 重建 OEE 时间（仅执行一次，依赖采集已落库状态转换）
                    if (!_oeeTimeRebuilt)
                    {
                        RebuildOeeTimeFromHistory();
                        _oeeTimeRebuilt = true;
                    }

                    long historyWriteMilliseconds = 0;
                    if (noDevicesToRead)
                    {
                        // 无设备可采集（0 台设备或全部未配置地址）：连接正常但无设备可读，
                        // 不触发断连重连，仅跳过历史快照，避免无设备时反复误报"PLC已断开"
                    }
                    else if (successDevices.Count == 0)
                    {
                        // 所有设备读取失败：标记断开，触发重连
                        _connectionManager.MarkDisconnected();
                    }
                    else
                    {
                        // 至少一台设备读取成功：按间隔写历史快照（仅记录本轮成功的设备）
                        _scanCount++;
                        if (_scanCount >= _appSettings.HistoryWriteIntervalScans)
                        {
                            _scanCount = 0;
                            var historyWriteStopwatch = Stopwatch.StartNew();
                            LogProductionSnapshot(successDevices);
                            historyWriteMilliseconds = historyWriteStopwatch.ElapsedMilliseconds;
                        }
                    }

                    var configuredDevices = _deviceRepository.GetDevicesSnapshot().Count;
                    var estimatedReadOperations = CountConfiguredReadOperations();
                    _diagnostics.RecordConnectedCycle(
                        successDevices,
                        noDevicesToRead,
                        configuredDevices,
                        estimatedReadOperations,
                        _scanPipeline.BatchReadRequests,
                        _scanPipeline.BatchReadSuccesses,
                        _scanPipeline.BatchReadValues,
                        _scanPipeline.BatchReadFallbacks,
                        _scanPipeline.BatchPlanRebuilds,
                        _scanPipeline.BatchPlanBuildMilliseconds);
                    _diagnostics.RecordStageTimings(
                        dwordReadMilliseconds,
                        alarmReadMilliseconds,
                        defectReadMilliseconds,
                        counterAlarmReadMilliseconds,
                        historyWriteMilliseconds);
                }
                else
                {
                    _diagnostics.RecordDisconnected(
                        _deviceRepository.GetDevicesSnapshot().Count,
                        CountConfiguredReadOperations());
                    // PLC 断线：将所有处于真实状态(1/2/3)的设备标记为离线，
                    // 使停机时段在 OEE 历史回溯中不计入任何状态（state=0 不累计）。
                    // 仅记录一次边沿（写后 prev=0），后续循环不再重复刷写。
                    foreach (var device in _deviceRepository.GetDevicesSnapshot())
                        LogOfflineTransition(device);

                    // PLC 断线时清除所有设备的活跃报警，避免遗留报警状态持续显示到 UI
                    _scanPipeline.ClearAlarmsOnDisconnect();

                    // PLC 断线时重置 Stopwatch 计时起点，
                    // 避免重连后第一次成功读取时 elapsed 包含整个断线期间导致 OEE 时间暴涨
                    _pollingStopwatch.Restart();
                }
            }
            catch (OperationCanceledException)
            {
                // Cancel 触发：跳出循环（外层 await Task.Delay 也会捕获，这里一致处理）
                break;
            }
            catch (Exception ex)
            {
                // P1-12：关键异常重抛，避免吞掉 OutOfMemoryException 等导致后续不可预测行为
                if (ex is OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException)
                    throw;

                // 异常分类：
                // - HslPlcDriver 已用 try-catch 包裹所有读写方法，把 HslCommunication 异常转成 PlcOperationResult.Fail
                // - TryScan 内部已对 ScanAlarms/ScanDefects/ScanCounterAlarms 做相同隔离处理
                // - 因此这里捕获的异常都是非通信异常（UI 跨线程、空引用、业务逻辑 bug 等），
                //   不应误判为 PLC 断连（否则会触发重连冷却期，掩盖真实业务问题并误导用户）
                // - 若异常确为通信类（SocketException/ObjectDisposedException 等漏出到此），
                //   下一轮 RefreshDeviceData 仍会因读取失败触发 MarkDisconnected(ReadFailure)，由专用路径处理
                if (IsCommunicationException(ex))
                {
                    _logger.LogError(ex, "采集循环捕获到通信类异常（下一轮将走 ReadFailure 路径触发断连）");
                }
                else
                {
                    _logger.LogError(ex, "采集循环捕获到业务异常（不影响 PLC 连接状态，下一轮继续采集）");
                }
                _diagnostics.RecordFailure(ex.Message);
                // 异常时也重置 Stopwatch，避免下次累计包含异常处理期间的时间
                _pollingStopwatch.Restart();
            }

            try
            {
                await Task.Delay(_appSettings.PollingIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                // 取消发生在本轮采集工作完成后的延迟期间：本轮仍是已完成周期，
                // 先结算诊断计数再退出，避免 StopAsync 后 CompletedCycles 丢失最后一轮。
                CompleteDiagnosticsCycle();
                break;
            }

            CompleteDiagnosticsCycle();
        }
        _pollingStopwatch.Stop();
        _diagnostics.Stop();
    }

    private void CompleteDiagnosticsCycle()
    {
        _diagnostics.CompleteCycle();
    }

    private int CountConfiguredReadOperations()
    {
        var count = 0;
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            var codec = _adapterResolver.Resolve(device).AddressCodec;
            count += codec.Parse(device.OkCountAddress) is { IsValid: true, Type: PlcAddressType.DWord } ? 1 : 0;
            count += codec.Parse(device.NgCountAddress) is { IsValid: true, Type: PlcAddressType.DWord } ? 1 : 0;
            count += codec.Parse(device.StatusCountAddress) is { IsValid: true, Type: PlcAddressType.DWord } ? 1 : 0;
            count += device.Alarms.Count(a => codec.Parse(a.PlcAddress) is { IsValid: true, Type: PlcAddressType.MBit });
            count += device.Defects.Count(d => codec.Parse(d.PlcAddress) is { IsValid: true, Type: PlcAddressType.DWord });
            count += device.CounterAlarms.Count(c => c.Enabled && codec.Parse(c.PlcAddress) is { IsValid: true, Type: PlcAddressType.DWord });
        }
        return count;
    }

    /// <summary>
    /// 包装报警/缺陷/计数报警扫描方法：捕获异常并记 LogError，不向上抛出。
    /// 避免单类扫描失败被外层 catch 块误判为 PLC 断连（successDevices 已成功读取，PLC 实际正常）。
    /// 通信异常（SocketException/ObjectDisposedException 等）触发 MarkDisconnected(ScanException)，
    /// 业务异常只记 LogError 不影响连接状态。
    /// </summary>
    private bool TryScan(Func<bool> scanAction, string scanName)
    {
        try
        {
            return scanAction();
        }
        catch (Exception ex)
        {
            if (ex is OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException)
                throw;
            if (IsCommunicationException(ex))
            {
                _logger.LogError(ex, "{ScanName} 扫描发生通信异常（触发 PLC 断连）", scanName);
                _connectionManager.MarkDisconnected(DisconnectionReason.ScanException);
            }
            else
            {
                _logger.LogError(ex, "{ScanName} 扫描发生业务异常（不影响 PLC 连接状态）", scanName);
            }
            return false;
        }
    }

    /// <summary>TryScan 的 void 重载，用于 ScanCounterAlarms 等无返回值扫描。</summary>
    private void TryScan(Action scanAction, string scanName)
    {
        try
        {
            scanAction();
        }
        catch (Exception ex)
        {
            if (ex is OutOfMemoryException or AppDomainUnloadedException or ThreadAbortException)
                throw;
            if (IsCommunicationException(ex))
            {
                _logger.LogError(ex, "{ScanName} 扫描发生通信异常（触发 PLC 断连）", scanName);
                _connectionManager.MarkDisconnected(DisconnectionReason.ScanException);
            }
            else
            {
                _logger.LogError(ex, "{ScanName} 扫描发生业务异常（不影响 PLC 连接状态）", scanName);
            }
        }
    }

    /// <summary>
    /// 识别已知通信异常类型。HslPlcDriver 已用 try-catch 包裹 IO，理论上不会逃逸；
    /// 此检测仅作防御性兜底，针对其他 IPlcDriver 实现或 Configure 重建瞬间引用旧实例的场景。
    /// internal 便于单元测试验证异常分类逻辑。
    /// </summary>
    internal static bool IsCommunicationException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex is System.IO.IOException
            or System.Net.Sockets.SocketException
            or ObjectDisposedException
            or TimeoutException
            or System.Net.WebException;
    }

    /// <summary>
    /// 遍历所有设备，从 PLC 读取 OK/NG 产量 + 状态字。
    /// 返回读取成功的设备集合，供 AccumulateOeeTime 按设备独立累计时间。
    /// </summary>
    internal HashSet<string> RefreshDeviceData(out bool noDevicesToRead)
    {
        HashSet<string> successDevices = [];
        _scanPipeline.PrepareDWordBatchValues();
        var checkedDevices = 0;
        var notConfiguredDevices = 0;
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            checkedDevices++;
            try
            {
                var ok = TryReadOkCount(device);
                var ng = TryReadNgCount(device);
                var sw = TryReadStatusWord(device);

                // 三项各自独立处理：一项失败不阻断其他项，避免 NG 地址错误导致 OK 产量和状态时长全部丢失。
                // 已成功读取的项内部已更新 DeviceRuntime，后续 AccumulateOeeTime 使用独立逻辑。
                var anySuccess = ok == ReadResult.Success || ng == ReadResult.Success || sw == ReadResult.Success;
                var noneConfigured = ok == ReadResult.NotConfigured && ng == ReadResult.NotConfigured && sw == ReadResult.NotConfigured;

                if (noneConfigured)
                {
                    notConfiguredDevices++;
                    continue;
                }

                if (anySuccess)
                    successDevices.Add(device.Id);
            }
            catch (Exception ex)
            {
                // 单台设备读取异常不应中断整轮采集，避免一台故障导致其他设备本轮数据丢失
                _logger.LogError(ex, "读取设备 {DeviceId} 数据时发生异常", device.Id);
            }
        }
        // 无可采集设备：0 台设备（checkedDevices==0）或全部设备均未配置地址 都视为"无设备可读"，
        // 不应触发 MarkDisconnected（连接本身正常），避免无设备时反复误报"PLC已断开"并空转重连。
        // 旧逻辑带 checkedDevices > 0 前置条件，会漏掉"0 台设备"场景，使其误判断开，这里改为相等比较。
        noDevicesToRead = checkedDevices == notConfiguredDevices;
        return successDevices;
    }

    /// <summary>
    /// PLC 读取结果三态：区分"未配置"与"读取失败"，避免未配置设备被错误累计 OEE 时间
    /// </summary>
    internal enum ReadResult { Success, NotConfigured, Failed }

    /// <summary>
    /// 读取产量计数器的通用实现。OK/NG 共享相同的基线管理与回退逻辑，避免代码重复。
    /// </summary>
    internal ReadResult TryReadProductionCount(
        Models.Device device, string address, string addressLabel, string baseKeySuffix,
        Action<DeviceRuntime, int> setRaw, Action<DeviceRuntime, int> setDelta)
    {
        var runtime = GetRuntime(device);
        if (runtime == null) return ReadResult.NotConfigured;

        if (string.IsNullOrWhiteSpace(address)) return ReadResult.NotConfigured;
        var adapter = _adapterResolver.Resolve(device);
        if (adapter.AddressCodec.Parse(address) is not { IsValid: true, Type: PlcAddressType.DWord })
        {
            _logger.LogWarning("设备 {Device} {Label}地址格式无效: {Address}", device.Name, addressLabel, address);
            return ReadResult.NotConfigured;
        }

        var result = _scanPipeline.ReadInt32Value(device, address);
        if (!result.IsSuccess) return ReadResult.Failed;

        var raw = result.Content;
        setRaw(runtime, raw);

        // 班次切换清零窗口期：保持产量为 0（ResetShift 已清零），仅记录原始值
        if (_baselineCoordinator.IsInClearWindow(device.Id))
        {
            setDelta(runtime, 0);
            return ReadResult.Success;
        }

        var key = device.Id + baseKeySuffix;
        // 取/建基线：首次读取按班次一致恢复持久化基线，否则以当前 raw 为新基线；
        // PLC 计数器回退时以 raw 为新高线。变更由 ProductionBaselineStore 内部原子落盘。
        var baseline = _baselineStore.GetOrCreate(key, raw, _shiftContext.CurrentShiftId?.ToString());
        setDelta(runtime, raw - baseline);
        return ReadResult.Success;
    }



    /// <summary>读取 OK 产量（委托 TryReadProductionCount 通用逻辑）</summary>
    internal ReadResult TryReadOkCount(Models.Device device)
        => TryReadProductionCount(device, device.OkCountAddress, "OK数量", "_ok_base",
            (rt, v) => rt.OkProduction = v,
            (rt, v) => rt.TotalOkProduction = v);

    /// <summary>读取 NG 产量（委托 TryReadProductionCount 通用逻辑）</summary>
    internal ReadResult TryReadNgCount(Models.Device device)
        => TryReadProductionCount(device, device.NgCountAddress, "NG数量", "_ng_base",
            (rt, v) => rt.NgProduction = v,
            (rt, v) => rt.TotalNgProduction = v);

    /// <summary>
    /// 读取单个设备的状态字。
    /// </summary>
    internal ReadResult TryReadStatusWord(Models.Device device)
    {
        var runtime = GetRuntime(device);
        if (runtime == null) return ReadResult.NotConfigured;

        var addr = device.StatusCountAddress;
        if (string.IsNullOrWhiteSpace(addr)) return ReadResult.NotConfigured;
        var adapter = _adapterResolver.Resolve(device);
        if (adapter.AddressCodec.Parse(addr) is not { IsValid: true, Type: PlcAddressType.DWord })
        {
            _logger.LogWarning("设备 {Device} 状态地址格式无效: {Address}", device.Name, addr);
            return ReadResult.NotConfigured;
        }

        var result = _scanPipeline.ReadInt32Value(device, addr);
        if (result.IsSuccess)
        {
            runtime.StatusWord = result.Content;
            _statusTracker.ReadAndUpdate(device, result.Content, _statusHistory, GetCurrentShiftName(), _logger, dto => StatusEdgeDetected?.Invoke(dto));
            return ReadResult.Success;
        }
        return ReadResult.Failed;
    }

    /// <summary>
    /// 将设备标记为"离线"：写入一条 CurrentState=0 的状态转换（仅当设备当前处于真实状态 1/2/3 时）。
    /// 用于软件停机或 PLC 断线场景：停机时段在 OEE 历史回溯中不被计入任何状态时长
    /// （AccumulateState 忽略 state=0），避免停机前的状态（往往是运行）被延续计入导致 OEE 虚高。
    /// 写入后将 _prevStatusWords 置 0，重连后真实状态读取会自然写入 0→真实状态 转换，不会重复刷写。
    /// 若设备已处于离线(0)或从未有过状态记录，则跳过。
    /// </summary>
    internal void LogOfflineTransition(Models.Device device)
        => _statusTracker.LogOfflineTransition(device, _statusHistory, GetCurrentShiftName(), _logger, dto => StatusEdgeDetected?.Invoke(dto));


    /// <summary>
    /// 记录本轮采集成功的设备的当前产量快照到历史数据库。
    /// 写入时附带当前班次名称快照（ShiftName），便于按班次查询历史。
    /// 仅记录 successDevices 中的设备：读取失败设备的运行时值仍是上一次成功读的旧值，
    /// 写入会造成历史数据失真。
    /// 写入 TotalOkProduction/TotalNgProduction（班次会话累计值）而非 OkProduction/NgProduction
    /// （PLC 原始累计值），使历史快照具备班次语义，可直接按班次统计产量而无需相邻快照差分。
    /// </summary>
    internal void LogProductionSnapshot(HashSet<string> successDevices)
    {
        var shiftName = GetCurrentShiftName();
        var count = 0;
        var defectSnapshots = new List<DefectSnapshotRecord>();
        var timestamp = DateTime.Now;

        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            if (!successDevices.Contains(device.Id))
                continue;

            var runtime = GetRuntime(device);
            if (runtime == null) continue;

            // 关联当前设备的 Running 工单（若有），打通工单与生产数据。
            // 未启动工单或 _workOrderRepo 未注入（测试场景）时为 null，历史记录仍可写入。
            var workOrderId = _workOrderRepo?.GetRunningByDevice(device.Id)?.Id;

            _productionWriter.LogProduction(new Kanban.Collector.Core.Entities.ProductionLog
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                ShiftName = shiftName,
                WorkOrderId = workOrderId,
                OkProduction = runtime.TotalOkProduction,
                NgProduction = runtime.TotalNgProduction,
                StatusWord = runtime.StatusWord,
                Timestamp = timestamp,
                // 稳定事件标识：恢复文件回放幂等键（同 EventId 不重复落库）
                EventId = Guid.NewGuid(),
            });
            foreach (var defect in device.Defects)
            {
                // 缺陷快照降频：累计值无变化且班次未变时不落库（帕累托增量差分只需变化点）。
                // 此前每采集轮次全量写（约 8.5 条/秒 → 73 万条/天），导致复盘页查询
                // 2 天窗口 27 万条全量物化（15~18s）。变化才写后写入量降到变化频率。
                var defectKey = $"{device.Id}|{defect.Id}";
                var defectCount = Math.Max(0, defect.Count);
                if (_lastDefectSnapshot.TryGetValue(defectKey, out var lastDefect)
                    && lastDefect.Count == defectCount
                    && lastDefect.ShiftName == shiftName)
                    continue;
                _lastDefectSnapshot[defectKey] = (defectCount, shiftName);

                defectSnapshots.Add(new DefectSnapshotRecord
                {
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    DefectId = defect.Id,
                    DefectName = defect.Name,
                    Severity = defect.Severity,
                    Category = defect.Category,
                    ShiftName = shiftName,
                    Count = defectCount,
                    Timestamp = timestamp,
                });
            }
            count++;
        }

        _defectHistoryStore?.Append(defectSnapshots);

        // 数据源快照：仅记录本轮 ScanSources 成功采样的源（设计稿 §5：单表 + device_id，按设备聚合查询）。
        // 落盘后清空采样累积，使下一落盘周期重新累计（触发源稀疏采样不丢值）。
        if (_dataSourceSnapshotStore != null)
        {
            _dataSourceSnapshotStore.Append(BuildSourceSnapshots(shiftName, timestamp));
            _scanPipeline.ClearCycleSourceValues();
        }
    }

    /// <summary>构建本轮落盘的数据源快照集合（只包含本轮 ScanSources 成功采样的值项；SourceId=值项 Id，按值项聚合）。</summary>
    private List<DataSourceSnapshotRecord> BuildSourceSnapshots(string shiftName, DateTime timestamp)
    {
        var sourceValues = _scanPipeline.GetCycleSourceValues();
        var snapshots = new List<DataSourceSnapshotRecord>();
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            foreach (var source in device.Sources.ToList())
            {
                if (!source.Enabled) continue;
                foreach (var value in source.Values.ToList())
                {
                    var key = $"{device.Id}:{source.Id}:{value.Id}";
                    if (!sourceValues.TryGetValue(key, out var v)) continue;
                    snapshots.Add(new DataSourceSnapshotRecord
                    {
                        DeviceId = device.Id,
                        DeviceName = device.Name,
                        SourceId = value.Id,
                        SourceName = value.Name,
                        SourceType = source.Type,
                        Unit = value.Unit,
                        Value = v,
                        ShiftName = shiftName,
                        Timestamp = timestamp,
                    });
                }
            }
        }
        return snapshots;
    }

    // ──────────── 扫描子系统 facade（转发 PlcScanPipeline，维持 internal 接口不变——测试与历史调用方零改动） ────────────

    internal bool ScanAlarms() => _scanPipeline.ScanAlarms();
    internal bool ScanDefects() => _scanPipeline.ScanDefects();
    internal void ScanCounterAlarms() => _scanPipeline.ScanCounterAlarms();
    internal void ClearAlarmsOnDisconnect() => _scanPipeline.ClearAlarmsOnDisconnect();

    /// <summary>
    /// 根据每个设备的 StatusWord 按轮询间隔累计 OEE 时间。
    /// 仅对本次读取成功的设备累计，单台失败不影响其他设备。
    /// 状态字定义：1=运行, 2=报警, 3=待机，其他值为未知状态
    /// </summary>
    internal void AccumulateOeeTime(double intervalSeconds, HashSet<string> successDevices)
    {
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            if (!successDevices.Contains(device.Id))
                continue;

            var runtime = GetRuntime(device);
            if (runtime == null) continue;

            var sw = runtime.StatusWord;
            if (sw == (int)DeviceStatus.Running)
                runtime.RunTime += intervalSeconds;
            else if (sw == (int)DeviceStatus.Alarm)
                runtime.AlarmTime += intervalSeconds;
            else if (sw == (int)DeviceStatus.Paused)
                runtime.PausedTime += intervalSeconds;
            else if (sw == (int)DeviceStatus.Unknown)
                _logger.LogDebug("设备 {Device} 初始（StatusWord=0），不计入 OEE 时间", device.Name);
            else
                // 持续性条件：每轮都会触发，降为 Debug 避免日志泛滥。首次出现时在 Debug 日志可见。
                _logger.LogDebug("设备 {Device} 状态字非法: {StatusWord}（合法值: 0=初始, 1=运行, 2=报警, 3=待机）", device.Name, sw);
        }
    }

    /// <summary>
    /// 从 DeviceRepository.RuntimeMap 获取对应设备的运行时状态
    /// </summary>
    private Models.DeviceRuntime? GetRuntime(Models.Device device) =>
        _deviceRepository.RuntimeMap.TryGetValue(device.Id, out var rt) ? rt : null;

    /// <summary>
    /// 首次启动时从 StatusTransitions 表重建本班次的 RunTime/AlarmTime/PausedTime。
    /// 与运行时 AccumulateOeeTime 使用同一口径（StatusWord 状态字），保证重启前后可用率/性能率可比，
    /// 并消除旧实现中"多个活跃报警时长求和""报警位≠状态字"两类口径偏差（见 #3/#4 业务修正）。
    /// 重建范围根据历史完整性确定：
    /// - 班次前有记录（正常崩溃重启）：从班次起始时刻累计，初始状态为班次前最后状态
    /// - 班次前无记录但班次内有记录（清理数据后启动）：从第一条 transition 开始累计，避免从班次起始虚高
    /// - 完全无历史记录：跳过重建，让运行时从 0 累计
    /// </summary>
    private void RebuildOeeTimeFromHistory()
    {
        var now = DateTime.Now;
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            var runtime = GetRuntime(device);
            if (runtime == null) continue;

            var shiftStart = GetCurrentShiftStart(now);
            var transitions = _statusHistory.QueryStatusTransitions(device.Id, shiftStart, now);

            // 查询班次起始前的最后一条状态转换，确定班次起始时刻的初始状态（与 HistoryQueryViewModel 口径一致）
            var preTransitions = _statusHistory.QueryStatusTransitions(device.Id, shiftStart.AddDays(-1), shiftStart);
            var lastBefore = preTransitions
                .Where(t => t.EventTime < shiftStart)
                .OrderByDescending(t => t.EventTime)
                .FirstOrDefault();

            // 完全无历史记录（清理数据后首次启动且尚未采集）：跳过重建，让运行时从 0 累计
            if (lastBefore == null && transitions.Count == 0)
            {
                _logger.LogInformation(
                    "设备 {Device} 无历史状态转换记录，跳过 OEE 时间重建（运行时将从 0 累计）",
                    device.Name);
                continue;
            }

            DateTime from;
            int initialState;
            if (lastBefore != null)
            {
                // 正常情况：班次前有记录，从班次起始累计，初始状态为班次前最后状态
                from = shiftStart;
                initialState = lastBefore.CurrentState;
            }
            else
            {
                // 班次前无记录但班次内有记录（清理数据后启动采集已写入记录）：
                // 从第一条 transition 的 EventTime 开始累计，避免从班次起始到第一条记录之间
                // 的时段（状态未知）被默认为运行导致 OEE 虚高
                from = transitions[0].EventTime;
                initialState = transitions[0].CurrentState;
            }

            var (run, alarm, paused) = OeeCalculator.CalculateStateDurations(transitions, from, now, initialState);
            runtime.RunTime = run;
            runtime.AlarmTime = alarm;
            runtime.PausedTime = paused;
            _logger.LogInformation(
                "设备 {Device} 已从历史重建 OEE 时间 Run={Run:F1}s Alarm={Alarm:F1}s Idle={Paused:F1}s（from={From:HH:mm:ss}）",
                device.Name, run, alarm, paused, from);
        }
    }

    /// <summary>
    /// 返回当前班次名称。用于历史记录写入时附带班次快照。_currentShiftId 为 null 时返回空字符串。
    /// </summary>
    internal string GetCurrentShiftName() => _shiftContext.CurrentName;

    /// <summary>
    /// 计算当前班次的起始 DateTime，用于重启后 OEE 时间重建的范围下界。
    /// 基于当前时刻所属班次配置推算；跨天班次（如夜班 20:00-次日08:00）若当前时刻在凌晨段，
    /// 起始落在昨天，使重建范围正确覆盖整个夜班。
    /// </summary>
    internal DateTime GetCurrentShiftStart(DateTime now)
    {
        // 班次集合锁内快照：与 ConfigSyncHandler/CopySettings 的原地写入互斥（审查修复 2026-08-13）
        List<ShiftConfig> snapshot;
        lock (_appSettings.ShiftsLock) snapshot = _appSettings.Shifts.ToList();
        return _shiftContext.CurrentStart(now, snapshot);
    }

    /// <summary>
    /// 上班次产量汇总的只读快照（线程安全拷贝）。
    /// HomeViewModel 通过此方法获取当前设备的上班次数据进行对比展示。
    /// </summary>
    public (int Ok, int Ng, string ShiftName) GetLastShiftSummary(string deviceId)
        => _shiftContext.GetLastShiftSummary(deviceId);

    /// <summary>
    /// 重置所有设备的班次累计：
    /// - 向每台设备各自的 ProductionResetAddress（D 字地址）写 1，触发 PLC 程序清零对应设备的产量计数器
    ///   （由 PLC 程序负责清零 OK/NG 计数器，软件不再单独向每个 OK/NG 地址写 0）
    /// - 产量基线清空采用延迟策略：触发 PLC 清零后延迟1秒再结束清空窗口，
    ///   期间由 ProductionBaselineStore.ClearAll 已清空内存与磁盘基线，
    ///   避免在 PLC 程序真正执行清零前，旧累计值被当作新基线导致本班次产量永久为 0。
    ///   期间若读取到 raw &lt; baseline，会自动更新基线（见 TryReadOkCount/TryReadNgCount）。
    /// - PLC 未连接时推迟清空基线：标记对应设备到 _pendingPlcResetOnReconnect，
    ///   待 PLC 重连后再触发清零 + 延迟1秒清空基线。
    /// - 清空报警状态字典，避免跨班次误判边沿；
    ///   下次读取时若 AlarmEvents 历史表最近事件为"触发"且无恢复，
    ///   会通过 GetLatestAlarmEvent 重建内存状态（StartTime 留空，新班次重新触发）
    /// - 调用每个 Device.ResetShift() 重置 OEE 时间/会话累计产量/报警时间戳
    /// </summary>
    public void ResetShift()
    {
        // _resetLock 保证 ResetShift 与 ResetDeviceProduction 互斥，避免 UI 线程单设备清零
        // 与轮询线程班次重置并发导致状态字典半重置
        lock (_resetLock)
        {
            // 软件侧 OEE 累计立即清零（断线期间本就没累计，立即清零无副作用）
            _scanPipeline.ResetAll();
            _statusTracker.ResetAll();
            _baselineCoordinator.ResetAll();

            // 清空全部产量基线（内存活动缓存 + 磁盘快照），并以当前班次标识持久化空基线。
            // 下一轮读取将以当前 PLC 值重建基线，避免跨班次误恢复旧基线。
            _baselineStore.ClearAll(_shiftContext.CurrentShiftId?.ToString());

            foreach (var device in _deviceRepository.GetDevicesSnapshot())
            {
                GetRuntime(device)?.ResetShift();

                // 同步重置报警时间戳，避免 Alarm.Duration 跨班次导致与 AlarmTime 语义割裂
                foreach (var alarm in device.Alarms.ToList())
                {
                    alarm.StartTime = default;
                    alarm.EndTime = default;
                }

                // 每台设备独立触发 PLC 清零 + 延迟清空基线
                if (_connectionManager.IsConnected)
                {
                    TriggerPlcProductionReset(device);
                    _baselineCoordinator.ScheduleClear(device.Id);
                }
                else
                {
                    // PLC 未连接，推迟到重连后再触发清零
                    _baselineCoordinator.AddPendingReconnect(device.Id);
                }
            }

            if (!_connectionManager.IsConnected)
            {
                _logger.LogWarning("班次重置时 PLC 未连接，{Count} 台设备推迟清零到 PLC 重连后",
                    _baselineCoordinator.PendingReconnectCount);
            }
        }
    }

    /// <summary>
    /// 向指定设备的 ProductionResetAddress 写 1，触发 PLC 程序清零该设备产量。
    /// 返回 PLC 写入是否成功；地址无效或写入失败均返回 false。
    /// 调用方需保证 PLC 已连接（班次切换时由 ResetShift 分流处理，PLC 重连场景已确认连接）。
    /// </summary>
    internal bool TriggerPlcProductionReset(Models.Device device)
    {
        var addr = device.ProductionResetAddress;
        if (string.IsNullOrWhiteSpace(addr))
        {
            _logger.LogInformation("设备 {Device} 未配置产量清零地址，跳过 PLC 清零触发", device.Name);
            return false;
        }
        var adapter = _adapterResolver.Resolve(device);
        if (adapter.AddressCodec.Parse(addr) is not { IsValid: true, Type: PlcAddressType.DWord })
        {
            _logger.LogWarning("设备 {Device} 产量清零地址格式无效（需要 D 字地址）: {Address}", device.Name, addr);
            return false;
        }

        var result = adapter.WriteInt32(addr, 1);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("设备 {Device} 触发产量清零失败: {Message}", device.Name, result.Message);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 手动 OEE 清零单台设备：触发 PLC 清零 + 清零软件侧 OEE 累计值 + 设置基线清零窗口。
    /// 与班次切换 ResetShift 对单台设备的效果一致，但仅作用于指定设备：
    /// - 清零产量累计值（TotalOkProduction / TotalNgProduction）+ 产量基线
    /// - 清零 OEE 时间（RunTime / AlarmTime / PausedTime）
    /// - 重置报警时间戳 + 清理 _prevAlarmStates 中该设备的报警状态（保持一致）
    /// 调用方需保证 PLC 已连接。
    /// 返回 PLC 清零是否成功（软件侧清零始终执行，不依赖 PLC 写入结果）。
    /// </summary>
    public bool ResetDeviceProduction(Models.Device device)
    {
        // _resetLock 与 ResetShift 互斥，避免 UI 线程单设备清零与轮询线程班次重置并发
        lock (_resetLock)
        {
            var plcResetSuccess = TriggerPlcProductionReset(device);

            // 软件侧 OEE 累计清零（无论 PLC 写入是否成功都执行，
            // 因为 PLC 清零失败时软件侧清零不会有副作用，且后续读取会自然跟上实际值）
            GetRuntime(device)?.ResetShift();

            // 重置报警时间戳，并清理 _prevAlarmStates 中该设备的报警状态，
            // 使下一轮 ScanAlarms 走重建逻辑（从历史表恢复），避免时间戳与内存状态不一致
            _scanPipeline.RemoveDeviceAlarms(device);
            foreach (var alarm in device.Alarms.ToList())
            {
                alarm.StartTime = default;
                alarm.EndTime = default;
            }

            // 清空该设备的产量基线，设置1秒窗口期避免旧值被当作新基线
            _baselineStore.ClearDevice(device.Id);
            _baselineCoordinator.ScheduleClear(device.Id);

            return plcResetSuccess;
        }
    }

    /// <summary>
    /// 清理已删除设备的残留内存状态：
    /// - 产量基线（通过 ProductionBaselineStore.ClearDevice 清理内存活动缓存与磁盘快照）
    /// - _prevAlarmStates 中该设备所有 Alarm.Id 的 key
    /// - _shiftChangeFailedAlarms 中该设备所有 Alarm.Id 的 key
    /// - _prevStatusWords 中该 Device.Id 的 key
    /// - _pendingBaselineClearAt 中该 Device.Id 的 key
    /// - _pendingPlcResetOnReconnect 中该 Device.Id 的 key
    /// 应在 DeviceManagerViewModel.RemoveDevice 删除设备后调用，避免内存泄漏
    /// </summary>
    public void RemoveDeviceState(Models.Device device)
    {
        if (device == null) return;

        _scanPipeline.RemoveDeviceAlarms(device);
        _statusTracker.RemoveDevice(device.Id);
        _baselineCoordinator.RemoveDevice(device.Id);
        _shiftContext.RemoveDevice(device.Id);

        // 清理该设备的产量基线（内存活动缓存 + 磁盘快照），避免 baselines.json 随设备增删膨胀
        _baselineStore.ClearDevice(device.Id);
        _logger.LogInformation("已清理设备 {Device} 的持久化产量基线", device.Name);
    }

    /// <summary>
    /// 清理已删除报警的残留内存状态（_prevAlarmStates + _shiftChangeFailedAlarms）。
    /// 应在 DeviceManagerViewModel.RemoveAlarm 删除报警后调用，避免内存泄漏。
    /// </summary>
    public void RemoveAlarmState(string alarmId)
        => _scanPipeline.RemoveAlarmState(alarmId);

    /// <summary>
    /// 检测班次切换：根据 DateTime.Now.TimeOfDay 匹配当前班次，
    /// 若与上一班次不同（或首次初始化）则触发 ResetShift()。
    /// 班次标识使用 <see cref="ShiftIdentifier"/> record（按 Name+StartTime+EndTime 值比较），避免重名班次误判。
    /// 注意：班次配置为空或当前时刻不属于任何班次时不触发，避免误清零。
    /// 班次切换时，对当前仍触发中的报警记录 EventType=3"班次切换"事件，
    /// 标记该报警在新班次重新开始计算 Duration（不沿用上班次的 StartTime）。
    /// </summary>
    internal void DetectShiftChange()
    {
        // 班次集合锁内快照：与 ConfigSyncHandler/CopySettings 的原地写入互斥（审查修复 2026-08-13）
        List<ShiftConfig> snapshot;
        lock (_appSettings.ShiftsLock) snapshot = _appSettings.Shifts.ToList();
        var newShift = _shiftContext.DetectChange(snapshot);
        if (newShift is null) return;

        _logger.LogInformation("班次切换：{Old} → {New}，触发 ResetShift", _shiftContext.CurrentName, newShift.Name);

        // 班次切换前缓存上班次产量汇总（在 ResetShift 清零之前）
        // 用于 HomeViewModel 本班次 vs 上班次对比展示
        // 注意：此时 rt.TotalOkProduction 仍是【旧班次】产量，因此班次名称也必须用【旧班次】
        // _currentShiftId 在下方才更新为新班次 ID
        var devicesSnapshot = _deviceRepository.GetDevicesSnapshot();
        _shiftContext.CacheLastShiftSummaries(devicesSnapshot, GetRuntime, GetCurrentShiftName());

        // 班次切换前，同步写入 EventType=3 事件（单次尝试，不 Thread.Sleep 重试），
        // 必须在 ResetShift 清空 _prevAlarmStates 之前完成，
        // 确保 ScanAlarms 重建时 GetLatestAlarmEvent 能查到 EventType=3 而非旧 EventType=1
        _scanPipeline.LogShiftChangeForActiveAlarms(devicesSnapshot);

        // 先更新当前班次标识，使后续基线文件与产量快照都归属新班次，再执行 ResetShift
        _shiftContext.SetCurrentShift(newShift);
        ResetShift();
    }

    // ──────────── facade 委托方法（保留原 internal API，供测试与历史调用方使用） ────────────

    /// <summary>
    /// 同步遍历当前仍触发中的报警（_prevAlarmStates 值为 true），
    /// 记录 EventType=3"班次切换"事件到 AlarmEvents 表。
    /// facade 委托到 <see cref="AlarmStateTracker.LogShiftChangeForActiveAlarms"/>。
    /// </summary>
    internal void LogShiftChangeForActiveAlarms()
        => _scanPipeline.LogShiftChangeForActiveAlarms(_deviceRepository.GetDevicesSnapshot());

    /// <summary>
    /// 班次切换前缓存当前（即将成为"上班次"）各设备产量汇总。
    /// facade 委托到 <see cref="ShiftContext.CacheLastShiftSummaries"/>。
    /// </summary>
    internal void CacheLastShiftSummaries(string shiftName)
        => _shiftContext.CacheLastShiftSummaries(_deviceRepository.GetDevicesSnapshot(), GetRuntime, shiftName);
}
