using Kanban.Contracts.Abstractions;
using Kanban.Contracts.Dtos;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Serilog;

namespace MainAPP.Services;

/// <summary>
/// Kanban.Collector SignalR 客户端（Remote 模式数据源）。
/// 管理连接生命周期：指数退避重连、快照/事件订阅、历史查询。
/// ViewModel 不直接依赖本类，而是通过 <see cref="RemoteRuntimeSink"/>
/// 把快照灌回 DeviceRepository.Runtimes，保持 UI 层绑定关系不变。
/// </summary>
public sealed class KanbanDataClient : IAsyncDisposable
{
    private readonly string _hubUrl;
    private readonly ILogger<KanbanDataClient> _logger;
    private HubConnection? _connection;
    private CancellationTokenSource? _reconnectCts;
    private int _consecutiveFailures;

    public KanbanDataClient(AppSettings appSettings, ILogger<KanbanDataClient> logger)
    {
        _hubUrl = appSettings.CollectorHubUrl;
        _logger = logger;
    }

    /// <summary>连接状态变化事件（IsConnected = SignalR 传输层状态）</summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// 建立连接并启动订阅。内部自动断线重连（指数退避 1s→30s，与 PlcConnectionManager 策略一致）。
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { State: HubConnectionState.Connected }) return;

        _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl)
            .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) })
            .Build();

        _connection.Reconnecting += _ =>
        {
            _logger.LogWarning("Collector 连接断开，正在重连...");
            ConnectionStateChanged?.Invoke(this, false);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            _logger.LogInformation("Collector 重连成功");
            _consecutiveFailures = 0;
            ConnectionStateChanged?.Invoke(this, true);
            return Task.CompletedTask;
        };
        _connection.Closed += async ex =>
        {
            _logger.LogWarning(ex, "Collector 连接已关闭");
            ConnectionStateChanged?.Invoke(this, false);
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                await _connection.StartAsync(_reconnectCts?.Token ?? CancellationToken.None);
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "Collector 重连失败");
                _consecutiveFailures++;
            }
        };

        try
        {
            await _connection.StartAsync(cancellationToken);
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

    // ──────────── 强类型回调注册（由 RemoteRuntimeSink 调用） ────────────

    public void OnSnapshot(Action<DeviceSnapshotDto> handler)
        => _connection!.On<DeviceSnapshotDto>(nameof(IKanbanHubClient.OnSnapshot), handler);

    public void OnAlarmEvent(Action<AlarmEventDto> handler)
        => _connection!.On<AlarmEventDto>(nameof(IKanbanHubClient.OnAlarmEvent), handler);

    public void OnStatusEvent(Action<StatusEventDto> handler)
        => _connection!.On<StatusEventDto>(nameof(IKanbanHubClient.OnStatusEvent), handler);

    // ──────────── 服务端调用 ────────────

    public async Task<IReadOnlyList<DeviceSnapshotDto>> GetCurrentSnapshotsAsync(CancellationToken ct = default)
        => await _connection!.InvokeAsync<IReadOnlyList<DeviceSnapshotDto>>(
            nameof(IKanbanHubServer.GetCurrentSnapshotsAsync), ct);

    public async Task SubscribeSnapshotsAsync(CancellationToken ct = default)
        => await _connection!.InvokeAsync(nameof(IKanbanHubServer.SubscribeSnapshotsAsync), ct);

    public async Task SubscribeAlarmEventsAsync(long afterSeq, CancellationToken ct = default)
        => await _connection!.InvokeAsync(nameof(IKanbanHubServer.SubscribeAlarmEventsAsync), afterSeq, ct);

    public async Task SubscribeStatusEventsAsync(long afterSeq, CancellationToken ct = default)
        => await _connection!.InvokeAsync(nameof(IKanbanHubServer.SubscribeStatusEventsAsync), afterSeq, ct);

    public async Task<HistoryQueryResponse> QueryHistoryAsync(HistoryQueryRequest request, CancellationToken ct = default)
        => await _connection!.InvokeAsync<HistoryQueryResponse>(
            nameof(IKanbanHubServer.QueryHistoryAsync), request, ct);

    /// <summary>拉取 Collector 运行诊断快照（运行监控页 Remote 模式）。</summary>
    public async Task<CollectorDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default)
        => await _connection!.InvokeAsync<CollectorDiagnosticsDto>("GetDiagnosticsAsync", ct);

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
