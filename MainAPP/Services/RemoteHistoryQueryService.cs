using Kanban.Client;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Contracts;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using MainAPP.Models;
using Microsoft.Extensions.Logging;
using DeviceStatus = Kanban.Contracts.Enums.DeviceStatus;
using DefectSeverity = Kanban.Contracts.Enums.DefectSeverity;
using DefectCategory = Kanban.Contracts.Enums.DefectCategory;

namespace MainAPP.Services;

/// <summary>
/// 历史查询路由代理（展示端瘦身核心）：实现全部历史接口，
/// Local 模式委托本地 <see cref="HistoryService"/>（SQLite，行为不变），
/// Remote 模式通过 <see cref="KanbanDataClient.QueryHistoryAsync"/> 走 SignalR 到 Collector。
/// ViewModel 只依赖接口，切换模式无需改任何调用方。
/// 写方法（Log*）在 Remote 模式下无意义（写入只在 Collector 采集管线发生），记录警告并忽略。
/// </summary>
public sealed class RemoteHistoryQueryService :
    IHistoryService,
    IHistoryQueryExecutor,
    IWorkOrderProductionBatchQuery,
    IDefectHistoryReader,
    IAsyncProductionHistoryReader,
    ISnEventStore
{
    private readonly HistoryService _local;
    private readonly DefectHistoryStore _localDefectStore;
    private readonly SnEventStore _localSnStore;
    private readonly KanbanDataClient _client;
    private readonly IRuntimeMode _runtimeMode;
    private readonly ILogger<RemoteHistoryQueryService> _logger;

    /// <summary>
    /// 单次 Remote 历史查询超时（SignalR 默认 Invoke 超时 30s，UI 线程同步等待下过长；
    /// 10s 内无响应按失败快速返回，调用方走既有"查询失败"提示路径）。
    /// </summary>
    private static readonly TimeSpan RemoteCallTimeout = TimeSpan.FromSeconds(10);

    public RemoteHistoryQueryService(
        HistoryService local,
        DefectHistoryStore localDefectStore,
        SnEventStore localSnStore,
        KanbanDataClient client,
        IRuntimeMode runtimeMode,
        ILogger<RemoteHistoryQueryService> logger)
    {
        _local = local;
        _localDefectStore = localDefectStore;
        _localSnStore = localSnStore;
        _client = client;
        _runtimeMode = runtimeMode;
        _logger = logger;
    }

    private bool IsRemote => _runtimeMode.IsRemote;

    // ──────────── IProductionHistoryReader ────────────

    public List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, from, to, deviceId, shiftName)
            : _local.QueryProductionLogs(from, to, deviceId, shiftName);

    public Task<List<ProductionLog>> QueryProductionLogsAsync(
        DateTime from,
        DateTime to,
        string? deviceId = null,
        string? shiftName = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRemote)
            return Task.Run(() => _local.QueryProductionLogs(from, to, deviceId, shiftName), cancellationToken);

        return QueryRemoteProductionLogsAsync(new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.ProductionLog,
            From = from == DateTime.MinValue ? null : from,
            To = to == DateTime.MaxValue ? null : to,
            DeviceId = deviceId,
            ShiftName = shiftName,
            Page = 1,
            PageSize = HistoryQueryLimits.MaxPageSize,
        }, cancellationToken);
    }

    public List<ProductionLog> QueryProductionLogsByWorkOrder(int workOrderId)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, DateTime.MinValue, DateTime.MaxValue, null, null, workOrderId: workOrderId)
            : _local.QueryProductionLogsByWorkOrder(workOrderId);

    public Task<List<ProductionLog>> QueryProductionLogsByWorkOrderAsync(
        int workOrderId,
        CancellationToken cancellationToken = default)
    {
        if (!IsRemote)
            return Task.Run(() => _local.QueryProductionLogsByWorkOrder(workOrderId), cancellationToken);

        return QueryRemoteProductionLogsAsync(new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.ProductionLog,
            WorkOrderId = workOrderId,
            Page = 1,
            PageSize = HistoryQueryLimits.MaxPageSize,
        }, cancellationToken);
    }

    public ProductionLog? GetLatestProductionBefore(string deviceId, DateTime before, string shiftName)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, DateTime.MinValue, before, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.GetLatestProductionBefore(deviceId, before, shiftName);

    public async Task<ProductionLog?> GetLatestProductionBeforeAsync(
        string deviceId,
        DateTime before,
        string shiftName,
        CancellationToken cancellationToken = default)
    {
        if (!IsRemote)
            return await Task.Run(() => _local.GetLatestProductionBefore(deviceId, before, shiftName), cancellationToken);

        var logs = await QueryRemoteProductionLogsAsync(new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.ProductionLog,
            To = before,
            DeviceId = deviceId,
            ShiftName = shiftName,
            LatestFirst = true,
            Page = 1,
            PageSize = 1,
        }, cancellationToken).ConfigureAwait(false);
        return logs.FirstOrDefault();
    }

    public Dictionary<string, List<ProductionLog>> QueryProductionLogsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
        => IsRemote
            ? QueryBatchByDevice<ProductionLog>(HistoryQueryType.ProductionLog, from, to, deviceIds)
            : _local.QueryProductionLogsBatch(from, to, deviceIds);

    // ──────────── IAlarmHistoryService ────────────

    public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<AlarmEventRecord>(HistoryQueryType.AlarmEvent, from, to, deviceId, shiftName)
            : _local.QueryAlarmEvents(from, to, deviceId, shiftName);

    public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
        => IsRemote
            ? QueryBatchByDevice<AlarmEventRecord>(HistoryQueryType.AlarmEvent, from, to, deviceIds)
            : _local.QueryAlarmEventsBatch(from, to, deviceIds);

    public AlarmEventRecord? GetLatestAlarmEvent(string alarmId)
        => IsRemote
            ? QueryRemoteAlarmByAlarmId(alarmId)
            : _local.GetLatestAlarmEvent(alarmId);

    public AlarmEventRecord? GetLatestAlarmEventStrict(string alarmId)
        => IsRemote
            ? QueryRemoteAlarmByAlarmId(alarmId)
            : _local.GetLatestAlarmEventStrict(alarmId);

    /// <summary>查询当前活跃报警状态快照：Local 委托本地存储；Remote 走 SignalR 直查采集端 ActiveAlarmStates 表。</summary>
    public List<ActiveAlarmStateRecord> QueryActiveAlarmStates(string? deviceId = null)
    {
        if (!IsRemote)
            return _local.QueryActiveAlarmStates(deviceId);

        using var timeoutCts = new CancellationTokenSource(RemoteCallTimeout);
        var dtos = Task.Run(
            () => _client.QueryActiveAlarmStatesAsync(deviceId, timeoutCts.Token).GetAwaiter().GetResult())
            .GetAwaiter().GetResult();
        return dtos.Select(ToActiveStateEntity).ToList();
    }

    private static ActiveAlarmStateRecord ToActiveStateEntity(ActiveAlarmStateDto dto) => new()
    {
        DeviceId = dto.DeviceId,
        DeviceName = dto.DeviceName,
        AlarmId = dto.AlarmId,
        AlarmName = dto.AlarmName,
        PlcAddress = dto.PlcAddress,
        IsActive = dto.IsActive,
        TriggeredAt = dto.TriggeredAt,
        ShiftName = dto.ShiftName,
        UpdatedAt = dto.UpdatedAt,
    };

    // ──────────── IStatusTransitionHistoryService ────────────

    public List<StatusTransitionRecord> QueryStatusTransitions(string deviceId, DateTime from, DateTime to, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<StatusTransitionRecord>(HistoryQueryType.StatusTransition, from, to, deviceId, shiftName)
            : _local.QueryStatusTransitions(deviceId, from, to, shiftName);

    public Dictionary<string, List<StatusTransitionRecord>> QueryStatusTransitionsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
        => IsRemote
            ? QueryBatchByDevice<StatusTransitionRecord>(HistoryQueryType.StatusTransition, from, to, deviceIds)
            : _local.QueryStatusTransitionsBatch(from, to, deviceIds);

    public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<StatusTransitionRecord>(HistoryQueryType.StatusTransition, DateTime.MinValue, before, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.GetLatestStatusBefore(deviceId, before, shiftName);

    // ──────────── IProductionHistoryWriter（Remote 模式忽略） ────────────

    public void LogProduction(ProductionLog log)
    {
        if (IsRemote)
        {
            _logger.LogWarning("Remote 模式忽略 LogProduction（写入仅在 Collector 采集管线）");
            return;
        }
        _local.LogProduction(log);
    }

    public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, Kanban.Collector.Core.Entities.AlarmEventType eventType, DateTime eventTime,
        string? shiftName = null)
    {
        if (IsRemote)
        {
            _logger.LogWarning("Remote 模式忽略 LogAlarmEvent Alarm={AlarmId}", alarmId);
            return false;
        }
        return _local.LogAlarmEvent(deviceId, deviceName, alarmId, alarmName, plcAddress, eventType, eventTime, shiftName);
    }

    public bool LogStatusTransition(string deviceId, string deviceName,
        int previousState, int currentState, DateTime eventTime,
        string? shiftName = null, int offlineCause = 0)
    {
        if (IsRemote)
        {
            _logger.LogWarning("Remote 模式忽略 LogStatusTransition Device={DeviceId}", deviceId);
            return false;
        }
        return _local.LogStatusTransition(deviceId, deviceName, previousState, currentState, eventTime, shiftName, offlineCause);
    }

    // ──────────── IHistoryQueryExecutor（Strict：Remote 网络异常直接抛出） ────────────

    public List<ProductionLog> QueryProductionLogsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, from, to, deviceId, shiftName)
            : _local.QueryProductionLogsStrict(from, to, deviceId, shiftName);

    public List<ProductionLog> QueryProductionLogsSampled15Min(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? SampleRemoteProduction(from, to, deviceId, shiftName)
            : _local.QueryProductionLogsSampled15Min(from, to, deviceId, shiftName);

    public ProductionLog? GetLatestProductionBeforeStrict(string deviceId, DateTime before, string shiftName)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, DateTime.MinValue, before, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.GetLatestProductionBeforeStrict(deviceId, before, shiftName);

    public List<AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<AlarmEventRecord>(HistoryQueryType.AlarmEvent, from, to, deviceId, shiftName)
            : _local.QueryAlarmEventsStrict(from, to, deviceId, shiftName);

    public List<StatusTransitionRecord> QueryStatusTransitionsStrict(string deviceId, DateTime from, DateTime to, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<StatusTransitionRecord>(HistoryQueryType.StatusTransition, from, to, deviceId, shiftName)
            : _local.QueryStatusTransitionsStrict(deviceId, from, to, shiftName);

    public StatusTransitionRecord? GetLatestStatusBeforeStrict(string deviceId, DateTime before, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<StatusTransitionRecord>(HistoryQueryType.StatusTransition, DateTime.MinValue, before, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.GetLatestStatusBeforeStrict(deviceId, before, shiftName);

    public (List<ProductionLog> Items, int Total) QueryProductionLogsPaged(
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize)
        => IsRemote
            ? QueryRemotePaged<ProductionLog>(HistoryQueryType.ProductionLog, from, to, deviceId, shiftName, page, pageSize)
            : _local.QueryProductionLogsPaged(from, to, deviceId, shiftName, page, pageSize);

    public ProductionLog? QueryLatestProductionLog(DateTime from, DateTime to, string? deviceId, string? shiftName)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, from, to, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.QueryLatestProductionLog(from, to, deviceId, shiftName);

    /// <summary>分页查询报警事件（Remote 走 SignalR 服务端 SQL 分页，避免全量拉取）。</summary>
    public (List<AlarmEventRecord> Items, int Total) QueryAlarmEventsPaged(
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize, string? alarmName = null)
        => IsRemote
            ? QueryRemotePaged<AlarmEventRecord>(HistoryQueryType.AlarmEvent, from, to, deviceId, shiftName, page, pageSize, alarmName)
            : _local.QueryAlarmEventsPaged(from, to, deviceId, shiftName, page, pageSize, alarmName);

    /// <summary>分页查询状态转换记录（Remote 走 SignalR 服务端 SQL 分页）。</summary>
    public (List<StatusTransitionRecord> Items, int Total) QueryStatusTransitionsPaged(
        string deviceId, DateTime from, DateTime to, string? shiftName, int page, int pageSize)
        => IsRemote
            ? QueryRemotePaged<StatusTransitionRecord>(HistoryQueryType.StatusTransition, from, to, deviceId, shiftName, page, pageSize)
            : _local.QueryStatusTransitionsPaged(deviceId, from, to, shiftName, page, pageSize);

    /// <summary>分页查询缺陷快照（Remote 走 SignalR 服务端 SQL 分页）。</summary>
    public (List<DefectSnapshotRecord> Items, int Total) QueryDefectSnapshotsPaged(
        DateTime from, DateTime to, string deviceId, int page, int pageSize)
        => IsRemote
            ? QueryRemotePaged<DefectSnapshotRecord>(HistoryQueryType.DefectSnapshot, from, to, deviceId, null, page, pageSize)
            : _localDefectStore.QueryDefectSnapshotsPaged(from, to, deviceId, page, pageSize);

    /// <summary>Remote 分页查询：服务端 SQL 层 Skip/Take + Count（配合 Collector 的 Query*Paged 实现）。</summary>
    private List<ProductionLog> SampleRemoteProduction(DateTime from, DateTime to, string? deviceId, string? shiftName)
    {
        var logs = QueryProductionLogsStrict(from, to, deviceId, shiftName);
        if (logs.Count == 0) return [];
        var dtos = logs.Select(p => new ProductionLogDto
        {
            Id = p.Id,
            DeviceId = p.DeviceId,
            DeviceName = p.DeviceName,
            ShiftName = p.ShiftName,
            WorkOrderId = p.WorkOrderId,
            OkProduction = p.OkProduction,
            NgProduction = p.NgProduction,
            StatusWord = p.StatusWord,
            Timestamp = p.Timestamp,
        }).ToList();
        var sampled = Kanban.Analysis.ProductionWindowMetrics.Sample15Min(dtos);
        var byId = logs.ToDictionary(l => l.Id);
        return sampled.Select(d => byId[d.Id]).ToList();
    }

    private (List<T> Items, int Total) QueryRemotePaged<T>(
        HistoryQueryType type,
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize, string? alarmName = null)
    {
        var request = new HistoryQueryRequest
        {
            QueryType = type,
            From = from == DateTime.MinValue ? null : from,
            To = to == DateTime.MaxValue ? null : to,
            DeviceId = deviceId,
            ShiftName = shiftName,
            AlarmName = alarmName,
            LatestFirst = false,
            Page = page,
            PageSize = pageSize,
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(RemoteCallTimeout);
        HistoryQueryResponse response;
        try
        {
            response = Task.Run(() => _client.QueryHistoryAsync(request, timeoutCts.Token)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Remote 历史查询超时 Type={QueryType} Device={DeviceId}", type, deviceId);
            throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed, ex);
        }
        sw.Stop();
        if (!string.IsNullOrEmpty(response.Error))
        {
            // 同上：服务端细节仅进日志，用户可见文案本地化渲染
            _logger.LogError("Remote 历史分页查询失败 ErrorCode={ErrorCode} 详情={Detail}", response.ErrorCode, response.Error);
            throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
        }
        _logger.LogInformation("Remote 历史分页查询完成 Type={QueryType} Device={DeviceId} 耗时 {Elapsed}ms Page={Page} Total={Total}",
            type, deviceId, sw.ElapsedMilliseconds, page, response.Total);
        if (response.IsWindowTruncated)
            _logger.LogWarning("Remote 历史分页查询时间窗口被服务端截断 Type={QueryType} Device={DeviceId}", type, deviceId);
        return (MapDtos<T>(response), response.Total);
    }

    // ──────────── IDefectHistoryReader ────────────

    public List<DefectSnapshotRecord> Query(DateTime from, DateTime to, string deviceId)
        => IsRemote
            ? QueryRemoteList<DefectSnapshotRecord>(HistoryQueryType.DefectSnapshot, from, to, deviceId, null)
            : _localDefectStore.Query(from, to, deviceId);

    /// <summary>
    /// 缺陷窗口边界聚合：走批量端点（服务端 SQL 分组下推，只返回每组首末 + 基线）。
    /// 缺陷快照是高频累计值（每日数十万条），旧的全量逐页拉取（2 天 27 万条 ≈ 549 页深分页）
    /// 是复盘页 15~18s 耗时的根因；分组后一次 Invoke 返回几十行。
    /// </summary>
    public List<DefectSnapshotRecord> QueryWindowBounds(DateTime from, DateTime to, string deviceId)
    {
        if (!IsRemote)
            return _localDefectStore.QueryWindowBounds(from, to, deviceId);

        var request = new BatchHistoryQueryRequest
        {
            Queries =
            [
                new HistoryQueryRequest
                {
                    QueryType = HistoryQueryType.DefectSnapshot,
                    From = from,
                    To = to,
                    DeviceId = deviceId,
                    Page = 1,
                    PageSize = HistoryQueryLimits.MaxPageSize,
                },
            ],
        };
        var response = InvokeBatch(request);
        return MapDtos<DefectSnapshotRecord>(response.Results[0]);
    }

    /// <summary>
    /// 缺陷快照按小时分组下推（Remote 走批量端点 DefectSnapshotHourly，Local 走同语义存储方法）。
    /// 供复盘页缺陷集中度逐小时差分，避免全量拉取高频累计快照。
    /// </summary>
    public List<DefectSnapshotRecord> QueryHourlyBounds(DateTime from, DateTime to, string deviceId)
    {
        if (!IsRemote)
            return _localDefectStore.QueryHourlyBounds(from, to, deviceId);

        var request = new BatchHistoryQueryRequest
        {
            Queries =
            [
                new HistoryQueryRequest
                {
                    QueryType = HistoryQueryType.DefectSnapshotHourly,
                    From = from,
                    To = to,
                    DeviceId = deviceId,
                    Page = 1,
                    PageSize = HistoryQueryLimits.MaxPageSize,
                },
            ],
        };
        var response = InvokeBatch(request);
        return MapDtos<DefectSnapshotRecord>(response.Results[0]);
    }

    // ──────────── IWorkOrderProductionBatchQuery ────────────
    public Dictionary<int, List<ProductionLog>> QueryProductionLogsByWorkOrderBatch(IReadOnlyList<int> workOrderIds)
    {
        if (!IsRemote)
            return _local.QueryProductionLogsByWorkOrderBatch(workOrderIds);

        // 审查修复 2026-08-15：与 QueryProductionLogsByDeviceWindowsBatch 一致，按服务端子查询上限分块
        // （HistoryQueryLimits.MaxBatchQueries）。此前一次性提交全部工单时，工单数超上限会导致
        // response.Results 被服务端截断、此处 Results[i] 越界抛 IndexOutOfRangeException。
        var result = new Dictionary<int, List<ProductionLog>>();
        for (var offset = 0; offset < workOrderIds.Count; offset += HistoryQueryLimits.MaxBatchQueries)
        {
            var chunk = workOrderIds.Skip(offset).Take(HistoryQueryLimits.MaxBatchQueries).ToList();
            var request = new BatchHistoryQueryRequest
            {
                Queries = chunk
                    .Select(id => new HistoryQueryRequest
                    {
                        QueryType = HistoryQueryType.ProductionLog,
                        WorkOrderId = id,
                        Page = 1,
                        PageSize = HistoryQueryLimits.MaxPageSize,
                    })
                    .ToList(),
            };
            var response = InvokeBatch(request);
            // 审查修复 2026-09-05（P1）：原实现直接 `response.Results[i]` 索引，未校验服务端返回
            // 条数。服务端对批量子查询截断/部分失败时结果数少于请求数 → IndexOutOfRangeException，
            // 中断工单产量聚合回退路径（概览/工单页加载）。与 QueryRemoteList 的校验保持一致。
            if (response.Results.Count != chunk.Count)
                throw new InvalidOperationException("远程历史查询返回数量与请求不一致");
            for (var i = 0; i < chunk.Count; i++)
                result[chunk[i]] = MapDtos<ProductionLog>(response.Results[i]);
        }
        return result;
    }

    public Dictionary<int, List<ProductionLog>> QueryProductionLogsByDeviceWindowsBatch(
        IReadOnlyList<(int WorkOrderId, string DeviceId, DateTime From, DateTime To)> windows)
    {
        if (!IsRemote)
            return (_local as IWorkOrderProductionBatchQuery)?.QueryProductionLogsByDeviceWindowsBatch(windows) ?? [];

        // 审查修复 2026-08-13：工单产量聚合回退路径按窗口批量查询——每批 ≤ HistoryQueryLimits.MaxBatchQueries
        // 个子查询（服务端上限），一次批量 Invoke 取代此前 N 次 UI 线程同步 SignalR 往返
        var result = new Dictionary<int, List<ProductionLog>>();
        for (var offset = 0; offset < windows.Count; offset += HistoryQueryLimits.MaxBatchQueries)
        {
            var chunk = windows.Skip(offset).Take(HistoryQueryLimits.MaxBatchQueries).ToList();
            var request = new BatchHistoryQueryRequest
            {
                Queries = chunk
                    .Select(w => new HistoryQueryRequest
                    {
                        QueryType = HistoryQueryType.ProductionLog,
                        From = w.From == DateTime.MinValue ? null : w.From,
                        To = w.To == DateTime.MaxValue ? null : w.To,
                        DeviceId = w.DeviceId,
                        Page = 1,
                        PageSize = HistoryQueryLimits.MaxPageSize,
                    })
                    .ToList(),
            };
            var response = InvokeBatch(request);
            // 审查修复 2026-09-05（P1）：同上，按下标索引前先校验返回条数，避免服务端结果不足时越界。
            if (response.Results.Count != chunk.Count)
                throw new InvalidOperationException("远程历史查询返回数量与请求不一致");
            for (var i = 0; i < chunk.Count; i++)
            {
                var logs = MapDtos<ProductionLog>(response.Results[i]);
                if (logs.Count > 0) result[chunk[i].WorkOrderId] = logs;
            }
        }
        return result;
    }

    // ──────────── Remote 查询核心 ────────────
    // 每页大小/最大页数/批量子查询数均收敛到 Kanban.Contracts.HistoryQueryLimits（跨进程契约单一来源）

    private List<T> QueryRemoteList<T>(
        HistoryQueryType type,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        bool latestFirst = false,
        int? workOrderId = null)
    {
        var request = new HistoryQueryRequest
        {
            QueryType = type,
            From = from == DateTime.MinValue ? null : from,
            To = to == DateTime.MaxValue ? null : to,
            DeviceId = deviceId,
            ShiftName = shiftName,
            LatestFirst = latestFirst,
            WorkOrderId = workOrderId,
            Page = 1,
            PageSize = HistoryQueryLimits.MaxPageSize,
        };
        return InvokeAndMapAll<T>(request);
    }

    private async Task<List<ProductionLog>> QueryRemoteProductionLogsAsync(
        HistoryQueryRequest firstRequest,
        CancellationToken cancellationToken)
    {
        var pages = new List<HistoryQueryResponse>(8);
        var current = firstRequest;
        var totalReceived = 0;
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            for (var page = 1; page <= HistoryQueryLimits.MaxFetchAllPages; page++)
            {
                overallCts.Token.ThrowIfCancellationRequested();
                using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                pageCts.CancelAfter(RemoteCallTimeout);
                var response = await _client.QueryHistoryAsync(current, pageCts.Token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(response.Error))
                {
                    _logger.LogError("Remote 异步生产历史查询失败 ErrorCode={ErrorCode} 详情={Detail}",
                        response.ErrorCode, response.Error);
                    throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
                }

                pages.Add(response);
                totalReceived += response.ProductionLogs.Count;
                if (response.Total <= totalReceived)
                    return pages.SelectMany(MapDtos<ProductionLog>).ToList();

                current = current with { Page = page + 1 };
            }

            throw new InvalidOperationException(MainAPP.Resources.Strings.F325);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Remote 异步生产历史查询超时 Device={DeviceId}", firstRequest.DeviceId);
            throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
        }
    }

    /// <summary>
    /// 全量语义的 Remote 查询：自动翻页聚合（服务端单页上限 500，直接请求全量会被 clamp 截断——
    /// 见 HistoryPagination.Normalize）。以响应 Total 为收敛条件逐页拉取，
    /// 使 Remote 模式与 Local 模式（全量）行为一致。
    /// </summary>
    private List<T> InvokeAndMapAll<T>(HistoryQueryRequest request)
    {
        // SignalR InvokeAsync 是真正异步的；直接 .GetAwaiter().GetResult() 在 UI 线程会死锁
        // （QueryHistoryAsync 的 await 会尝试回到 UI SynchronizationContext，但 UI 线程被阻塞）。
        // Task.Run 转入线程池执行，无 SynchronizationContext 回跳，避免死锁。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 外层总闸 60s：翻页上限 200 页 × 单页 10s 理论最坏 2000s，总闸防止异常网络下无限等待（审查修复 2026-08-13）
        using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var response = Task.Run(
                () => FetchAllPages(request, r =>
                {
                    // 每页独立 10s 超时（审查修复 2026-08-13）：此前单个 CTS 是整段翻页循环的总预算，
                    // 10s 内未收齐所有页时后续页立即取消，大数据量查询系统性失败。
                    // 与外层总闸链接：总闸到期取消在途页请求。
                    using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                    pageCts.CancelAfter(RemoteCallTimeout);
                    return _client.QueryHistoryAsync(r, pageCts.Token).GetAwaiter().GetResult();
                }, _logger))
                .GetAwaiter().GetResult();
            sw.Stop();
            // 结构化错误：任一页失败即整体失败（与 Local 全量查询"要么全量要么报错"一致）。
            // 服务端 Error 仅进日志（调试细节，可能含 SQLite 内部信息）；用户可见文案按
            // ErrorCode 用本地化资源渲染，避免多语言界面收到服务端中文回显。
            if (!string.IsNullOrEmpty(response.Error))
            {
                _logger.LogError("Remote 历史查询失败 ErrorCode={ErrorCode} 详情={Detail}", response.ErrorCode, response.Error);
                throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
            }
            _logger.LogInformation("Remote 历史全量查询完成 Type={QueryType} Device={DeviceId} 耗时 {Elapsed}ms Total={Total}",
                request.QueryType, request.DeviceId, sw.ElapsedMilliseconds, response.Total);
            if (response.IsWindowTruncated)
                _logger.LogWarning("Remote 历史查询时间窗口被服务端截断 Type={QueryType} Device={DeviceId}（结果已按最近窗口收窄）",
                    request.QueryType, request.DeviceId);
            return MapDtos<T>(response);
        }
        catch (OperationCanceledException)
        {
            // 超时/取消统一转本地化文案（审查修复 2026-08-13）：此前 OCE 直接冒泡，UI 收到未本地化的取消消息
            _logger.LogWarning("Remote 历史全量查询超时 Type={QueryType} Device={DeviceId} 耗时 {Elapsed}ms",
                request.QueryType, request.DeviceId, sw.ElapsedMilliseconds);
            throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
        }
    }

    /// <summary>
    /// 自动翻页聚合：以 <paramref name="firstRequest"/> 为首页逐页拉取，直至收齐
    /// <see cref="HistoryQueryResponse.Total"/> 条。Total 语义（HistoryQueryHandler.Build）：
    /// 分页路径 = 服务端 SQL Count（全量总数），未分页路径（WorkOrderId/AlarmId/LatestFirst）= 本页条数——
    /// 两类对该循环均安全：未分页路径首页即收齐。
    /// 防失控保护：任一页 Error 立即返回（由调用方抛异常）；超过 <see cref="HistoryQueryLimits.MaxFetchAllPages"/> 页
    /// 仍未收齐（服务端 Total 语义异常）则抛异常（审查修复 2026-08-13：此前静默返回部分数据，
    /// 调用方无法区分"已收齐"与"被截断"，复盘/健康评分会基于不完整数据渲染）。
    /// </summary>
    internal static HistoryQueryResponse FetchAllPages(
        HistoryQueryRequest firstRequest,
        Func<HistoryQueryRequest, HistoryQueryResponse> fetchPage,
        ILogger logger)
    {
        var pages = new List<HistoryQueryResponse>(8);
        var current = firstRequest;
        var totalReceived = 0;
        HistoryQueryResponse last = null!;

        for (var page = 1; page <= HistoryQueryLimits.MaxFetchAllPages; page++)
        {
            last = fetchPage(current);
            pages.Add(last);
            if (!string.IsNullOrEmpty(last.Error))
                return last;
            totalReceived += CountItems(last, firstRequest.QueryType);
            if (last.Total <= totalReceived)
                break; // 已收齐
            current = current with { Page = page + 1 };
        }

        if (pages.Count == HistoryQueryLimits.MaxFetchAllPages && last.Total > totalReceived)
        {
            logger.LogWarning("历史查询翻页达上限 {MaxPages} 页仍未收齐 QueryType={QueryType} Total={Total} 已收={Received}", HistoryQueryLimits.MaxFetchAllPages, firstRequest.QueryType, last.Total, totalReceived);
            throw new InvalidOperationException(MainAPP.Resources.Strings.F325);
        }
        return Merge(pages);
    }

    private static int CountItems(HistoryQueryResponse response, HistoryQueryType queryType) => queryType switch
    {
        HistoryQueryType.ProductionLog => response.ProductionLogs.Count,
        HistoryQueryType.AlarmEvent => response.AlarmEvents.Count,
        HistoryQueryType.StatusTransition => response.StatusTransitions.Count,
        HistoryQueryType.DefectSnapshot => response.DefectSnapshots.Count,
        _ => 0,
    };

    /// <summary>按页拼接响应（Total/Page/PageSize 保持首页值；未用类型字段为空列表，无害）。</summary>
    private static HistoryQueryResponse Merge(List<HistoryQueryResponse> pages)
    {
        var first = pages[0];
        return first with
        {
            ProductionLogs = pages.SelectMany(p => p.ProductionLogs).ToList(),
            AlarmEvents = pages.SelectMany(p => p.AlarmEvents).ToList(),
            StatusTransitions = pages.SelectMany(p => p.StatusTransitions).ToList(),
            DefectSnapshots = pages.SelectMany(p => p.DefectSnapshots).ToList(),
        };
    }

    private AlarmEventRecord? QueryRemoteAlarmByAlarmId(string alarmId)
    {
        var request = new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.AlarmEvent,
            AlarmId = alarmId,
            Page = 1,
            PageSize = 1,
        };
        return InvokeAndMap<AlarmEventRecord>(request).FirstOrDefault();
    }

    private List<T> InvokeAndMap<T>(HistoryQueryRequest request)
    {
        // SignalR InvokeAsync 是真正异步的；直接 .GetAwaiter().GetResult() 在 UI 线程会死锁
        // （QueryHistoryAsync 的 await 会尝试回到 UI SynchronizationContext，但 UI 线程被阻塞）。
        // Task.Run 转入线程池执行，无 SynchronizationContext 回跳，避免死锁。
        using var timeoutCts = new CancellationTokenSource(RemoteCallTimeout);
        var response = Task.Run(() => _client.QueryHistoryAsync(request, timeoutCts.Token)).GetAwaiter().GetResult();
        // 结构化错误：服务端把落库/IO 故障转为 Error 返回（而非伪装空数据），此处转异常
        // 让调用方走既有的"查询失败"提示路径，与真实空数据区分开。
        // 服务端 Error 仅进日志（调试细节）；用户可见文案统一本地化渲染（与 InvokeAndMapAll/QueryRemotePaged 一致）。
        if (!string.IsNullOrEmpty(response.Error))
        {
            _logger.LogError("Remote 历史查询失败 ErrorCode={ErrorCode} 详情={Detail}", response.ErrorCode, response.Error);
            throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
        }
        if (response.IsWindowTruncated)
            _logger.LogWarning("Remote 历史查询时间窗口被服务端截断 QueryType={QueryType} Device={DeviceId}",
                request.QueryType, request.DeviceId);
        return MapDtos<T>(response);
    }

    // ──────────── 批量查询（一次 SignalR 往返，服务端全量翻页聚合） ────────────

    /// <summary>
    /// 多设备同类历史查询：所有设备合并为一个批量请求，一次 Invoke 取回全量。
    /// 服务端对每个子查询做全量翻页聚合（Total 收齐），客户端不再逐设备逐页拉取——
    /// 生产复盘页默认窗口数万条时，把数十次串行往返降为 1 次。
    /// 任一子查询失败 → 整体抛异常（与单查 Strict 语义一致，调用方走既有失败提示路径）。
    /// </summary>
    private Dictionary<string, List<T>> QueryBatchByDevice<T>(
        HistoryQueryType type, DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
    {
        var result = new Dictionary<string, List<T>>(deviceIds.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in deviceIds.Chunk(HistoryQueryLimits.MaxBatchQueries))
        {
            var chunkRequest = new BatchHistoryQueryRequest
            {
                Queries = chunk.Select(id => new HistoryQueryRequest
                {
                    QueryType = type,
                    From = from == DateTime.MinValue ? null : from,
                    To = to == DateTime.MaxValue ? null : to,
                    DeviceId = id,
                    Page = 1,
                    PageSize = HistoryQueryLimits.MaxPageSize,
                }).ToList(),
            };
            var response = InvokeBatch(chunkRequest);
            if (response.Results.Count != chunk.Length)
                throw new InvalidOperationException("远程历史查询返回数量与请求不一致");
            for (var i = 0; i < chunk.Length; i++)
            {
                var item = response.Results[i];
                if (!ResponseMatchesDevice(item, type, chunk[i]))
                    throw new InvalidOperationException("远程历史查询返回顺序或设备标识不一致");
                if (!result.TryAdd(chunk[i], MapDtos<T>(item)))
                    throw new InvalidOperationException("远程历史查询返回重复设备");
            }
        }
        return result;
    }

    private static bool ResponseMatchesDevice(HistoryQueryResponse response, HistoryQueryType type, string deviceId)
    {
        IEnumerable<string> ids = type switch
        {
            HistoryQueryType.ProductionLog => response.ProductionLogs.Select(x => x.DeviceId),
            HistoryQueryType.AlarmEvent => response.AlarmEvents.Select(x => x.DeviceId),
            HistoryQueryType.StatusTransition => response.StatusTransitions.Select(x => x.DeviceId),
            HistoryQueryType.DefectSnapshot or HistoryQueryType.DefectSnapshotHourly => response.DefectSnapshots.Select(x => x.DeviceId),
            _ => []
        };
        return ids.All(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>批量请求执行 + 整体失败判定：任一子查询失败即整体抛异常（Strict 语义）。</summary>
    private BatchHistoryQueryResponse InvokeBatch(BatchHistoryQueryRequest request)
    {
        // 同 InvokeAndMap：Task.Run 避免 UI 线程 SynchronizationContext 死锁
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(RemoteCallTimeout);
        var response = Task.Run(() => _client.QueryHistoryBatchAsync(request, timeoutCts.Token)).GetAwaiter().GetResult();
        sw.Stop();
        foreach (var result in response.Results)
        {
            if (!string.IsNullOrEmpty(result.Error))
            {
                _logger.LogError("Remote 批量历史查询失败 ErrorCode={ErrorCode} 详情={Detail}", result.ErrorCode, result.Error);
                throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
            }
        }
        if (response.Results.Any(r => r.IsWindowTruncated))
            _logger.LogWarning("Remote 批量历史查询存在时间窗口被服务端截断的子查询：{Count} 个", response.Results.Count);
        var counts = string.Join(",", response.Results.Select(r => r.Total));
        _logger.LogInformation("Remote 批量历史查询完成：{Count} 个子查询，耗时 {Elapsed}ms，各 Total=[{Totals}]",
            response.Results.Count, sw.ElapsedMilliseconds, counts);
        return response;
    }

    private static List<T> MapDtos<T>(HistoryQueryResponse response)
    {
        if (typeof(T) == typeof(ProductionLog))
            return response.ProductionLogs
                .Select(dto => (T)(object)new ProductionLog
                {
                    Id = dto.Id,
                    DeviceId = dto.DeviceId,
                    DeviceName = dto.DeviceName,
                    ShiftName = dto.ShiftName,
                    WorkOrderId = dto.WorkOrderId,
                    OkProduction = dto.OkProduction,
                    NgProduction = dto.NgProduction,
                    StatusWord = dto.StatusWord,
                    Timestamp = dto.Timestamp,
                }).ToList();

        if (typeof(T) == typeof(AlarmEventRecord))
            return response.AlarmEvents
                .Select(dto => (T)(object)new AlarmEventRecord
                {
                    Id = dto.Id,
                    DeviceId = dto.DeviceId,
                    DeviceName = dto.DeviceName,
                    AlarmId = dto.AlarmId,
                    AlarmName = dto.AlarmName,
                    PlcAddress = dto.PlcAddress,
                    EventType = (Kanban.Collector.Core.Entities.AlarmEventType)dto.EventType,
                    EventTime = dto.EventTime,
                    ShiftName = dto.ShiftName,
                }).ToList();

        if (typeof(T) == typeof(StatusTransitionRecord))
            return response.StatusTransitions
                .Select(dto => (T)(object)new StatusTransitionRecord
                {
                    Id = dto.Id,
                    DeviceId = dto.DeviceId,
                    DeviceName = dto.DeviceName,
                    PreviousState = (int)dto.PreviousState,
                    CurrentState = (int)dto.CurrentState,
                    EventTime = dto.EventTime,
                    ShiftName = dto.ShiftName,
                }).ToList();

        if (typeof(T) == typeof(DefectSnapshotRecord))
            return response.DefectSnapshots
                .Select(dto => (T)(object)new DefectSnapshotRecord
                {
                    Id = dto.Id,
                    DeviceId = dto.DeviceId,
                    DeviceName = dto.DeviceName,
                    DefectId = dto.DefectId,
                    DefectName = dto.DefectName,
                    Severity = (Kanban.Collector.Core.Models.DefectSeverity)(int)dto.Severity,
                    Category = (Kanban.Collector.Core.Models.DefectCategory)(int)dto.Category,
                    ShiftName = dto.ShiftName,
                    Count = dto.Count,
                    Timestamp = dto.Timestamp,
                }).ToList();

        return [];
    }

    // ──────────── ISnEventStore（SN 序列号追溯） ────────────

    /// <summary>SN 采集写入：Local 委托本地 SnEventStore；Remote 模式下采集只在 Collector 发生，忽略（防误用）。</summary>
    public void Append(SnEventRecord record)
    {
        if (!IsRemote)
        {
            _localSnStore.Append(record);
            return;
        }
        _logger.LogDebug("Remote 模式下忽略 SN 采集写入（采集只在 Collector 进程发生）SN={Sn}", record.Sn);
    }

    public List<SnEventRecord> QueryBySn(string sn)
    {
        if (!IsRemote)
            return _localSnStore.QueryBySn(sn);
        var response = QueryRemoteSn(new SnEventQueryRequest { Sn = sn });
        return response.Items.Select(ToEntity).ToList();
    }

    public (int Total, List<SnEventRecord> Items) QueryByWorkOrder(int workOrderId, int page, int pageSize)
    {
        if (!IsRemote)
            return _localSnStore.QueryByWorkOrder(workOrderId, page, pageSize);
        var response = QueryRemoteSn(new SnEventQueryRequest { WorkOrderId = workOrderId, Page = page, PageSize = pageSize });
        return (response.Total, response.Items.Select(ToEntity).ToList());
    }

    public (int Total, List<SnEventRecord> Items) QueryByTimeRange(
        string? deviceId, DateTime from, DateTime to, int page, int pageSize)
    {
        if (!IsRemote)
            return _localSnStore.QueryByTimeRange(deviceId, from, to, page, pageSize);
        var response = QueryRemoteSn(new SnEventQueryRequest
        {
            DeviceId = deviceId,
            From = from == DateTime.MinValue ? null : from,
            To = to == DateTime.MaxValue ? null : to,
            Page = page,
            PageSize = pageSize,
        });
        return (response.Total, response.Items.Select(ToEntity).ToList());
    }

    public SnEventStoreDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => IsRemote ? new SnEventStoreDiagnosticsSnapshot() : _localSnStore.GetDiagnosticsSnapshot();

    /// <summary>
    /// 历史写入诊断：Remote 模式写入发生在 Collector 侧，本地无写入队列，返回空快照；
    /// Local 模式直接透传本地 HistoryService。与 SnEventStore 诊断的 IsRemote 分支同模式。
    /// 显式接口实现：与下方 SnEventStore 同名无参方法仅返回类型不同，无法隐式共存。
    /// </summary>
    HistoryDiagnosticsSnapshot IHistoryService.GetDiagnosticsSnapshot()
        => IsRemote ? new HistoryDiagnosticsSnapshot() : _local.GetDiagnosticsSnapshot();

    /// <summary>
    /// Remote SN 查询同步封装：Task.Run 转入线程池执行（SignalR InvokeAsync 在 UI 线程
    /// 直接 GetAwaiter().GetResult() 会死锁），10s 超时统一转本地化失败文案。
    /// </summary>
    private SnEventQueryResponse QueryRemoteSn(SnEventQueryRequest request)
    {
        using var cts = new CancellationTokenSource(RemoteCallTimeout);
        try
        {
            return Task.Run(
                () => _client.QuerySnEventsAsync(request, cts.Token).GetAwaiter().GetResult(),
                cts.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Remote SN 追溯查询超时 Sn={Sn} WorkOrderId={WorkOrderId}", request.Sn, request.WorkOrderId);
            throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote SN 追溯查询失败 Sn={Sn} WorkOrderId={WorkOrderId}", request.Sn, request.WorkOrderId);
            throw new InvalidOperationException(MainAPP.Resources.Strings.F_QueryFailed);
        }
    }

    private static SnEventRecord ToEntity(SnEventRecordDto dto) => new()
    {
        Id = dto.Id,
        Sn = dto.Sn,
        DeviceId = dto.DeviceId,
        DeviceName = dto.DeviceName,
        WorkOrderId = dto.WorkOrderId,
        ShiftName = dto.ShiftName,
        Result = dto.Result,
        Source = dto.Source,
        SourceId = dto.SourceId,
        BatchNo = dto.BatchNo,
        Timestamp = dto.Timestamp,
    };
}
