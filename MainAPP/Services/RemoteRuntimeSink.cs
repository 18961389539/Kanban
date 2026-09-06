using System.Collections.Concurrent;
using Kanban.Client;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Mapping;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using AlarmEventType = Kanban.Contracts.Enums.AlarmEventType;
using MainAPP.Models;
using Microsoft.Extensions.Logging;
using System.Windows.Threading;
using CoreDataSourceValueType = Kanban.Collector.Core.Models.DataSourceValueType;

namespace MainAPP.Services;

/// <summary>
/// Remote 模式运行时同步器：把 Collector 推来的快照/事件灌回本地内存状态。
/// 双连接架构（与 WASM 端同源约束，桌面端同适用）：
///   _client        快照连接：唯一长驻 = 快照订阅（500ms）；Invoke（初始快照/设备/工单）走此连接
///   _eventsClient  事件连接：唯一长驻优先级——报警（第一个长驻，必须实时，不受"多长驻延迟"影响）
///                   + Meta（第二个长驻，一次性延迟约 13s 可接受，工单/班次低频）
/// 说明：桌面端"长驻+Invoke"合法（RemoteHistoryQueryService 已用），但"同连接多长驻订阅"
/// 第二个起会延迟（实测 13s）——报警若与快照同连接将成为第二长驻而延迟，故必须分连接。
/// </summary>
public sealed class RemoteRuntimeSink : IAsyncDisposable
{
    private readonly KanbanDataClient _client;
    private readonly KanbanDataClient _eventsClient;
    private readonly DeviceRepository _deviceRepository;
    private readonly WorkOrderRepository _workOrderRepository;
    private readonly ILogger<RemoteRuntimeSink> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdownCts = new();
    private CancellationToken _cancellationToken => _shutdownCts.Token;
    private readonly List<Task> _backgroundTasks = new();
    private readonly object _taskGate = new();
    private long _lastSnapshotReceivedTicks;
    private readonly ConcurrentDictionary<string, long> _lastSnapshotReceivedTicksByDevice = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近收到 Collector 快照的时间；只由快照刷新，不能被低频 Meta/事件掩盖。</summary>
    public DateTime LastSnapshotReceivedAt
    {
        get
        {
            var ticks = Volatile.Read(ref _lastSnapshotReceivedTicks);
            return ticks == 0 ? default : new DateTime(ticks, DateTimeKind.Local);
        }
    }

    public DateTime GetLastSnapshotReceivedAt(string deviceId)
    {
        if (!_lastSnapshotReceivedTicksByDevice.TryGetValue(deviceId, out var ticks))
            return default;
        return ticks == 0 ? default : new DateTime(ticks, DateTimeKind.Local);
    }

    // ── Dispatcher 节流合并 ──
    // 快照 500ms/设备（30 台 ≈60 帧/秒）+ 报警/状态边沿事件若逐条 InvokeAsync，UI 线程
    // 繁忙（OxyPlot 重建/页面切换）时 Dispatcher 队列无界积压（内存上升 + 数据滞后）。
    // 统一走 500ms 批量闸：SignalR 回调线程只入队（廉价），UI 线程定时批量应用。
    private readonly object _batchGate = new();
    private readonly SnapshotBatchMerger _snapshotMerger = new();
    private readonly List<AlarmEventDto> _pendingAlarmEvents = new();
    private readonly List<StatusEventDto> _pendingStatusEvents = new();
    private readonly System.Windows.Threading.DispatcherTimer _batchTimer;

