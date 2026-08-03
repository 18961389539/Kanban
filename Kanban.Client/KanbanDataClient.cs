using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kanban.Client;

/// <summary>
/// Kanban.Collector SignalR 客户端（共享库，WPF 与 Blazor WASM 展示端共用）。
/// 管理连接生命周期：指数退避重连、快照/事件订阅、历史查询。
/// 不依赖任何 UI/WPF 类型：桌面端（MainAPP）以 <c>useMessagePack: true</c> 使用 MessagePack 协议，
/// 浏览器端（Blazor WASM）以 <c>useMessagePack: false</c> 使用默认 JSON 协议（Collector 双协议并存）。
/// </summary>
public sealed class KanbanDataClient : IAsyncDisposable
{
    private readonly string _hubUrl;
    private readonly bool _useMessagePack;
    private readonly ILogger<KanbanDataClient> _logger;
    private HubConnection? _connection;
    private CancellationTokenSource? _reconnectCts;
    private int _consecutiveFailures;
    // 连接建立互斥：并发 ConnectAsync 时串行化，防止各自建连接互相覆盖 _connection（旧连接泄漏）
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    public KanbanDataClient(string hubUrl, ILogger<KanbanDataClient> logger, bool useMessagePack = true)
    {
        _hubUrl = hubUrl;
        _useMessagePack = useMessagePack;
        _logger = logger;
    }

    /// <summary>连接状态变化事件（IsConnected = SignalR 传输层状态）</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>进入自动重连阶段事件（SignalR WithAutomaticReconnect 触发，UI 可据此显示"重连中"）</summary>
    public event EventHandler? Reconnecting;

    /// <summary>自动重连成功事件（订阅方可恢复快照/按游标补拉事件）</summary>
    public event EventHandler? Reconnected;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>Hub 地址（供创建同地址的独立查询连接，如 WASM 端双连接架构）。</summary>
    public string HubUrl => _hubUrl;

    public int ConsecutiveFailures => _consecutiveFailures;

    // ──────────── 数据新鲜度（采集停滞监控） ────────────

    private DateTime _lastDataReceivedAt;
    private readonly object _dataLock = new();

    /// <summary>最后一次收到实时数据（快照/事件）的时间。default 表示尚未收到。</summary>
    public DateTime LastDataReceivedAt
    {
        get { lock (_dataLock) return _lastDataReceivedAt; }
    }

    /// <summary>收到实时数据时由数据消费者调用，刷新数据新鲜度时间戳。</summary>
    public void MarkDataReceived()
    {
        lock (_dataLock) _lastDataReceivedAt = DateTime.Now;
    }

