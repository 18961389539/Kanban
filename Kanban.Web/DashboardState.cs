using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Microsoft.Extensions.Logging;

namespace Kanban.Web;

/// <summary>
/// 看板内存状态（Blazor WASM 端唯一数据源）。
/// 双连接架构（WASM 约束：每连接最多一个长驻订阅，禁止"长驻+Invoke"/"多长驻"混用——均有实测问题）：
///   _client      订阅连接：唯一长驻 = 快照推送（OnSnapshot）
///   _metaClient  元数据连接：唯一长驻 = Meta 推送（OnMeta，工单/班次）
///   _invokeClient 查询连接：无长驻，专做历史查询等 Invoke（懒连接，首次调用才建）
/// 渲染节流：推送回调（500ms 快照 / 5s 元数据）只更新内存字典，页面用 2s Timer 触发重渲染。
/// OEE 四率直接使用快照自带值（服务端 OeeCalculator 单源计算，客户端零重复计算）。
/// 缓存策略：排序快照列表（设备增删失效）、状态汇总（状态变化脏标记）——避免每帧全量重算。
/// </summary>
public sealed class DashboardState : IAsyncDisposable
{
    private readonly KanbanDataClient _client;
    private readonly KanbanDataClient _metaClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DashboardState> _logger;
    private readonly Dictionary<string, DeviceSnapshotDto> _snapshots = new();
    private readonly Dictionary<string, Queue<SpeedPoint>> _speedHistoryByDevice = new();
    private readonly Dictionary<string, WorkOrderDto?> _workOrdersByDevice = new();
    private readonly object _lock = new();
    private IReadOnlyList<DeviceSnapshotDto>? _sortedSnapshotsCache;
    private bool _sortedSnapshotsDirty = true;
    private bool _statusSummaryDirty = true;
    private DeviceSnapshotStatusSummary? _statusSummaryCache;
    private ShiftProgressDto? _shiftProgress;
    private KanbanDataClient? _invokeClient;
    private bool _initialized;
    private CancellationTokenSource? _retryCts;
    private DateTime _invokeFailedUntil;

    public DashboardState(
        KanbanDataClient client,
        ILoggerFactory loggerFactory,
        ILogger<DashboardState> logger)
    {
        _client = client;
        _loggerFactory = loggerFactory;
        _logger = logger;
        // 元数据连接：与订阅连接分开（每连接单长驻订阅约束），专职工单/班次推送
        _metaClient = new KanbanDataClient(client.HubUrl, loggerFactory.CreateLogger<KanbanDataClient>(), useMessagePack: false);
        _client.ConnectionStateChanged += (_, connected) =>
        {
            IsConnected = connected;
            if (connected) LastConnectedAt = DateTime.Now;
            StateChanged?.Invoke();
        };
        _client.Reconnecting += (_, _) => StateChanged?.Invoke();
        _client.Reconnected += (_, _) =>
        {
            // 重连成功：恢复快照订阅（游标补拉由 Collector 侧 Seq 保证）
            _ = SubscribeAndRefreshAsync();
        };
        // 元数据连接重连成功后：重新订阅 Meta（长驻订阅随连接断开而结束）
        _metaClient.Reconnected += (_, _) => _ = SubscribeMetaSafeAsync();
    }

    /// <summary>连接状态变化通知（UI 刷新连接指示器）。</summary>
    public event Action? StateChanged;

    /// <summary>当前是否已连接 Collector。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>最近一次连接成功时间。</summary>
    public DateTime? LastConnectedAt { get; private set; }

    /// <summary>Collector 服务端版本（版本握手获取；失败/未获取时 null）。用于升级兼容性校验与展示。</summary>
    public string? ServerVersion { get; private set; }

    // ──────────── 数据新鲜度（统一走 KanbanDataClient，快照回调时 MarkDataReceived） ────────────

    /// <summary>最后一次收到实时数据（快照）的时间；null=尚未收到。</summary>
    public DateTime? LastDataAt
    {
        get
        {
            var t = _client.LastDataReceivedAt;
            return t == default ? null : t;
        }
    }

