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
public sealed class KanbanDataClient : IAsyncDisposable, IKanbanMonitoringClient
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

    /// <summary>
    /// 当前操作人（用于远程管理写操作的审计溯源）。连接时经查询串 operator 传给 Collector Hub
    /// （Hub 无认证，属有意设计的局域网查看；操作人由客户端显式提供）。
    /// </summary>
    public string OperatorName { get; set; } = string.Empty;

    /// <summary>连接用 URL：未设置操作人时用原始 HubUrl，否则追加 operator 查询参数。</summary>
    private string BuildConnectionUrl()
    {
        if (string.IsNullOrEmpty(OperatorName)) return _hubUrl;
        var sep = _hubUrl.Contains('?') ? "&" : "?";
        return _hubUrl + sep + "operator=" + Uri.EscapeDataString(OperatorName);
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
    /// 建立连接并启动订阅。内部自动断线重连（指数退避 1s→30s，与 PlcConnectionManager 策略一致）。
    /// 运行中断线由 WithAutomaticReconnect 自愈；**首次连接失败（StartAsync 抛异常）不做后台重连**——
    /// 重连所有权归调用方（WASM 端 DashboardState.RetryLoop / WPF 端 Coordinator），
    /// 避免"客户端内部循环 + 调用方循环"双重重连互相覆盖连接。
    /// 本方法失败时会释放本次创建的连接实例，可安全重复调用。
    /// 连接超时 10s：Collector 不可达时快速失败，避免长时间挂起。
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // 并发保护：同一实例的并发 ConnectAsync 串行化（double-check 已连接则直接返回），
        // 避免多个调用各自建连接、后者覆盖 _connection 导致前者泄漏。
        await _connectGate.WaitAsync(cancellationToken);
        HubConnection? created = null;
        try
        {
            if (_connection is { State: HubConnectionState.Connected }) return;

            _reconnectCts?.Dispose();
            _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var builder = new HubConnectionBuilder()
                .WithUrl(BuildConnectionUrl())
                // 与 Collector 服务端一致：MessagePack 二进制序列化（需两端同时启用）；WASM 端走默认 JSON
                .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) });
            if (_useMessagePack)
                builder.AddMessagePackProtocol(options =>
                {
                    // 时区漂移修复（P1）：保留 DateTime.Kind（默认 resolver 会把 DateTime 转
                    // UTC 序列化（值 -8h）且 Kind=Utc，WPF 直接 StringFormat 渲染早 8 小时）。
                    // 必须用 CompositeResolver 组合：NativeDateTimeResolver 只处理 DateTime，
                    // 其余类型（DTO/枚举等）回退 ContractlessStandardResolver——直接替换 resolver
                    // 会丢 Contractless 导致 FormatterNotRegisteredException（快照/事件推送失败）。
                    options.SerializerOptions = MessagePack.MessagePackSerializerOptions.Standard
                        .WithResolver(MessagePack.Resolvers.CompositeResolver.Create(
                            MessagePack.Resolvers.NativeDateTimeResolver.Instance,
                            MessagePack.Resolvers.ContractlessStandardResolver.Instance));
                });
            created = builder.Build();
            _connection = created;
            // 新连接实例：已注册回调作废（回调挂在旧实例上，新实例需重新注册——按连接实例去重语义）
            lock (_registeredHandlers) _registeredHandlers.Clear();

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
            _connection.Closed += ex =>
            {
                // WithAutomaticReconnect 全部耗尽后触发：仅通知 UI 状态（保持 Disconnected），
                // 不再自行循环重连——重连所有权在调用方（首次连接重试循环 / 上层策略）。
                _logger.LogWarning(ex, "Collector 自动重连已耗尽，连接保持断开（等待上层重试策略）");
                ConnectionStateChanged?.Invoke(this, false);
                return Task.CompletedTask;
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
            catch
            {
                _logger.LogError("连接 Collector 失败 {Url}", _hubUrl);
                // 释放本次失败的连接实例（含其内部重连任务），避免反复 ConnectAsync 泄漏旧连接；
                // 调用方重试时会创建全新连接。异常继续向上抛（调用方决定是否重试）。
                if (ReferenceEquals(_connection, created))
                    _connection = null;
                try { await created.DisposeAsync(); } catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "释放失败连接异常"); }
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
            throw new InvalidOperationException(NotConnectedMessage);
    }

    /// <summary>连接未建立异常消息（供调用方按消息特征识别"连接未就绪"瞬态，避免硬编码中文）。</summary>
    public const string NotConnectedMessage = "SignalR 连接尚未建立：请先调用 ConnectAsync 并等待成功（回调注册同理）。";

    /// <summary>校验连接已建立（供依赖连接状态的服务端调用使用）。</summary>
    public void EnsureConnectionEstablished() => EnsureConnected();

    // ──────────── 强类型回调注册（由数据消费者调用，须在连接建立后） ────────────

    /// <summary>
    /// 已注册回调方法名（**按连接实例**去重，审查修复 2026-08-13）：
    /// SignalR 的 On 是追加语义——调用方在"部分失败重试"路径对同一连接重复注册会导致同一消息双回调；
    /// 而重连失败重建连接实例后回调会丢失、必须重新注册。两者矛盾，故：
    /// 注册幂等性按连接实例维护——ConnectAsync 新建连接时清空本集合，On* 对当前连接只注册一次。
    /// </summary>
    private readonly HashSet<string> _registeredHandlers = new();

    private void RegisterHandlerOnce(string methodName, Action register)
    {
        EnsureConnected();
        lock (_registeredHandlers)
        {
            if (!_registeredHandlers.Add(methodName)) return;
        }
        register();
    }

    public void OnSnapshot(Action<DeviceSnapshotDto> handler)
    {
        RegisterHandlerOnce(nameof(IKanbanHubClient.OnSnapshot),
            () => _connection!.On<DeviceSnapshotDto>(nameof(IKanbanHubClient.OnSnapshot), handler));
    }

    public void OnAlarmEvent(Action<AlarmEventDto> handler)
    {
        RegisterHandlerOnce(nameof(IKanbanHubClient.OnAlarmEvent),
            () => _connection!.On<AlarmEventDto>(nameof(IKanbanHubClient.OnAlarmEvent), handler));
    }

    public void OnStatusEvent(Action<StatusEventDto> handler)
    {
        RegisterHandlerOnce(nameof(IKanbanHubClient.OnStatusEvent),
            () => _connection!.On<StatusEventDto>(nameof(IKanbanHubClient.OnStatusEvent), handler));
    }

    public void OnMeta(Action<MetaStateDto> handler)
    {
        RegisterHandlerOnce(nameof(IKanbanHubClient.OnMeta),
            () => _connection!.On<MetaStateDto>(nameof(IKanbanHubClient.OnMeta), handler));
    }

    public void OnLocalizationChanged(Action<LocalizationChangedDto> handler)
    {
        RegisterHandlerOnce(nameof(IKanbanHubClient.OnLocalizationChanged),
            () => _connection!.On<LocalizationChangedDto>(nameof(IKanbanHubClient.OnLocalizationChanged), handler));
    }

    /// <summary>
    /// 订阅配方下发进度推送（返回订阅句柄，Dispose 即退订——调用方必须在不再需要时释放，
    /// 否则 handler 逐次累积（进度回调重复触发 + 内存泄漏）。
    /// </summary>
    internal IDisposable OnRecipeApplyProgress(Action<RecipeApplyProgressDto> handler)
    {
        EnsureConnected();
        return _connection!.On<RecipeApplyProgressDto>(nameof(IKanbanHubClient.OnRecipeApplyProgress), handler);
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

    /// <summary>SN 序列号追溯查询（按 SN 精确 / 工单 / 设备+时间范围，服务端分页）。</summary>
    public async Task<SnEventQueryResponse> QuerySnEventsAsync(SnEventQueryRequest request, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<SnEventQueryResponse>(
            nameof(IKanbanHubServer.QuerySnEventsAsync), request, ct);
    }

    /// <summary>批量历史查询：多个子查询一次往返（服务端全量翻页聚合），供生产复盘页多设备批查使用。</summary>
    public async Task<BatchHistoryQueryResponse> QueryHistoryBatchAsync(
        BatchHistoryQueryRequest request, CancellationToken ct = default)
    {
        EnsureConnected();
        _logger.LogInformation("批量查询 Invoke 发出 {Count} 个子查询 State={State}",
            request.Queries.Count, _connection!.State);
        var result = await _connection!.InvokeAsync<BatchHistoryQueryResponse>(
            nameof(IKanbanHubServer.QueryHistoryBatchAsync), request, ct);
        _logger.LogInformation("批量查询 Invoke 返回 {Count} 个结果", result.Results.Count);
        return result;
    }

    /// <summary>拉取 Collector 运行诊断快照（运行监控页 Remote 模式）。</summary>
    public async Task<CollectorDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        // 审查修复 2026-09-05（P2）：改用契约接口名调用（原为字符串字面量，接口未声明该方法，
        // 服务端改名后仅运行时报错）。IKanbanHubServer.GetDiagnosticsAsync 现已声明。
        return await _connection!.InvokeAsync<CollectorDiagnosticsDto>(
            nameof(IKanbanHubServer.GetDiagnosticsAsync), ct);
    }

    /// <summary>同步设备配置到 Collector 落盘（Remote 模式设备管理保存）。</summary>
    internal async Task SaveDevicesAsync(IReadOnlyList<DeviceConfigDto> devices, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanAdminServer.SaveDevicesAsync), devices, ct);
    }

    /// <summary>查询 Collector 侧 devices.json.bak 是否存在（Remote 回滚按钮状态）。</summary>
    internal async Task<bool> HasDeviceBackupAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<bool>(nameof(IKanbanAdminServer.HasDeviceBackupAsync), ct);
    }

    /// <summary>从 Collector 侧 devices.json.bak 恢复设备配置（Remote 模式）。</summary>
    internal async Task<IReadOnlyList<DeviceConfigDto>?> RollbackDevicesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<IReadOnlyList<DeviceConfigDto>?>(
            nameof(IKanbanAdminServer.RollbackDevicesAsync), ct);
    }

    /// <summary>同步全部配方到 Collector（Remote 模式配方管理保存时落盘 recipes.json）。</summary>
    internal async Task SaveRecipesAsync(List<RecipeDto> recipes, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanAdminServer.SaveRecipesAsync), recipes, ct);
    }

    /// <summary>从 Collector 拉取配方库（Remote 模式）。</summary>
    public async Task<IReadOnlyList<RecipeDto>> GetRecipesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<IReadOnlyList<RecipeDto>>(nameof(IKanbanHubServer.GetRecipesAsync), ct);
    }

    /// <summary>查询当前活跃报警状态快照（Remote 模式；活跃报警墙直查采集端真源）。</summary>
    public async Task<IReadOnlyList<ActiveAlarmStateDto>> QueryActiveAlarmStatesAsync(string? deviceId = null, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<IReadOnlyList<ActiveAlarmStateDto>>(
            nameof(IKanbanHubServer.QueryActiveAlarmStatesAsync), deviceId, ct);
    }

    /// <summary>下发配方到指定设备（Remote 模式：写 PLC 由 Collector 执行，失败已回滚）。</summary>
    internal async Task<RecipeApplyResultDto> ApplyRecipeAsync(string deviceId, string recipeId, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<RecipeApplyResultDto>(nameof(IKanbanAdminServer.ApplyRecipeAsync), deviceId, recipeId, ct);
    }

    /// <summary>一键清零全部设备 OEE（Remote 模式：PLC 触发位与软件侧累计均由 Collector 清零）。</summary>
    internal async Task<OeeResetAllResultDto> ResetAllOeeAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<OeeResetAllResultDto>(nameof(IKanbanAdminServer.ResetAllOeeAsync), ct);
    }

    /// <summary>向 Collector 管理 Hub 写入一条审计记录。</summary>
    internal async Task RecordAuditAsync(AuditLogRecordRequest request, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanAdminServer.RecordAuditAsync), request, ct);
    }

    /// <summary>从 Collector 拉取设备配置（Remote 模式屏端零配置，不依赖本地 devices.json）。</summary>
    public async Task<IReadOnlyList<DeviceConfigDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<IReadOnlyList<DeviceConfigDto>>(nameof(IKanbanHubServer.GetDevicesAsync), ct);
    }

    /// <summary>新增/更新工单到 Collector 落库，返回带 Id 的结果。</summary>
    internal async Task<WorkOrderDto> UpsertWorkOrderAsync(WorkOrderDto workOrder, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<WorkOrderDto>(nameof(IKanbanAdminServer.UpsertWorkOrderAsync), workOrder, ct);
    }

    /// <summary>删除工单（Collector 落库）。</summary>
    internal async Task DeleteWorkOrderAsync(int workOrderId, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanAdminServer.DeleteWorkOrderAsync), workOrderId, ct);
    }

    /// <summary>查询设备当前工单（Running 优先，无则回退最新 Pending；无工单返回 null）。</summary>
    public async Task<WorkOrderDto?> GetCurrentWorkOrderAsync(string deviceId, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<WorkOrderDto?>(nameof(IKanbanHubServer.GetCurrentWorkOrderAsync), deviceId, ct);
    }

    /// <summary>查询工单产量聚合（按工单时间窗口差分，非班次会话累计）。</summary>
    public async Task<WorkOrderProductionSummaryDto> GetWorkOrderProductionSummaryAsync(int workOrderId, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<WorkOrderProductionSummaryDto>(
            nameof(IKanbanHubServer.GetWorkOrderProductionSummaryAsync), workOrderId, ct);
    }

    /// <summary>查询当前班次进度。</summary>
    public async Task<ShiftProgressDto> GetShiftProgressAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<ShiftProgressDto>(nameof(IKanbanHubServer.GetShiftProgressAsync), ct);
    }

    /// <summary>同步采集设置到 Collector 落盘并热生效（Remote 模式设置页保存）。</summary>
    internal async Task SaveCollectorSettingsAsync(CollectorSettingsDto settings, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(nameof(IKanbanAdminServer.SaveCollectorSettingsAsync), settings, ct);
    }

    /// <summary>服务端版本握手（Collector 程序集信息版本；用于升级兼容性校验）。</summary>
    public async Task<string> GetServerVersionAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string>(nameof(IKanbanHubServer.GetServerVersionAsync), ct);
    }

    /// <summary>工单列表（只读管理页数据源）。</summary>
    public async Task<IReadOnlyList<WorkOrderDto>> GetWorkOrdersAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<IReadOnlyList<WorkOrderDto>>(nameof(IKanbanHubServer.GetWorkOrdersAsync), ct);
    }

    /// <summary>采集设置快照（只读设置页数据源）。</summary>
    public async Task<CollectorSettingsDto> GetCollectorSettingsAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CollectorSettingsDto>(nameof(IKanbanHubServer.GetCollectorSettingsAsync), ct);
    }

    /// <summary>审计日志分页查询（只读审计页数据源）。</summary>
    public async Task<AuditLogQueryResponse> QueryAuditLogsAsync(AuditLogQueryRequest request, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<AuditLogQueryResponse>(nameof(IKanbanHubServer.QueryAuditLogsAsync), request, ct);
    }

    /// <summary>看板标题（Collector settings.json 的 AppTitle；屏端拉取实现零配置）。</summary>
    public async Task<string> GetTitleAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string>(nameof(IKanbanHubServer.GetTitleAsync), ct);
    }

    /// <summary>旧版界面语言枚举值（兼容旧版 Collector；新屏端使用 GetLanguageCodeAsync）。</summary>
    public async Task<int> GetLanguageAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<int>(nameof(IKanbanHubServer.GetLanguageAsync), ct);
    }

    /// <summary>界面语言文化代码（屏端拉取实现零配置，支持 CSV 动态语言列）。</summary>
    public async Task<string> GetLanguageCodeAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string>(nameof(IKanbanHubServer.GetLanguageCodeAsync), ct);
    }

    /// <summary>从 Collector 拉取启动时加载的本地化覆盖表。</summary>
    public async Task<IReadOnlyList<LocalizationOverrideDto>> GetLocalizationOverridesAsync(
        CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<IReadOnlyList<LocalizationOverrideDto>>(
            nameof(IKanbanHubServer.GetLocalizationOverridesAsync), ct);
    }

    public async ValueTask DisposeAsync()
    {
        // 幂等：首次调用把 _reconnectCts 置 null 并取消/释放；二次调用直接释放连接
        // （HubConnection.DisposeAsync 本身幂等）。修复：二次调用对已 Dispose 的 CTS 再 Cancel()
        // 会抛 ObjectDisposedException——IAsyncDisposable 契约要求重复调用安全。
        var cts = Interlocked.Exchange(ref _reconnectCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "释放 HubConnection 异常"); }
        }
        try { cts?.Dispose(); } catch (ObjectDisposedException) { }
    }
}