    /// <summary>
    /// 建立连接并启动订阅。内部自动断线重连（指数退避 1s→30s，与 PlcConnectionManager 策略一致）；
    /// 自动重连耗尽后由 Closed 处理器进入后台循环重连（5s 间隔），实现"永久自愈"。
    /// 连接超时 10s：Collector 不可达时快速失败（WASM 端由上层按节流重试），避免长时间挂起。
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // 并发保护：同一实例的并发 ConnectAsync 串行化（double-check 已连接则直接返回），
        // 避免多个调用各自建连接、后者覆盖 _connection 导致前者泄漏。
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { State: HubConnectionState.Connected }) return;

            _reconnectCts?.Dispose();
            _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var builder = new HubConnectionBuilder()
                .WithUrl(_hubUrl)
                // 与 Collector 服务端一致：MessagePack 二进制序列化（需两端同时启用）；WASM 端走默认 JSON
                .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) });
            if (_useMessagePack)
                builder.AddMessagePackProtocol();
            _connection = builder.Build();

            _connection.Reconnecting += _ =>
            {
                _logger.LogWarning("Collector 连接断开，正在重连...");
                Interlocked.Increment(ref _consecutiveFailures);
                // 重连中：连接状态已非 Connected，通知 UI 徽标切换（否则断线期间仍显示"实时"误导）
                ConnectionStateChanged?.Invoke(this, false);
                Reconnecting?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            };
            _connection.Reconnected += _ =>
            {
                _logger.LogInformation("Collector 重连成功");
                _consecutiveFailures = 0;
                ConnectionStateChanged?.Invoke(this, true);
                Reconnected?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            };
            _connection.Closed += async ex =>
            {
                // WithAutomaticReconnect 全部耗尽后触发：进入后台循环重连（5s 间隔），
                // 直至成功或连接被显式释放。否则 Collector 短暂不可用后页面将永久离线。
                _logger.LogWarning(ex, "Collector 自动重连已耗尽，进入后台循环重连");
                ConnectionStateChanged?.Invoke(this, false);
                var cts = _reconnectCts;
                while (cts is { IsCancellationRequested: false })
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                        await _connection.StartAsync(cts.Token);
                        _logger.LogInformation("Collector 后台重连成功");
                        _consecutiveFailures = 0;
                        ConnectionStateChanged?.Invoke(this, true);
                        Reconnected?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        return; // 连接被释放，停止重连
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogWarning(retryEx, "Collector 后台重连失败，5s 后重试");
                    }
                }
            };

            try
            {
                // 10s 连接超时：Collector 不可达时快速失败（超时抛 OperationCanceledException）
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                await _connection.StartAsync(timeoutCts.Token);
                _consecutiveFailures = 0;
                _logger.LogInformation("已连接 Collector {Url}", _hubUrl);
                ConnectionStateChanged?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "连接 Collector 失败 {Url}", _hubUrl);
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>校验连接已建立，未建立时抛带说明的异常（替代 _connection! 的 NullReferenceException）。</summary>
    private void EnsureConnected()
    {
        if (_connection is not { State: HubConnectionState.Connected })
            throw new InvalidOperationException("SignalR 连接尚未建立：请先调用 ConnectAsync 并等待成功（回调注册同理）。");
    }

    /// <summary>校验连接已建立（供依赖连接状态的服务端调用使用）。</summary>
    public void EnsureConnectionEstablished() => EnsureConnected();

    // ──────────── 强类型回调注册（由数据消费者调用，须在连接建立后） ────────────

    public void OnSnapshot(Action<DeviceSnapshotDto> handler)
    {
        EnsureConnected();
        _connection!.On<DeviceSnapshotDto>(nameof(IKanbanHubClient.OnSnapshot), handler);
    }

    public void OnAlarmEvent(Action<AlarmEventDto> handler)
    {
        EnsureConnected();
        _connection!.On<AlarmEventDto>(nameof(IKanbanHubClient.OnAlarmEvent), handler);
    }

    public void OnStatusEvent(Action<StatusEventDto> handler)
    {
        EnsureConnected();
        _connection!.On<StatusEventDto>(nameof(IKanbanHubClient.OnStatusEvent), handler);
    }

    public void OnMeta(Action<MetaStateDto> handler)
    {
        EnsureConnected();
        _connection!.On<MetaStateDto>(nameof(IKanbanHubClient.OnMeta), handler);
    }

    // ──────────── 服务端调用（均前置校验连接，未连接抛带说明的 InvalidOperationException） ────────────

    public async Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<IReadOnlyList<DeviceSnapshotDto>>(
            nameof(IKanbanHubServer.GetCurrentSnapshotsAsync), ct);
    }

    public async Task SubscribeSnapshotsAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanHubServer.SubscribeSnapshotsAsync), ct);
    }

    public async Task SubscribeAlarmEventsAsync(long afterSeq, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanHubServer.SubscribeAlarmEventsAsync), afterSeq, ct);
    }

    public async Task SubscribeStatusEventsAsync(long afterSeq, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanHubServer.SubscribeStatusEventsAsync), afterSeq, ct);
    }

    public async Task SubscribeMetaAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanHubServer.SubscribeMetaAsync), ct);
    }

    public async Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<HistoryQueryResponse>(
            nameof(IKanbanHubServer.QueryHistoryAsync), request, ct);
    }

    /// <summary>拉取 Collector 运行诊断快照（运行监控页 Remote 模式）。</summary>
    public async Task<CollectorDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CollectorDiagnosticsDto>("GetDiagnosticsAsync", ct);
    }

    /// <summary>同步设备配置到 Collector 落盘（Remote 模式设备管理保存）。</summary>
    public async Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanHubServer.SaveDevicesAsync), devices, ct);
    }

    /// <summary>从 Collector 拉取设备配置（Remote 模式屏端零配置，不依赖本地 devices.json）。</summary>
    public async Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<IReadOnlyList<DeviceConfigDto>>(nameof(IKanbanHubServer.GetDevicesAsync), ct);
    }

    /// <summary>新增/更新工单到 Collector 落库，返回带 Id 的结果。</summary>
    public async Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<WorkOrderDto>(nameof(IKanbanHubServer.UpsertWorkOrderAsync), workOrder, ct);
    }

    /// <summary>删除工单（Collector 落库）。</summary>
    public async Task DeleteWorkOrderAsync(int workOrderId, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanHubServer.DeleteWorkOrderAsync), workOrderId, ct);
    }

    /// <summary>查询设备当前工单（Running 优先，无则回退最新 Pending；无工单返回 null）。</summary>
    public async Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<WorkOrderDto?>(nameof(IKanbanHubServer.GetCurrentWorkOrderAsync), deviceId, ct);
    }

    /// <summary>查询当前班次进度。</summary>
    public async Task<ShiftProgressDto> GetShiftProgressAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<ShiftProgressDto>(nameof(IKanbanHubServer.GetShiftProgressAsync), ct);
    }

    /// <summary>同步采集设置到 Collector 落盘并热生效（Remote 模式设置页保存）。</summary>
    public async Task SaveCollectorSettingsAsync(CollectorSettingsDto settings, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanHubServer.SaveCollectorSettingsAsync), settings, ct);
    }

    /// <summary>服务端版本握手（Collector 程序集信息版本；用于升级兼容性校验）。</summary>
    public async Task<string> GetServerVersionAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string>(nameof(IKanbanHubServer.GetServerVersionAsync), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _reconnectCts?.Cancel();
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "释放 HubConnection 异常"); }
        }
        _reconnectCts?.Dispose();
    }
}
