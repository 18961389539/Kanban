using Kanban.Client;
using Kanban.Contracts.Dtos;
using Microsoft.Extensions.Logging;

namespace Kanban.Web;

/// <summary>
/// 看板内存状态（Blazor WASM 端唯一数据源）。
/// 连接 Collector（JSON 协议）→ 拉初始快照 → 订阅快照流，全部快照按 DeviceId 存字典。
/// 渲染节流策略：快照回调（约 500ms/次）只更新内存字典，页面用 1s Timer 触发重渲染——
/// 避免 Blazor render tree 以 500ms 频率全量 diff 导致卡顿。
/// OEE 四率直接使用快照自带值（服务端 OeeCalculator 单源计算，客户端零重复计算）。
/// </summary>
public sealed class DashboardState : IAsyncDisposable
{
    private readonly KanbanDataClient _client;
    private readonly ILogger<DashboardState> _logger;
    private readonly Dictionary<string, DeviceSnapshotDto> _snapshots = new();
    private readonly object _lock = new();
    private bool _initialized;

    public DashboardState(KanbanDataClient client, ILogger<DashboardState> logger)
    {
        _client = client;
        _logger = logger;
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

    /// <summary>建立连接并启动订阅（幂等，可安全重入；失败后自动复位允许下次重试）。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // 回调注册必须在连接建立之后（KanbanDataClient.On* 依赖 _connection）
        _client.OnSnapshot(OnSnapshotReceived);
        try
        {
            await _client.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "连接 Collector 失败，看板显示离线状态（请确认 Collector 已启动且端口一致）");
            _initialized = false; // 允许页面定时器下轮重试
            return;
        }
        await SubscribeAndRefreshAsync();
    }

    /// <summary>快照回调：仅更新内存字典（不触达 UI，渲染节流由页面 Timer 负责）。</summary>
    private void OnSnapshotReceived(DeviceSnapshotDto snapshot)
    {
        lock (_lock) _snapshots[snapshot.DeviceId] = snapshot;
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

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}
