using Kanban.Client;
using Kanban.Contracts.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Web;

/// <summary>
/// 看板内存状态（Blazor WASM 端唯一数据源）。
/// 连接 Collector（JSON 协议）→ 拉初始快照 → 订阅快照流，全部快照按 DeviceId 存字典。
/// 渲染节流策略：快照回调（约 500ms/次）只更新内存字典，页面用 2s Timer 触发重渲染——
/// 避免 Blazor render tree 高频 diff 导致卡顿。
/// OEE 四率直接使用快照自带值（服务端 OeeCalculator 单源计算，客户端零重复计算）。
/// 附加状态：设备状态汇总、数据新鲜度、速度趋势历史、当前工单、班次进度。
/// </summary>
public sealed class DashboardState : IAsyncDisposable
{
    private readonly KanbanDataClient _client;
    private readonly KanbanDataClient _queryClient;
    private readonly ILogger<DashboardState> _logger;
    private readonly Dictionary<string, DeviceSnapshotDto> _snapshots = new();
    private readonly Dictionary<string, Queue<SpeedPoint>> _speedHistoryByDevice = new();
    private readonly object _lock = new();
    private bool _initialized;

    public DashboardState(KanbanDataClient client, ILogger<DashboardState> logger)
    {
        _client = client;
        _logger = logger;
        // 独立查询连接：WASM 上同一连接"长驻订阅 + 后续 InvokeAsync"会导致 Invoke 永久挂起
        // （服务端推送正常但客户端→服务端请求无响应，已用无头浏览器复现）。双连接绕开该问题：
        // 订阅连接只收快照推送，查询连接专职工单/班次等 Invoke 调用。
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
            // 重连成功：恢复快照订阅（游标补拉由 Collector 侧 Seq 保证，此处无需补拉快照）
            _ = SubscribeAndRefreshAsync();
        };
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

    // ──────────── 当前工单 / 班次进度（低频元数据） ────────────

    /// <summary>当前选中设备的工单（Running 优先，回退最新 Pending；null=无）。</summary>
    public WorkOrderDto? CurrentWorkOrder { get; private set; }

    /// <summary>工单拉取失败原因（供 UI 直接显示，便于定位 WASM 运行时问题）。</summary>
    public string? WorkOrderError { get; private set; }

    /// <summary>工单最后拉取时间（供页面节流判断）。</summary>
    public DateTime WorkOrderFetchedAt { get; private set; }

    /// <summary>当前班次进度。</summary>
    public ShiftProgressDto? ShiftProgress { get; private set; }

    /// <summary>班次进度拉取失败原因（供 UI 直接显示）。</summary>
    public string? ShiftError { get; private set; }

    /// <summary>班次进度最后拉取时间。</summary>
    public DateTime ShiftFetchedAt { get; private set; }

    /// <summary>元数据（工单/班次）刷新诊断：记录最近一次尝试的结果，供 UI 直接显示定位问题。</summary>
    public string MetaStatus { get; private set; } = "尚未刷新（等待首个渲染周期）";

    /// <summary>记录元数据刷新诊断（成功/跳过/失败原因）。</summary>
    private void SetMetaStatus(string status) => MetaStatus = $"{DateTime.Now:HH:mm:ss} {status}";

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
            // 独立查询连接：与订阅连接分开，避免 WASM 上 InvokeAsync 挂起
            await _queryClient.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "连接 Collector 失败，看板显示离线状态（请确认 Collector 已启动且端口一致）");
            _initialized = false; // 允许页面定时器下轮重试
            return;
        }
        await SubscribeAndRefreshAsync();
    }

    /// <summary>刷新当前选中设备的工单（页面按节流周期调用；设备切换时立即调用）。</summary>
    public async Task RefreshWorkOrderAsync(string? deviceId)
    {
        _logger.LogInformation("RefreshWorkOrder 进入 Device={DeviceId} IsConnected={IsConnected}", deviceId, IsConnected);
        if (!IsConnected)
        {
            SetMetaStatus($"跳过工单刷新（IsConnected=false，连接尚未就绪）");
            return;
        }
        if (string.IsNullOrEmpty(deviceId))
        {
            SetMetaStatus($"跳过工单刷新（deviceId 为空）");
            return;
        }
        try
        {
            // 6s 超时兜底：WASM 上 InvokeAsync 曾有永久挂起（双连接已绕开，超时仅作保险）
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await _queryClient.GetCurrentWorkOrderAsync(deviceId, cts.Token);
            CurrentWorkOrder = result;
            WorkOrderError = null;
            SetMetaStatus($"工单刷新成功（{deviceId}）");
        }
        catch (Exception ex)
        {
            WorkOrderError = ex.Message;
            SetMetaStatus($"工单刷新失败：{ex.Message}");
            _logger.LogWarning(ex, "拉取当前工单失败 Device={DeviceId}", deviceId);
        }
        WorkOrderFetchedAt = DateTime.Now;
    }

    /// <summary>刷新班次进度（页面按节流周期调用）。</summary>
    public async Task RefreshShiftAsync()
    {
        if (!IsConnected)
        {
            SetMetaStatus($"跳过班次刷新（IsConnected=false，连接尚未就绪）");
            return;
        }
        try
        {
            ShiftProgress = await _queryClient.GetShiftProgressAsync();
            ShiftError = null;
            SetMetaStatus($"班次刷新成功（{ShiftProgress?.Name}）");
        }
        catch (Exception ex)
        {
            ShiftError = ex.Message;
            SetMetaStatus($"班次刷新失败：{ex.Message}");
            _logger.LogWarning(ex, "拉取班次进度失败");
        }
        ShiftFetchedAt = DateTime.Now;
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

    /// <summary>订阅快照流 + 拉取一次当前全量快照（覆盖 Collector 重启导致的内存清空）。</summary>
    private async Task SubscribeAndRefreshAsync()
    {
        _ = _client.SubscribeSnapshotsAsync(); // 长驻调用，fire-and-forget
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

    public async ValueTask DisposeAsync()
    {
        await _queryClient.DisposeAsync();
        await _client.DisposeAsync();
    }
}

/// <summary>速度趋势点（时间 + 实时速度 件/小时）。</summary>
public sealed record SpeedPoint(DateTime Time, double Speed);