    // ──────────── 低频元数据（Collector 5s 推送，非轮询） ────────────

    /// <summary>当前班次进度（Collector 推送，5s 更新一次）。</summary>
    public ShiftProgressDto? ShiftProgress
    {
        get { lock (_lock) return _shiftProgress; }
    }

    /// <summary>指定设备当前工单（Running 优先回退最新 Pending；null=无工单或尚未收到推送）。</summary>
    public WorkOrderDto? GetWorkOrder(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        lock (_lock)
            return _workOrdersByDevice.TryGetValue(deviceId, out var wo) ? wo : null;
    }

    // ──────────── 快照访问（带缓存） ────────────

    /// <summary>设备总数（快照字典大小）。</summary>
    public int DeviceCount
    {
        get { lock (_lock) return _snapshots.Count; }
    }

    /// <summary>全部设备快照（按设备名排序，设备集合变化时才重排）。</summary>
    public IReadOnlyList<DeviceSnapshotDto> Snapshots
    {
        get
        {
            lock (_lock)
            {
                if (_sortedSnapshotsDirty || _sortedSnapshotsCache is null)
                {
                    _sortedSnapshotsCache = _snapshots.Values.OrderBy(s => s.DeviceName).ToList();
                    _sortedSnapshotsDirty = false;
                }
                return _sortedSnapshotsCache;
            }
        }
    }

    /// <summary>设备状态汇总（状态变化时脏标记重算，状态不变时复用缓存）。</summary>
    public DeviceSnapshotStatusSummary StatusSummary
    {
        get
        {
            lock (_lock)
            {
                if (!_statusSummaryDirty && _statusSummaryCache is not null)
                    return _statusSummaryCache;
                var s = new DeviceSnapshotStatusSummary();
                foreach (var snap in _snapshots.Values)
                {
                    switch (snap.Status)
                    {
                        case DeviceStatus.Running: s.Running++; break;
                        case DeviceStatus.Alarm: s.Alarm++; break;
                        case DeviceStatus.Paused: s.Paused++; break;
                        default: s.Idle++; break;
                    }
                }
                s.Total = _snapshots.Count;
                _statusSummaryCache = s;
                _statusSummaryDirty = false;
                return s;
            }
        }
    }

    /// <summary>指定设备最新快照。</summary>
    public DeviceSnapshotDto? GetSnapshot(string deviceId)
    {
        lock (_lock)
            return _snapshots.TryGetValue(deviceId, out var s) ? s : null;
    }

    /// <summary>指定设备的速度趋势点（时间升序，客户端按 500ms 快照采样，最多保留 120 点 ≈ 1 分钟）。</summary>
    public IReadOnlyList<SpeedPoint> GetSpeedHistory(string deviceId)
    {
        lock (_lock)
            return _speedHistoryByDevice.TryGetValue(deviceId, out var q) ? q.ToList() : [];
    }

    // ──────────── 查询连接（懒连接，历史查询等 Invoke 专用，无长驻订阅） ────────────

