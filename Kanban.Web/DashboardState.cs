using Kanban.Client;
using Kanban.Contracts.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Web;

/// <summary>
/// 看板内存状态（Blazor WASM 端唯一数据源）。
/// 双连接架构（WASM 约束）：订阅连接只收推送（快照/事件/元数据），查询连接只做 Invoke——
/// 同一连接"长驻订阅 + 后续 InvokeAsync"在 WASM 上会导致 Invoke 永久挂起（已复现确认）。
/// 渲染节流：推送回调（500ms 快照 / 5s 元数据）只更新内存字典，页面用 2s Timer 触发重渲染。
/// OEE 四率直接使用快照自带值（服务端 OeeCalculator 单源计算，客户端零重复计算）。
/// 工单/班次由 Collector 低频推送（OnMeta），客户端零轮询、零 Invoke。
/// </summary>
public sealed class DashboardState : IAsyncDisposable
{
    private readonly KanbanDataClient _client;
    private readonly KanbanDataClient _queryClient;
    private readonly ILogger<DashboardState> _logger;
    private readonly Dictionary<string, DeviceSnapshotDto> _snapshots = new();
    private readonly Dictionary<string, Queue<SpeedPoint>> _speedHistoryByDevice = new();
    private readonly Dictionary<string, WorkOrderDto?> _workOrdersByDevice = new();
    private readonly object _lock = new();
    private bool _initialized;

    public DashboardState(KanbanDataClient client, ILogger<DashboardState> logger)
    {
        _client = client;
        _logger = logger;
        // 双连接架构（WASM 约束）：每连接最多一个长驻订阅，禁止"长驻订阅 + 后续 Invoke"混用
        // （WASM 上会永久挂起、桌面端会严重延迟——已复现）。
        // 订阅连接：快照推送（OnSnapshot）；查询连接：元数据订阅（OnMeta）+ 未来历史查询 Invoke。
        _queryClient = new KanbanDataClient(client.HubUrl, NullLogger<KanbanDataClient>.Instance, useMessagePack: false);
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
        // 查询连接重连成功后：重新订阅元数据（长驻订阅随连接断开而结束）
        _queryClient.Reconnected += (_, _) => _ = SubscribeMetaSafeAsync();
    }

    /// <summary>连接状态变化通知（UI 刷新连接指示器）。</summary>
    public event Action? StateChanged;

    /// <summary>当前是否已连接 Collector。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>最近一次连接成功时间。</summary>
    public DateTime? LastConnectedAt { get; private set; }

    // ──────────── 数据新鲜度 ────────────

    private DateTime? _lastDataAt;

    /// <summary>最后一次收到实时数据（快照）的时间。</summary>
    public DateTime? LastDataAt
    {
        get { lock (_lock) return _lastDataAt; }
    }

    // ──────────── 低频元数据（Collector 5s 推送，非轮询） ────────────

    private ShiftProgressDto? _shiftProgress;

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

    /// <summary>设备总数（快照字典大小）。</summary>
    public int DeviceCount => _snapshots.Count;

    /// <summary>全部设备快照（按设备名排序）。</summary>
    public IReadOnlyList<DeviceSnapshotDto> Snapshots
    {
        get
        {
            lock (_lock)
                return _snapshots.Values.OrderBy(s => s.DeviceName).ToList();
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

    /// <summary>建立连接并启动订阅（幂等，可安全重入；失败后自动复位允许下次重试）。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            await _client.ConnectAsync();
            // 回调注册必须在连接建立之后（KanbanDataClient.On* 依赖 _connection 已创建）
            _client.OnSnapshot(OnSnapshotReceived);
            await _queryClient.ConnectAsync();
            // 元数据推送走查询连接（每连接单长驻订阅约束），回调注册在其连接上
            _queryClient.OnMeta(OnMetaReceived);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "连接 Collector 失败，看板显示离线状态（请确认 Collector 已启动且端口一致）");
            _initialized = false; // 允许页面定时器下轮重试
            return;
        }
        await SubscribeAndRefreshAsync();
    }

    /// <summary>快照回调：更新内存字典 + 数据新鲜度 + 速度趋势历史（不触达 UI，渲染节流由页面 Timer 负责）。</summary>
    private void OnSnapshotReceived(DeviceSnapshotDto snapshot)
    {
        lock (_lock)
        {
            _snapshots[snapshot.DeviceId] = snapshot;
            _lastDataAt = DateTime.Now;

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
    }

    /// <summary>元数据回调（Collector 约 5s 推送）：更新全部设备工单缓存 + 班次进度。</summary>
    private void OnMetaReceived(MetaStateDto meta)
    {
        lock (_lock)
        {
            foreach (var d in meta.Devices)
                _workOrdersByDevice[d.DeviceId] = d.WorkOrder;
            _shiftProgress = meta.Shift;
        }
    }

    /// <summary>订阅快照流 + 元数据流 + 拉取一次当前全量快照（覆盖 Collector 重启导致的内存清空）。</summary>
    private async Task SubscribeAndRefreshAsync()
    {
        _ = SubscribeSnapshotsSafeAsync(); // 长驻调用，fire-and-forget（包装避免 fault 未观察触发 Blazor 错误 UI）
        _ = SubscribeMetaSafeAsync();      // 元数据订阅走查询连接（每连接单长驻订阅约束）
        try
        {
            var snapshots = await _client.GetCurrentSnapshotsAsync();
            lock (_lock)
                foreach (var s in snapshots)
                    _snapshots[s.DeviceId] = s;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取初始快照失败（等待订阅推送）");
        }
    }

    /// <summary>
    /// 快照订阅包装：长驻 Invoke 在连接断开时会 fault（属正常生命周期），
    /// 必须观察异常，否则 fire-and-forget 的未观察 Task 会触发 Blazor 全局错误 UI。
    /// </summary>
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

    /// <summary>元数据订阅包装（查询连接上长驻；断开/重连自动重订阅，观察 fault 防止未观察异常）。</summary>
    private async Task SubscribeMetaSafeAsync()
    {
        try
        {
            await _queryClient.SubscribeMetaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "元数据订阅结束（连接断开/重连触发，属正常）");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _queryClient.DisposeAsync();
        await _client.DisposeAsync();
    }
}

/// <summary>速度趋势点（时间 + 实时速度 件/小时）。</summary>
public sealed record SpeedPoint(DateTime Time, double Speed);
