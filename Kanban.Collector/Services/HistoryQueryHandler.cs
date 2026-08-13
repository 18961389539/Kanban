using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Core.Entities;
using Kanban.Core.Services;
using Microsoft.Extensions.Logging;
using DeviceStatus = Kanban.Contracts.Enums.DeviceStatus;
using DefectSeverity = Kanban.Contracts.Enums.DefectSeverity;
using DefectCategory = Kanban.Contracts.Enums.DefectCategory;
using AlarmEventType = Kanban.Contracts.Enums.AlarmEventType;

namespace Kanban.Collector.Services;

/// <summary>
/// 历史查询服务端实现：把 <see cref="HistoryQueryRequest"/> 分发到 Core 的 Store 查询，
/// 实体 → DTO 映射后返回。展示端（MainAPP）不再直接持有 SQLite，统一经此查询。
/// 语义约定：返回范围内全部匹配记录（客户端内存分页），LatestFirst=true 时只返回最新一条。
/// </summary>
public sealed class HistoryQueryHandler
{
    /// <summary>单次批量查询子查询数上限：防无鉴权/异常客户端一次发起数十个全表查询拖垮 SQLite（资源耗尽向量）。</summary>
    private const int MaxBatchQueries = 32;
    /// <summary>单查询最大时间跨度：超限截断为最近窗口，防 MinValue..MaxValue 全表扫描。</summary>
    private static readonly TimeSpan MaxQueryWindow = TimeSpan.FromDays(7);
    /// <summary>未指定时间范围时的默认窗口（最近 24 小时），替代全表（MinValue..MaxValue）。</summary>
    private static readonly TimeSpan DefaultQueryWindow = TimeSpan.FromHours(24);

    private readonly IHistoryService _history;
    private readonly IHistoryQueryExecutor _executor;
    private readonly DefectHistoryStore _defectStore;
    private readonly ILogger<HistoryQueryHandler> _logger;

    public HistoryQueryHandler(
        IHistoryService history,
        IHistoryQueryExecutor executor,
        DefectHistoryStore defectStore,
        ILogger<HistoryQueryHandler> logger)
    {
        _history = history;
        _executor = executor;
        _defectStore = defectStore;
        _logger = logger;
    }

    public async Task<HistoryQueryResponse> QueryAsync(HistoryQueryRequest request, CancellationToken cancellationToken = default)
    {
        // EF Core 查询为同步 IO，且批量落在后台线程；直接包 Task.Run 避免阻塞 SignalR 调度线程
        return await Task.Run(() => QueryCore(request), cancellationToken);
    }

    /// <summary>
    /// 批量历史查询：多个子查询一次往返。每个子查询执行服务端全量查询（一次 SQL），
    /// 客户端无需再逐页拉取——生产复盘页多设备批查从「N 次往返 × 每往返多页」降为 1 次。
    /// 任一子查询失败时该项携带 ErrorCode=QueryFailed，其余子查询结果不受影响。
    /// </summary>
    public async Task<BatchHistoryQueryResponse> QueryBatchAsync(
        BatchHistoryQueryRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Queries.Count == 0)
            return new BatchHistoryQueryResponse { Results = [] };

        // 子查询数上限：超出截断（保留前 N 个），不拒绝整个请求——正常客户端一次 3~10 个，
        // 32 上限足够复盘页多设备批查，同时挡住恶意/误用的批量全表请求。
        var queries = request.Queries;
        if (queries.Count > MaxBatchQueries)
        {
            _logger.LogWarning(
                "批量历史查询子查询数 {Count} 超过上限 {Max}，已截断处理（防资源耗尽）",
                queries.Count, MaxBatchQueries);
            queries = queries.Take(MaxBatchQueries).ToList();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("批量历史查询开始：{Count} 个子查询", queries.Count);
        return await Task.Run(() =>
        {
            var results = new List<HistoryQueryResponse>(queries.Count);
            foreach (var query in queries)
            {
                var qsw = System.Diagnostics.Stopwatch.StartNew();
                var result = QueryCoreAll(query);
                qsw.Stop();
                _logger.LogInformation("批量子查询完成 Type={QueryType} Device={DeviceId} 耗时 {Elapsed}ms Total={Total} ErrorCode={ErrorCode}",
                    query.QueryType, query.DeviceId, qsw.ElapsedMilliseconds, result.Total, result.ErrorCode);
                results.Add(result);
            }
            sw.Stop();
            _logger.LogInformation("批量历史查询完成：{Count} 个子查询，总耗时 {Elapsed}ms", request.Queries.Count, sw.ElapsedMilliseconds);
            return new BatchHistoryQueryResponse { Results = results };
        }, cancellationToken);
    }