    public RemoteRuntimeSink(
        KanbanDataClient client,
        DeviceRepository deviceRepository,
        WorkOrderRepository workOrderRepository,
        ILoggerFactory loggerFactory,
        ILogger<RemoteRuntimeSink> logger)
    {
        _client = client;
        _deviceRepository = deviceRepository;
        _workOrderRepository = workOrderRepository;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // 事件连接：与快照连接分开，保证报警是事件连接的第一个长驻订阅（实时不延迟）
        _eventsClient = new KanbanDataClient(
            client.HubUrl, loggerFactory.CreateLogger<KanbanDataClient>(), useMessagePack: true);
        // 节流定时器（500ms 批量闸）：Tick 在 _dispatcher 线程执行 → 批量应用免再封送；
        // 构造即启动：回调先于 Start() 到达时仍会被 500ms 内下一 tick 消费。
        _batchTimer = new System.Windows.Threading.DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _batchTimer.Tick += (_, _) => FlushBatch();
        _batchTimer.Start();
    }

    /// <summary>启动订阅。调用方须确保主 KanbanDataClient 已连接（事件连接在内部连接）。</summary>
    public void Start()
    {
        // 连接后先拉一次当前快照（覆盖 Collector 重启导致的内存清空）
        _client.OnSnapshot(OnSnapshotReceived);
        _client.Reconnected += (_, _) => { _ = OnReconnectedAsync(); };
        // 事件连接的回调注册延后到事件连接建立之后（On* 依赖连接已建立，EnsureConnected 会抛）：
        // 首次连接由 StartEventLinkAsync 在 ConnectAsync 成功后注册；
        // 首次失败后的后台重连成功路径由 OnEventsReconnectedAsync 兜底补注册。
        _eventsClient.Reconnected += (_, _) => { _ = OnEventsReconnectedAsync(); };

        TrackTask(RefreshAsync());
        TrackTask(_client.SubscribeSnapshotsAsync());
        TrackTask(StartEventLinkAsync());
    }

    /// <summary>注册事件连接回调（幂等：KanbanDataClient 按连接实例去重，审查修复 2026-08-13——
    /// 重试路径对同一连接重复调用不会双注册；连接实例重建后回调丢失，必须重新调用以重挂）。</summary>
    private void EnsureEventsCallbacksRegistered()
    {
        // 直接调用 On*：客户端层已按连接实例幂等（同连接重复注册被跳过、新连接自动重挂），
        // 原 bool 守卫在新连接场景会漏挂（回调随旧连接实例销毁）。
        _eventsClient.OnAlarmEvent(OnAlarmEventReceived);
        _eventsClient.OnStatusEvent(OnStatusEventReceived);
        _eventsClient.OnMeta(OnMetaReceived);
    }