    /// <summary>历史查询（走独立查询连接；懒连接——首次调用才建立，不占用任何长驻订阅连接）。
    /// 连接失败后 30s 内快速失败（避免每次点击都等 10s 连接超时，页面像死机）。</summary>
    public async Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request, CancellationToken ct = default)
    {
        if (DateTime.Now < _invokeFailedUntil)
            throw new InvalidOperationException("查询连接暂不可用（上次连接失败），请稍后重试");
        var client = GetInvokeClient();
        try
        {
            if (!client.IsConnected)
                await client.ConnectAsync(ct);
            return await client.QueryHistoryAsync(request, ct);
        }
        catch
        {
            _invokeFailedUntil = DateTime.Now.AddSeconds(30);
            throw;
        }
    }

    private KanbanDataClient GetInvokeClient()
    {
        lock (_lock)
        {
            return _invokeClient ??= new KanbanDataClient(
                _client.HubUrl, _loggerFactory.CreateLogger<KanbanDataClient>(), useMessagePack: false);
        }
    }

    // ──────────── 连接与订阅 ────────────

    /// <summary>建立连接并启动订阅（幂等，可安全重入；失败自动进入 5s 间隔内部重试循环，Collector 晚启动也能自愈）。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            await _client.ConnectAsync();
            // 回调注册必须在连接建立之后（KanbanDataClient.On* 依赖 _connection 已创建）
            _client.OnSnapshot(OnSnapshotReceived);
            await _metaClient.ConnectAsync();
            _metaClient.OnMeta(OnMetaReceived);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "连接 Collector 失败，看板显示离线状态（5s 后自动重试，请确认 Collector 已启动且端口一致）");
            _initialized = false; // 允许重试
            ScheduleRetry();
            return;
        }
        await SubscribeAndRefreshAsync();
    }

    /// <summary>
    /// 失败重试调度：Collector 晚启动/重启时，页面不依赖用户手动刷新即可自动连上。
    /// 保证只存在一个重试循环（_retryCts 非空且未取消则跳过）。
    /// </summary>
    private void ScheduleRetry()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_retryCts is { IsCancellationRequested: false }) return;
            cts = _retryCts = new CancellationTokenSource();
        }
        _ = RetryLoopAsync(cts);
    }

    private async Task RetryLoopAsync(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // Dispose 停止重试
            }
            try
            {
                await InitializeAsync();
                // 双连接均连通才算成功（InitializeAsync 成功路径已订阅快照/元数据）
                if (_client.IsConnected && _metaClient.IsConnected)
                {
                    lock (_lock)
                    {
                        if (ReferenceEquals(_retryCts, cts)) _retryCts = null;
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "连接重试异常（InitializeAsync 内部已 catch，此处兜底）");
            }
        }
    }

    /// <summary>快照回调：更新内存字典 + 数据新鲜度 + 速度趋势历史 + 汇总脏标记（不触达 UI）。</summary>
    private void OnSnapshotReceived(DeviceSnapshotDto snapshot)
    {
        // tombstone：Collector 设备配置删除广播，从内存移除该设备（快照流只有 upsert 语义，删除须显式表达）
        if (snapshot.Removed)
        {
            lock (_lock)
            {
                if (_snapshots.Remove(snapshot.DeviceId))
                {
                    _sortedSnapshotsDirty = true;
                    _statusSummaryDirty = true;
                }
            }
            _client.MarkDataReceived();
            return;
        }

        lock (_lock)
        {
            var isNew = !_snapshots.TryGetValue(snapshot.DeviceId, out var old);
            _snapshots[snapshot.DeviceId] = snapshot;
            if (isNew)
            {
                _sortedSnapshotsDirty = true;
                _statusSummaryDirty = true;
            }
            else
            {
                if (old!.Status != snapshot.Status)
                    _statusSummaryDirty = true;
                // 设备重命名（同 Id 换名）：排序缓存按 DeviceName，须一并失效
                if (old!.DeviceName != snapshot.DeviceName)
                    _sortedSnapshotsDirty = true;
            }

            // 速度点：总产量 / 运行小时（RunTime >= 5s 才记，避免启动失真；口径与 WPF HomeViewModel 一致）
            double speed = snapshot.RunTime >= 5
                ? (snapshot.TotalOkProduction + snapshot.TotalNgProduction) / (snapshot.RunTime / 3600.0)
                : 0;
            if (!_speedHistoryByDevice.TryGetValue(snapshot.DeviceId, out var queue))
            {
                queue = new Queue<SpeedPoint>(121);
                _speedHistoryByDevice[snapshot.DeviceId] = queue;
            }
            queue.Enqueue(new SpeedPoint(DateTime.Now, speed));
            while (queue.Count > 120) queue.Dequeue();
        }
        _client.MarkDataReceived(); // 统一数据新鲜度来源
    }

    /// <summary>元数据回调（Collector 约 5s 推送）：全量替换工单缓存 + 更新班次（设备删除不留残留）。</summary>
    private void OnMetaReceived(MetaStateDto meta)
    {
        // 数据新鲜度：Meta 约 5s 一帧，快照增量发布后静止设备不再触发 OnSnapshot，
        // 必须由 Meta 维持 LastDataAt 前进（否则全厂静止时"数据更新"时间戳卡住）
        _client.MarkDataReceived();
        lock (_lock)
        {
            _workOrdersByDevice.Clear();
            foreach (var d in meta.Devices)
                _workOrdersByDevice[d.DeviceId] = d.WorkOrder;
            _shiftProgress = meta.Shift;
        }
    }

    /// <summary>订阅快照流 + 元数据流 + 拉取一次当前全量快照（覆盖 Collector 重启导致的内存清空）。
    /// 全量拉取采用**替换**语义（清空后写入）：服务端返回的就是当前完整设备集，
    /// 配合 tombstone 保证设备删除后本机收敛（只 upsert 会导致被删设备永远残留）。
    ///
    /// ⚠️ 顺序约束（重要）：SignalR 服务端对同一连接**按序处理 invocation**（DefaultHubDispatcher
    /// 顺序 dispatch）——长驻订阅（SubscribeSnapshotsAsync/SubscribeMetaAsync）一旦发出，
    /// 同连接的后续 Invoke（GetCurrentSnapshotsAsync/GetServerVersionAsync 等）会**无限排队**。
    /// 因此所有"客户端→服务端"Invoke 必须在发起订阅**之前**完成（实测复现并锁定于
    /// KanbanDataClientIntegrationTests.ConcurrentInvoke_WhileLongRunningSubscribePending_StillWorks）。</summary>
    private async Task SubscribeAndRefreshAsync()
    {
        // ① 先做 Invoke（连接空闲，不会被长驻订阅阻塞）
        try
        {
            var snapshots = await _client.GetCurrentSnapshotsAsync();
            lock (_lock)
            {
                _snapshots.Clear();
                foreach (var s in snapshots)
                {
                    _snapshots[s.DeviceId] = s;
                }
                _sortedSnapshotsDirty = true;
                _statusSummaryDirty = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取初始快照失败（等待订阅推送）");
        }
        // 版本握手（升级兼容性观测）：失败不阻断看板（LogWarning 便于冒烟/运维诊断）
        try
        {
            ServerVersion = await _client.GetServerVersionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取服务端版本失败（忽略）");
        }

        // ② 最后发起长驻订阅（Invoke 全部完成后，避免占线阻塞——见方法注释的顺序约束）
        _ = SubscribeSnapshotsSafeAsync(); // 长驻调用，fire-and-forget（包装避免 fault 未观察触发 Blazor 错误 UI）
        _ = SubscribeMetaSafeAsync();      // 元数据订阅走元数据连接（每连接单长驻订阅约束）
    }

    /// <summary>快照订阅包装：长驻 Invoke 在连接断开时会 fault（属正常生命周期），观察异常防全局错误 UI。</summary>
    private async Task SubscribeSnapshotsSafeAsync()
    {
        try
        {
            await _client.SubscribeSnapshotsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "快照订阅结束（连接断开/重连触发，属正常）");
        }
    }

    /// <summary>元数据订阅包装（元数据连接上长驻；断开/重连自动重订阅，观察 fault 防未观察异常）。</summary>
    private async Task SubscribeMetaSafeAsync()
    {
        try
        {
            await _metaClient.SubscribeMetaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "元数据订阅结束（连接断开/重连触发，属正常）");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _retryCts = null;
        if (_invokeClient is not null)
            await _invokeClient.DisposeAsync();
        await _metaClient.DisposeAsync();
        await _client.DisposeAsync();
    }
}

/// <summary>设备状态汇总（可变计数，Home 汇总条一次取用）。</summary>
public sealed class DeviceSnapshotStatusSummary
{
    public int Running { get; set; }
    public int Alarm { get; set; }
    public int Paused { get; set; }
    public int Idle { get; set; }
    public int Total { get; set; }
}

/// <summary>速度趋势点（时间 + 实时速度 件/小时）。</summary>
public sealed record SpeedPoint(DateTime Time, double Speed);