    /// <summary>
    /// 批量聚合查询（一次往返）：每个子查询走 Strict 全量（一次 SQL），不做逐页 Skip/Take。
    /// ⚠️ 深分页教训：逐页翻页在大窗口下是 O(n²) 深分页（7 天数万条 × 多设备 × 3 类 ≈ 数百次 SQL，
    /// 且 Skip 越深越慢），服务端 30s 内无法完成 → 客户端 InvokeAsync 超时（TaskCanceledException）。
    /// 全量聚合是复盘页的固有语义（必须全量数据做汇总），与查询页的浏览分页语义不同。
    /// 缺陷快照数据量小（无 Strict 全量接口），保留分页翻页聚合。
    /// </summary>
    private HistoryQueryResponse QueryCoreAll(HistoryQueryRequest request)
    {
        try
        {
            var (from, to) = NormalizeRange(request);
            var deviceId = request.DeviceId;

            // 工单过滤：记录量有限，全量拉取（WorkOrderId 语义，翻页路径不适用）
            if (request.WorkOrderId.HasValue)
            {
                var logs = _history.QueryProductionLogsByWorkOrder(request.WorkOrderId.Value)
                    .Select(ToDto)
                    .ToList();
                return Build(logs, request);
            }

            return request.QueryType switch
            {
                HistoryQueryType.ProductionLog => Build(
                    _executor.QueryProductionLogsStrict(from, to, deviceId, request.ShiftName).Select(ToDto).ToList(), request),
                HistoryQueryType.AlarmEvent => Build(
                    _executor.QueryAlarmEventsStrict(from, to, deviceId, request.ShiftName).Select(ToDto).ToList(), request),
                HistoryQueryType.StatusTransition => deviceId is null
                    ? Empty(request)
                    : Build(_executor.QueryStatusTransitionsStrict(deviceId, from, to, request.ShiftName).Select(ToDto).ToList(), request),
                HistoryQueryType.DefectSnapshot => QueryDefectSnapshotsAll(request, from, to, deviceId),
                _ => Empty(request),
            };
        }
        catch (Exception ex)
        {
            // 与单查 QueryCore 相同的结构化错误语义：客户端据此区分"真实空数据"与"查询失败"
            _logger.LogError(ex, "批量历史查询失败 QueryType={QueryType} Device={DeviceId}", request.QueryType, request.DeviceId);
            return new HistoryQueryResponse
            {
                ErrorCode = HistoryErrorCode.QueryFailed,
                Page = request.Page,
                PageSize = request.PageSize,
                Error = $"历史查询失败: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// 缺陷快照批量查询：SQL 分组下推（每组首末 + 窗口前基线），不拉全量。
    /// 缺陷快照是高频累计值（每日数十万条），帕累托增量差分只需边界记录——
    /// 全量翻页会让复盘页每次查询拉 2 天 27 万条（数百页深分页），是复盘页耗时根因。
    /// DefectSnapshotHourly：按小时分组下推（每组小时末值 + 基线），供缺陷集中度逐小时差分。
    /// </summary>
    private HistoryQueryResponse QueryDefectSnapshotsAll(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        if (deviceId is null)
            return Empty(request);

        var snapshots = request.QueryType == HistoryQueryType.DefectSnapshotHourly
            ? _defectStore.QueryHourlyBounds(from, to, deviceId)
            : _defectStore.QueryWindowBounds(from, to, deviceId);
        return Build(snapshots.Select(ToDto).ToList(), request);
    }

    private HistoryQueryResponse QueryCore(HistoryQueryRequest request)
    {
        try
        {
            var (from, to) = NormalizeRange(request);
            var deviceId = request.DeviceId;

            return request.QueryType switch
            {
                HistoryQueryType.ProductionLog => QueryProductionLogs(request, from, to, deviceId),
                HistoryQueryType.AlarmEvent => QueryAlarmEvents(request, from, to, deviceId),
                HistoryQueryType.StatusTransition => QueryStatusTransitions(request, from, to, deviceId),
                HistoryQueryType.DefectSnapshot => QueryDefectSnapshots(request, from, to, deviceId),
                _ => Empty(request),
            };
        }
        catch (Exception ex)
        {
            // 结构化错误：客户端据此区分"真实空数据"与"查询失败"，而非把故障当空结果显示。
            // Error 只承载调试细节（仅进日志），用户可见文案由客户端按 ErrorCode 本地化渲染，
            // 避免多语言界面（en/ja）收到中文回显。
            _logger.LogError(ex, "历史查询失败 QueryType={QueryType} Device={DeviceId}", request.QueryType, request.DeviceId);
            return new HistoryQueryResponse
            {
                ErrorCode = HistoryErrorCode.QueryFailed,
                Page = request.Page,
                PageSize = request.PageSize,
                Error = $"历史查询失败: {ex.Message}",
            };
        }
    }

    // ──────────── 各类型查询（均服务端 SQL 分页 + Strict 异常上抛，数据库故障不再伪装为空数据） ────────────

    private HistoryQueryResponse QueryProductionLogs(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        // 按工单查询：工单关联记录量有限，全量拉取
        if (request.WorkOrderId.HasValue)
        {
            var items = _history.QueryProductionLogsByWorkOrder(request.WorkOrderId.Value)
                .Select(ToDto)
                .ToList();
            return Build(items, request);
        }

        // 服务端分页下推（SQL Skip/Take + Count）：历史查询不再全量 ToList 传输百万级记录；
        // LatestFirst 同样下推为 SQL 层 OrderByDescending().Take(1)
        if (request.LatestFirst)
        {
            var latest = _history.QueryLatestProductionLog(from, to, deviceId, request.ShiftName);
            var items = latest is null
                ? new List<ProductionLogDto>()
                : new List<ProductionLogDto> { ToDto(latest) };
            return Build(items, request);
        }

        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        var (pageItems, total) = _history.QueryProductionLogsPaged(
            from, to, deviceId, request.ShiftName, page, pageSize);
        return Build(pageItems.Select(ToDto).ToList(), request, total);
    }

    private HistoryQueryResponse QueryAlarmEvents(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        // AlarmId 精确过滤（GetLatestAlarmEvent 语义）：走 Strict 路径，数据库故障向上抛（不伪装空数据）
        if (!string.IsNullOrWhiteSpace(request.AlarmId))
        {
            var latest = _history.QueryAlarmEventsStrict(from, to, deviceId, request.ShiftName)
                .Where(e => e.AlarmId == request.AlarmId)
                .OrderByDescending(e => e.EventTime)
                .FirstOrDefault();
            return Build(latest is null
                ? new List<AlarmEventRecordDto>()
                : new List<AlarmEventRecordDto> { ToDto(latest) }, request);
        }

        // 服务端 SQL 分页（Count + OrderByDescending + Skip/Take），替代全量 ToList
        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        if (request.LatestFirst)
        {
            var (latestItems, _) = _history.QueryAlarmEventsPaged(from, to, deviceId, request.ShiftName, 1, 1);
            var items = latestItems.Select(ToDto).ToList();
            return Build(items, request, totalOverride: 1);
        }

        var (pageItems, total) = _history.QueryAlarmEventsPaged(from, to, deviceId, request.ShiftName, page, pageSize);
        return Build(pageItems.Select(ToDto).ToList(), request, total);
    }

    private HistoryQueryResponse QueryStatusTransitions(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        if (deviceId is null)
            return Empty(request);

        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        if (request.LatestFirst)
        {
            var (latestItems, _) = _history.QueryStatusTransitionsPaged(deviceId, from, to, request.ShiftName, 1, 1);
            var items = latestItems.Select(ToDto).ToList();
            return Build(items, request, totalOverride: 1);
        }

        var (pageItems, total) = _history.QueryStatusTransitionsPaged(deviceId, from, to, request.ShiftName, page, pageSize);
        return Build(pageItems.Select(ToDto).ToList(), request, total);
    }

    private HistoryQueryResponse QueryDefectSnapshots(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId)
    {
        if (deviceId is null)
            return Empty(request);

        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        if (request.LatestFirst)
        {
            var (latestItems, _) = _history.QueryDefectSnapshotsPaged(from, to, deviceId, 1, 1);
            var items = latestItems.Select(ToDto).ToList();
            return Build(items, request, totalOverride: 1);
        }

        var (pageItems, total) = _history.QueryDefectSnapshotsPaged(from, to, deviceId, page, pageSize);
        return Build(pageItems.Select(ToDto).ToList(), request, total);
    }

    // ──────────── 工具 ────────────

    private (DateTime From, DateTime To) NormalizeRange(HistoryQueryRequest request)
    {
        // 未指定范围 → 最近 24h（替代历史 MinValue..MaxValue 全表扫描）；
        // 跨度超过 7 天 → 截断为最近 7 天窗口（复盘页正常窗口内，同时挡住恶意全表请求）。
        var to = request.To ?? DateTime.Now;
        var from = request.From ?? to - DefaultQueryWindow;
        if (to - from > MaxQueryWindow)
        {
            _logger.LogWarning(
                "历史查询时间范围 {From:o}~{To:o} 超过上限 {Days} 天，已截断为最近窗口（防全表扫描）",
                from, to, MaxQueryWindow.TotalDays);
            from = to - MaxQueryWindow;
        }
        return (from, to);
    }

    private static HistoryQueryResponse Build<T>(List<T> items, HistoryQueryRequest request, int? totalOverride = null)
    {
        var response = new HistoryQueryResponse
        {
            // 分页查询时 totalOverride 为服务端 Count（全量总数）；否则等于本页 items 数量（未分页类型）
            Total = totalOverride ?? items.Count,
            Page = request.Page,
            PageSize = request.PageSize,
        };
        switch (items)
        {
            case List<ProductionLogDto> logs:
                response = response with { ProductionLogs = logs };
                break;
            case List<AlarmEventRecordDto> alarms:
                response = response with { AlarmEvents = alarms };
                break;
            case List<StatusTransitionRecordDto> transitions:
                response = response with { StatusTransitions = transitions };
                break;
            case List<DefectSnapshotRecordDto> defects:
                response = response with { DefectSnapshots = defects };
                break;
        }
        return response;
    }

    private static HistoryQueryResponse Empty(HistoryQueryRequest request)
        => new()
        {
            Total = 0,
            Page = request.Page,
            PageSize = request.PageSize,
        };

    // ──────────── 实体 → DTO ────────────

    private static ProductionLogDto ToDto(ProductionLog e) => new()
    {
        Id = e.Id,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        ShiftName = e.ShiftName,
        WorkOrderId = e.WorkOrderId,
        OkProduction = e.OkProduction,
        NgProduction = e.NgProduction,
        StatusWord = e.StatusWord,
        Timestamp = e.Timestamp,
    };

    private static AlarmEventRecordDto ToDto(AlarmEventRecord e) => new()
    {
        Id = e.Id,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        AlarmId = e.AlarmId,
        AlarmName = e.AlarmName,
        PlcAddress = e.PlcAddress,
        EventType = (AlarmEventType)e.EventType,
        EventTime = e.EventTime,
        ShiftName = e.ShiftName,
    };

    private static StatusTransitionRecordDto ToDto(StatusTransitionRecord e) => new()
    {
        Id = e.Id,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        PreviousState = (DeviceStatus)e.PreviousState,
        CurrentState = (DeviceStatus)e.CurrentState,
        EventTime = e.EventTime,
        ShiftName = e.ShiftName,
    };

    private static DefectSnapshotRecordDto ToDto(DefectSnapshotRecord e) => new()
    {
        Id = e.Id,
        DeviceId = e.DeviceId,
        DeviceName = e.DeviceName,
        DefectId = e.DefectId,
        DefectName = e.DefectName,
        Severity = (DefectSeverity)e.Severity,
        Category = (DefectCategory)e.Category,
        ShiftName = e.ShiftName,
        Count = e.Count,
        Timestamp = e.Timestamp,
    };
}