    /// <summary>建立事件连接并订阅报警（第一个长驻）+ 状态 + Meta。
    /// 三个长驻订阅必须**并行**启动：报警订阅在连接正常时永不返回，串行 await 会让
    /// 后续订阅永远不可达。连接失败进入 5s 后台重连循环，成功后补注册回调并订阅（自愈）。</summary>
    private async Task StartEventLinkAsync()
    {
        try
        {
            await _eventsClient.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "事件连接建立失败：报警/状态/工单/班次推送不可用（快照链路不受影响，转入后台重连自愈）");
            StartEventsRetryLoop();
            return;
        }
        EnsureEventsCallbacksRegistered();
        // 订阅（含失败重试循环，见 StartSubscribeRetryLoop）
        StartSubscribeRetryLoop();
    }

    private bool _eventsRetryLoopStarted;
    private readonly object _eventsRetryGate = new();

    /// <summary>
    /// 事件连接首次连接失败后的后台重连循环（5s 间隔，单实例保证）。
    /// 重连成功后补齐事件连接的回调注册与长驻订阅；运行中连接断开时由 WithAutomaticReconnect
    /// + <see cref="OnEventsReconnectedAsync"/> 恢复，本循环只负责"从未连上过"的启动期场景。
    /// </summary>
    private void StartEventsRetryLoop()
    {
        lock (_eventsRetryGate)
        {
            if (_eventsRetryLoopStarted) return;
            _eventsRetryLoopStarted = true;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken);
                    await _eventsClient.ConnectAsync(_cancellationToken);
                    EnsureEventsCallbacksRegistered();
                    StartSubscribeRetryLoop();
                    _logger.LogInformation("事件连接后台重连成功，报警/状态/元数据订阅已恢复");
                    return;
                }
                catch (OperationCanceledException)
                {
                    return; // 连接被释放
                }
                catch (Exception retryEx)
                {
                    _logger.LogWarning(retryEx, "事件连接后台重连失败，5s 后重试");
                }
            }
        });
    }

    /// <summary>报警事件游标：已消费的最大 Seq。断线重连后从此处补拉，避免漏报。
    /// 仅在 <see cref="_lastAlarmEpoch"/> 不变时有效；Collector 重启（epoch 变化）时归零，
    /// 避免旧游标过滤掉新进程从 1 重新计数的事件（漏报）。</summary>
    private long _lastAlarmSeq;
    /// <summary>状态事件游标：与报警游标同语义（各自独立计数）。</summary>
    private long _lastStatusSeq;
    /// <summary>报警流纪元（审查修复 2026-08-13）：报警/状态回调并发于不同线程，
    /// 共享同一纪元字段存在竞态——报警先到更新共享纪元后，后到的状态事件走"同纪元"分支不归零游标，
    /// 导致重连后旧状态游标过滤掉新进程低 Seq 事件。改为每流独立纪元、各自归零自己的游标。</summary>
    private long _lastAlarmEpoch;
    private long _lastStatusEpoch;

    /// <summary>订阅报警事件流：带游标断线续传（服务端环形缓冲按 afterSeq 补发）。
    /// 返回是否成功——失败由 <see cref="StartSubscribeRetryLoop"/> 退避重试，避免事件永久丢失。</summary>
    private async Task<bool> SubscribeAlarmEventsWithResumeAsync()
    {
        try
        {
            await _eventsClient.SubscribeAlarmEventsAsync(_lastAlarmSeq);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // 连接被释放
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "订阅报警事件流失败（游标 {Seq}），5s 后重试", _lastAlarmSeq);
            return false;
        }
    }

    /// <summary>订阅状态事件流：带游标断线续传（与报警流同构，Collector 重启归零）。</summary>
    private async Task<bool> SubscribeStatusEventsWithResumeAsync()
    {
        try
        {
            await _eventsClient.SubscribeStatusEventsAsync(_lastStatusSeq);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "订阅状态事件流失败（游标 {Seq}），5s 后重试", _lastStatusSeq);
            return false;
        }
    }

    /// <summary>订阅元数据流（工单/班次，Collector 5s 推送）：长驻 fault 属正常生命周期，观察防未观察异常。</summary>
    private async Task SubscribeMetaSafeAsync()
    {
        try
        {
            await _eventsClient.SubscribeMetaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "元数据订阅结束（连接断开/重连触发，属正常）");
        }
    }

    private bool _subscribeRetryLoopStarted;
    private readonly object _subscribeRetryGate = new();

    /// <summary>
    /// 订阅层失败重试循环（单实例守卫）：报警/状态订阅任一失败（协议错误/服务端故障等非连接类异常）
    /// 即 5s 退避重试，全部成功后退出——旧实现只 LogWarning 无重试（重试仅挂在 Reconnected 上），
    /// 非连接类故障会让报警/状态事件永久漏掉。连接断开场景由连接层 Reconnected 重新触发本循环。
    /// </summary>
    private void StartSubscribeRetryLoop()
    {
        lock (_subscribeRetryGate)
        {
            if (_subscribeRetryLoopStarted) return;
            _subscribeRetryLoopStarted = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    try
                    {
                        // 三个长驻订阅必须**并行**启动（审查修复 2026-08-13）：报警订阅在连接正常时永不返回，
                        // 原实现串行 await 使状态/元数据订阅永远不可达（Remote 模式状态事件流与工单元数据从未建立）。
                        var alarmTask = SubscribeAlarmEventsWithResumeAsync();
                        var statusTask = SubscribeStatusEventsWithResumeAsync();
                        var metaTask = SubscribeMetaSafeAsync();
                        // WhenAll(Task<bool>, Task<bool>, Task) 命中 params Task[] 重载返回 Task（await 为 void，不能 var 接），
                        // 故分开取值：三个订阅一起结束后再读报警/状态结果。
                        await Task.WhenAll(alarmTask, statusTask, metaTask);
                        // 连接断开时三个长驻订阅一起结束（正常生命周期），由连接层 Reconnected 重启本循环；
                        // 非连接类失败（返回 false）才需要本循环 5s 退避重试。
                        if (await alarmTask && await statusTask)
                            return;
                    }
                    catch (OperationCanceledException)
                    {
                        return; // 连接被释放
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "事件订阅重试循环异常，5s 后重试");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken);
                }
            }
            finally
            {
                lock (_subscribeRetryGate) _subscribeRetryLoopStarted = false;
            }
        });
    }

    /// <summary>主连接重连成功：恢复快照订阅 + 按游标补拉报警事件。</summary>
    private async Task OnReconnectedAsync()
    {
        _logger.LogInformation("Collector 重连成功，恢复订阅（报警游标 {Seq}）", _lastAlarmSeq);
        try
        {
            await _client.SubscribeSnapshotsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重连后恢复快照订阅失败");
        }
    }

    /// <summary>事件连接重连成功：恢复报警 + 状态 + 元数据订阅（首次失败的后台重连路径在此补注册回调）。</summary>
    private async Task OnEventsReconnectedAsync()
    {
        _logger.LogInformation("事件连接重连成功，恢复报警/状态/元数据订阅");
        EnsureEventsCallbacksRegistered();
        // 订阅（含失败重试循环：非连接类故障不丢事件）
        StartSubscribeRetryLoop();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshots = await _client.GetCurrentSnapshotsAsync();
            if (snapshots.Count > 0)
                Volatile.Write(ref _lastSnapshotReceivedTicks, DateTime.Now.Ticks);
            foreach (var s in snapshots)
            {
                _lastSnapshotReceivedTicksByDevice[s.DeviceId] = DateTime.Now.Ticks;
                ApplySnapshot(s);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取初始快照失败（等待订阅推送）");
        }
    }

    private void OnSnapshotReceived(DeviceSnapshotDto snapshot)
    {
        Volatile.Write(ref _lastSnapshotReceivedTicks, DateTime.Now.Ticks);
        _lastSnapshotReceivedTicksByDevice[snapshot.DeviceId] = DateTime.Now.Ticks;
        // 数据新鲜度：收到实时数据即刷新时间戳（采集停滞监控依据）
        _client.MarkDataReceived();
        // 节流合并：快照是全量状态（最终一致），同设备旧帧可安全丢弃——只保留最新一帧，
        // 由 500ms 定时器在 UI 线程批量应用（替代逐条 InvokeAsync，防 Dispatcher 队列无界积压）。
        _snapshotMerger.Add(snapshot);
    }

    private void ApplySnapshot(DeviceSnapshotDto snapshot)
    {
        // tombstone：Collector 设备配置删除广播，从本地移除该设备（Runtimes 集合 + RuntimeMap）
        if (snapshot.Removed)
        {
            _lastSnapshotReceivedTicksByDevice.TryRemove(snapshot.DeviceId, out _);
            _deviceRepository.RemoveDevice(snapshot.DeviceId);
            return;
        }

        var device = _deviceRepository.GetDeviceById(snapshot.DeviceId);
        if (device is null) return; // Collector 的设备列表未与本地同步时跳过（等设备配置同步后再灌入）

        var runtime = _deviceRepository.EnsureRuntime(device);
        runtime.SyncTargetCycle(snapshot.TargetCycle);
        runtime.UpdateFromCollector(
            snapshot.OkProduction, snapshot.NgProduction, snapshot.StatusWord,
            snapshot.TotalOkProduction, snapshot.TotalNgProduction,
            snapshot.RunTime, snapshot.AlarmTime, snapshot.PausedTime, snapshot.OfflineTime,
            snapshot.OfflineCause);

        // 快照携带"当前活跃报警"（服务端权威）：以快照为准重建报警显示状态。
        // 覆盖场景：Collector 重启后事件流 Seq 归零、触发边沿不再补发，靠快照恢复仍在触发的报警；
        // 同时纠正"恢复事件丢包但报警仍显示触发中"的残留。
        var activeIds = snapshot.ActiveAlarms.Select(a => a.AlarmId).ToHashSet(StringComparer.Ordinal);
        foreach (var alarm in device.Alarms)
        {
            if (activeIds.Contains(alarm.Id))
            {
                // 快照 StartTime 缺失（default）时保留现有 StartTime，避免覆盖已恢复的正确值
                var startTime = snapshot.ActiveAlarms.First(a => a.AlarmId == alarm.Id).StartTime;
                if (startTime != default)
                    alarm.StartTime = startTime;
                alarm.EndTime = default;
            }
            else if (alarm.EndTime == default && alarm.StartTime != default)
            {
                // 快照确认已恢复：补 EndTime（不重复覆盖用户已确认的结束时间）
                alarm.EndTime = snapshot.Timestamp;
            }
        }

        foreach (var sourceSnapshot in snapshot.SourceValues)
        {
            var source = device.Sources.FirstOrDefault(item => item.Id == sourceSnapshot.SourceId);
            var value = source?.Values.FirstOrDefault(item => item.Id == sourceSnapshot.ValueId);
            if (value is null) continue;

            var runtimeTimestamp = sourceSnapshot.IsValid
                ? sourceSnapshot.LastUpdatedAt
                : snapshot.Timestamp == default
                    ? DateTime.Now
                    : snapshot.Timestamp;
            value.SetRuntimeValue(new DataSourceRuntimeValue(
                (CoreDataSourceValueType)sourceSnapshot.DataType,
                sourceSnapshot.Int32Value,
                sourceSnapshot.Float32Value,
                sourceSnapshot.BoolValue,
                sourceSnapshot.StringValue,
                sourceSnapshot.IsValid),
                runtimeTimestamp);
        }
    }

    private void OnAlarmEventReceived(AlarmEventDto evt)
    {
        // 数据新鲜度：报警事件也是实时数据信号
        _client.MarkDataReceived();
        // 更新游标：无论 UI 应用是否成功，事件已消费（防止补拉风暴）
        // Collector 重启（ServerEpoch 变化）时归零游标：新进程 Seq 从 1 重新计数，
        // 旧游标会过滤掉新进程低 Seq 的事件（漏报）；归零后从新进程起点重新订阅。
        // 每流独立纪元（见字段注释）：只在报警流自己的纪元变化时归零报警游标。
        var epoch = Volatile.Read(ref _lastAlarmEpoch);
        if (evt.ServerEpoch != epoch)
        {
            if (epoch != 0 && evt.ServerEpoch != 0)
                _logger.LogWarning("检测到采集服务重启（报警事件纪元 {Old} -> {New}），重置报警补拉游标", epoch, evt.ServerEpoch);
            Interlocked.Exchange(ref _lastAlarmEpoch, evt.ServerEpoch);
            Interlocked.Exchange(ref _lastAlarmSeq, 0);
        }
        else
        {
            Interlocked.Exchange(ref _lastAlarmSeq, evt.Seq);
        }
        // UI 应用入队（保序），由 500ms 定时器批量应用——游标已在此推进，不随 UI 延迟
        lock (_batchGate)
            _pendingAlarmEvents.Add(evt);
    }

    /// <summary>
    /// 状态转换事件（边沿流）：更新本地设备运行状态字。
    /// 快照（500ms）最终会携带同一状态，但事件流让状态变化立即反映（无需等下一帧快照）。
    /// 游标/纪元语义与报警流一致：Collector 重启（epoch 变化）时归零，避免旧游标漏掉新进程低 Seq 事件。
    /// </summary>
    private void OnStatusEventReceived(StatusEventDto evt)
    {
        // 数据新鲜度：状态事件也是实时数据信号
        _client.MarkDataReceived();
        // 每流独立纪元（见字段注释）：只在状态流自己的纪元变化时归零状态游标，
        // 不再交叉归零报警游标（报警流由自己的回调独立判定，消除共享纪元的竞态窗口）。
        var epoch = Volatile.Read(ref _lastStatusEpoch);
        if (evt.ServerEpoch != epoch)
        {
            if (epoch != 0 && evt.ServerEpoch != 0)
                _logger.LogWarning("检测到采集服务重启（状态事件纪元 {Old} -> {New}），重置状态补拉游标", epoch, evt.ServerEpoch);
            Interlocked.Exchange(ref _lastStatusEpoch, evt.ServerEpoch);
            Interlocked.Exchange(ref _lastStatusSeq, 0);
        }
        else
        {
            Interlocked.Exchange(ref _lastStatusSeq, evt.Seq);
        }
        // UI 应用入队（保序），由 500ms 定时器批量应用
        lock (_batchGate)
            _pendingStatusEvents.Add(evt);
    }

    /// <summary>
    /// 批量闸 Tick（UI 线程）：一次性应用 500ms 窗口内积压的快照（每设备最新一帧）+
    /// 报警事件 + 状态事件。先快照后事件的顺序保证：快照先恢复报警显示态，
    /// 事件边沿再覆盖最新变化（与原逐条 InvokeAsync 的先后语义一致）。
    /// </summary>
    private void FlushBatch()
    {
        var snapshots = _snapshotMerger.Drain();
        List<AlarmEventDto> alarms;
        List<StatusEventDto> statuses;
        lock (_batchGate)
        {
            alarms = _pendingAlarmEvents.Count > 0 ? new List<AlarmEventDto>(_pendingAlarmEvents) : [];
            _pendingAlarmEvents.Clear();
            statuses = _pendingStatusEvents.Count > 0 ? new List<StatusEventDto>(_pendingStatusEvents) : [];
            _pendingStatusEvents.Clear();
        }
        if (snapshots.Count == 0 && alarms.Count == 0 && statuses.Count == 0)
            return;

        foreach (var snapshot in snapshots)
        {
            try
            {
                ApplySnapshot(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "应用快照失败 Device={DeviceId}", snapshot.DeviceId);
            }
        }
        foreach (var evt in alarms)
        {
            try
            {
                ApplyAlarmEvent(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "应用报警事件失败 Alarm={AlarmId}", evt.AlarmId);
            }
        }
        foreach (var evt in statuses)
        {
            try
            {
                ApplyStatusEvent(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "应用状态事件失败 Device={DeviceId}", evt.DeviceId);
            }
        }
    }

    private void ApplyAlarmEvent(AlarmEventDto evt)
    {
        var device = _deviceRepository.GetDeviceById(evt.DeviceId);
        if (device is null) return;

        var alarm = device.Alarms.FirstOrDefault(a => a.Id == evt.AlarmId);
        if (alarm is null) return;

        switch (evt.EventType)
        {
            case AlarmEventType.Triggered:
            case AlarmEventType.ShiftChange:
                alarm.StartTime = evt.EventTime;
                alarm.EndTime = default;
                break;
            case AlarmEventType.Recovered:
                alarm.EndTime = evt.EventTime;
                break;
        }
    }

    private void ApplyStatusEvent(StatusEventDto evt)
    {
        // 状态事件只更新已存在运行状态的设备（与快照灌入同语义；设备尚未同步时跳过）
        if (!_deviceRepository.RuntimeMap.TryGetValue(evt.DeviceId, out var runtime))
            return;
        runtime.ApplyLiveStatus((int)evt.CurrentState, evt.OfflineCause);
    }

    /// <summary>
    /// 元数据推送（Collector 5s）：**增量同步**工单列表。
    /// Meta 只携带"每设备当前工单"（Running 或最新一条 Pending），是全量工单表的子集——
    /// 绝不能全量重建本地集合（会把未绑定设备的工单/多条 Pending 从 UI 清掉）。
    /// 增量语义：meta 中出现 → 服务器权威（已存在则整项替换触发 UI 通知，不存在则插入首位）；meta 未出现 → 本地保留。
    /// 局限：跨端删除的工单不会出现在 meta 中，本机无法感知（低频场景，可接受）。
    /// 工单列表绑定 UI（ObservableCollection），走 Dispatcher 封送。
    /// </summary>
    private void OnMetaReceived(MetaStateDto meta)
    {
        // 数据新鲜度：Meta 约 5s 一帧，快照增量发布后静止设备不再触发 OnSnapshot，
        // 必须由 Meta 维持 LastDataReceivedAt 前进（否则全厂静止时 WPF 10s 停滞判定误报）
        _client.MarkDataReceived();
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                var workOrders = _workOrderRepository.WorkOrders;
                // 批量作用域：N 台设备只抛 1 次 Reset，而不是每设备一次集合事件
                // （否则 ViewModel 要为每个设备全量重算一次派生计数，随设备数平方级恶化）
                using (_workOrderRepository.BeginBulkUpdate())
                {
                    foreach (var d in meta.Devices)
                    {
                        if (d.WorkOrder is null) continue;
                        var entity = WorkOrderMapper.ToEntity(d.WorkOrder);

                        // 一次线性扫描同时完成「查找 + 定位索引」，替代原先 FirstOrDefault + IndexOf 两趟
                        var idx = -1;
                        for (var i = 0; i < workOrders.Count; i++)
                        {
                            if (workOrders[i].Id == entity.Id)
                            {
                                idx = i;
                                break;
                            }
                        }

                        if (idx >= 0)
                        {
                            // 服务器权威：整项替换（与 SyncMemoryCollection 一致，触发 UI 重新读取）
                            workOrders[idx] = entity;
                        }
                        else
                        {
                            // 跨端新增（另一台 WPF/浏览器创建的工单）：按 CreatedAt 倒序约定插到首位
                            workOrders.Insert(0, entity);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "应用元数据推送失败（工单列表增量同步）");
            }
        });
    }

    private void TrackTask(Task task)
    {
        lock (_taskGate) _backgroundTasks.Add(task);
        // 故障观察必须走线程池：TrackTask 可能在 UI 线程被调用，ContinueWith 默认捕获调用
        // 线程的 SynchronizationContext —— 若该任务永不完成，续延也永远排不上队。
        _ = task.ContinueWith(
            t => _logger.LogError(t.Exception, "RemoteRuntimeSink 后台任务失败"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        // 审查修复 2026-09-05（P2）：原实现只 Add 从不移除，已完成任务被 _backgroundTasks
        // 长期引用无法回收（RefreshAsync/SubscribeSnapshotsAsync/StartEventLinkAsync 及重连循环
        // 会持续产生任务），运行天级后缓慢泄漏。完成后随即从集合移除；DisposeAsync 的 WhenAll
        // 只关心仍未完成的任务，移除已完成项不影响停机排空语义。
        _ = task.ContinueWith(
            _ =>
            {
                lock (_taskGate) _backgroundTasks.Remove(task);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        _batchTimer.Stop();
        Task[] tasks;
        lock (_taskGate) tasks = _backgroundTasks.ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { _logger.LogWarning("RemoteRuntimeSink 后台任务停止超时"); }
        await _eventsClient.DisposeAsync();
        _shutdownCts.Dispose();
        // 主连接由 KanbanDataClient 统一释放（StartupCoordinator/退出流程）
    }
}
