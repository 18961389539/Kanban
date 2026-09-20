using Kanban.Analysis;
using Kanban.Contracts;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly IHistoryService _history;
    private readonly IHistoryQueryExecutor _executor;
    private readonly DefectHistoryStore _defectStore;
    private readonly ILogger<HistoryQueryHandler> _logger;
    private readonly IMemoryCache? _cache;

    private const int ProductionCacheTtlSeconds = 12;
    private const int ProductionCacheBucketSeconds = 15;

    public HistoryQueryHandler(
        IHistoryService history,
        IHistoryQueryExecutor executor,
        DefectHistoryStore defectStore,
        ILogger<HistoryQueryHandler> logger,
        IMemoryCache? cache = null)
    {
        _history = history;
        _executor = executor;
        _defectStore = defectStore;
        _logger = logger;
        _cache = cache;
    }

    public async Task<HistoryQueryResponse> QueryAsync(HistoryQueryRequest request, CancellationToken cancellationToken = default)
    {
        // EF Core 查询为同步 IO，且批量落在后台线程；直接包 Task.Run 避免阻塞 SignalR 调度线程
        return await Task.Run(() => QueryCore(request), cancellationToken);
    }

    /// <summary>
    /// 产量窗口服务端分析：全量 SQL 留在 Collector，屏端只收 KPI + 15 分钟抽样。
    /// </summary>
    public async Task<ProductionWindowAnalysisDto> AnalyzeProductionWindowAsync(
        HistoryQueryRequest request, CancellationToken cancellationToken = default)
        => await Task.Run(() => AnalyzeProductionWindowCore(request), cancellationToken);

    private ProductionWindowAnalysisDto AnalyzeProductionWindowCore(HistoryQueryRequest request)
    {
        try
        {
            var (from, to, truncated) = NormalizeRange(request);
            if (_cache != null && !request.WorkOrderId.HasValue && !string.IsNullOrEmpty(request.DeviceId))
            {
                var key = ProductionCacheKey(request.DeviceId, request.ShiftName, from, to);
                if (_cache.TryGetValue(key, out ProductionWindowAnalysisDto? cached) && cached is not null)
                    return cached;
                var computed = ComputeProductionWindow(request, from, to, truncated);
                if (computed.ErrorCode == HistoryErrorCode.None)
                    _cache.Set(key, computed, TimeSpan.FromSeconds(ProductionCacheTtlSeconds));
                return computed;
            }
            return ComputeProductionWindow(request, from, to, truncated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "产量窗口分析失败 Device={DeviceId}", request.DeviceId);
            return new ProductionWindowAnalysisDto
            {
                ErrorCode = HistoryErrorCode.QueryFailed,
                Error = $"产量窗口分析失败: {ex.Message}",
            };
        }
    }

    private ProductionWindowAnalysisDto ComputeProductionWindow(
        HistoryQueryRequest request, DateTime from, DateTime to, bool truncated)
    {
        List<ProductionLogDto> logs;
        List<ProductionLogDto> baseline;

        if (request.WorkOrderId.HasValue)
        {
            var all = (_history.QueryProductionLogsByWorkOrder(request.WorkOrderId.Value) ?? [])
                .Select(ToDto)
                .ToList();
            logs = all.Where(p => p.Timestamp >= from && p.Timestamp <= to).ToList();
            var baselineFrom = from.AddDays(-1);
            baseline = all.Where(p => p.Timestamp >= baselineFrom && p.Timestamp < from).ToList();
        }
        else
        {
            var spanFrom = from.AddDays(-1);
            List<ProductionLogDto> sampled;
            try
            {
                sampled = (_executor.QueryProductionLogsSampled15Min(spanFrom, to, request.DeviceId, request.ShiftName) ?? [])
                    .Select(ToDto)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "产量 15 分钟抽样失败，回退两次 Strict");
                var window = (_executor.QueryProductionLogsStrict(from, to, request.DeviceId, request.ShiftName) ?? [])
                    .Select(ToDto)
                    .ToList();
                var baseLogs = (_executor.QueryProductionLogsStrict(spanFrom, from, request.DeviceId, request.ShiftName) ?? [])
                    .Select(ToDto)
                    .ToList();
                sampled = window.Concat(baseLogs).OrderBy(p => p.Timestamp).ToList();
            }
            logs = sampled.Where(p => p.Timestamp >= from && p.Timestamp <= to).ToList();
            baseline = sampled.Where(p => p.Timestamp >= spanFrom && p.Timestamp < from).ToList();
        }

        var (ok, ng) = ProductionWindowMetrics.SumWindowProduction(logs, baseline, from);
        var compact = ProductionWindowMetrics.Sample15Min(logs);
        var chart = ProductionWindowMetrics.BuildChartData(logs);
        return new ProductionWindowAnalysisDto
        {
            Truncated = truncated,
            Ok = ok,
            Ng = ng,
            QualityRate = Kanban.Analysis.OeeCalculator.CalculateQualityRate(ok, ng),
            ChartPoints = chart
                .Select(p => new ProductionChartPointDto { Time = p.Time, Ok = p.Ok, Ng = p.Ng })
                .ToList(),
            CompactLogs = compact,
        };
    }

    private static string ProductionCacheKey(string deviceId, string? shiftName, DateTime from, DateTime to)
    {
        var ticks = TimeSpan.FromSeconds(ProductionCacheBucketSeconds).Ticks;
        var bucket = new DateTime(to.Ticks / ticks * ticks, to.Kind);
        return $"prodwin|{deviceId}|{shiftName}|{from.Ticks}|{bucket.Ticks}";
    }

    /// <summary>
    /// 报警窗口服务端统计：全量 SQL 留在 Collector，屏端只收 KPI / Top / 最近事件。
    /// </summary>
    public async Task<AlarmWindowStatsDto> AnalyzeAlarmWindowAsync(
        HistoryQueryRequest request, CancellationToken cancellationToken = default)
        => await Task.Run(() => AnalyzeAlarmWindowCore(request), cancellationToken);

    private AlarmWindowStatsDto AnalyzeAlarmWindowCore(HistoryQueryRequest request)
    {
        try
        {
            var (from, to, truncated) = NormalizeRange(request);
            var todayStart = to.Date;
            var yesterdayStart = todayStart.AddDays(-1);
            var spanFrom = from < yesterdayStart ? from : yesterdayStart;
            var events = (_executor.QueryAlarmEventsStrict(spanFrom, to, request.DeviceId, request.ShiftName) ?? [])
                .Select(ToDto)
                .ToList();
            if (!string.IsNullOrWhiteSpace(request.AlarmName))
                events = events.Where(e => e.AlarmName == request.AlarmName).ToList();
            var dto = AlarmWindowMetrics.Build(events, from, to, truncated);
            return dto;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "报警窗口统计失败 Device={DeviceId}", request.DeviceId);
            return new AlarmWindowStatsDto
            {
                ErrorCode = HistoryErrorCode.QueryFailed,
                Error = $"报警窗口统计失败: {ex.Message}",
            };
        }
    }

    public async Task<StatusWindowAnalysisDto> AnalyzeStatusWindowAsync(
        HistoryQueryRequest request, CancellationToken cancellationToken = default)
        => await Task.Run(() => AnalyzeStatusWindowCore(request), cancellationToken);

    private StatusWindowAnalysisDto AnalyzeStatusWindowCore(HistoryQueryRequest request)
    {
        try
        {
            var (from, to, truncated) = NormalizeRange(request);
            if (string.IsNullOrEmpty(request.DeviceId))
                return new StatusWindowAnalysisDto { Truncated = truncated };

            var trans = (_executor.QueryStatusTransitionsStrict(request.DeviceId, from, to, request.ShiftName) ?? [])
                .Select(ToDto)
                .ToList();
            var lastBefore = _executor.GetLatestStatusBeforeStrict(request.DeviceId, from, request.ShiftName);
            var initialState = lastBefore is null ? 1 : lastBefore.CurrentState;
            var now = DateTime.Now;
            var durations = StatusWindowMetrics.CalculateStateDurations(trans, from, to, initialState, now);
            var daily = StatusWindowMetrics.BuildDailyDurations(trans, from, to, initialState, now);
            var segments = StatusWindowMetrics.BuildSegments(trans, from, to, initialState, now);
            return new StatusWindowAnalysisDto
            {
                Truncated = truncated,
                InitialState = initialState,
                TotalCount = trans.Count,
                RunSeconds = durations.RunTime,
                AlarmSeconds = durations.AlarmTime,
                PausedSeconds = durations.PausedTime,
                OfflineSeconds = durations.OfflineTime,
                Daily = daily,
                Segments = segments,
                ShiftNames = trans.Select(t => t.ShiftName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "状态窗口分析失败 Device={DeviceId}", request.DeviceId);
            return new StatusWindowAnalysisDto
            {
                ErrorCode = HistoryErrorCode.QueryFailed,
                Error = $"状态窗口分析失败: {ex.Message}",
            };
        }
    }

    public async Task<ReviewWindowAnalysisDto> AnalyzeReviewWindowAsync(
        HistoryQueryRequest request, CancellationToken cancellationToken = default)
        => await Task.Run(() => AnalyzeReviewWindowCore(request), cancellationToken);

    private ReviewWindowAnalysisDto AnalyzeReviewWindowCore(HistoryQueryRequest request)
    {
        try
        {
            var (from, to, truncated) = NormalizeRange(request);
            if (string.IsNullOrEmpty(request.DeviceId))
                return new ReviewWindowAnalysisDto { Truncated = truncated };

            var comparisonFrom = from - (to - from);
            var windowProd = AnalyzeProductionWindowCore(request);
            if (windowProd.ErrorCode != HistoryErrorCode.None)
            {
                return new ReviewWindowAnalysisDto
                {
                    ErrorCode = windowProd.ErrorCode,
                    Error = windowProd.Error,
                    Truncated = windowProd.Truncated,
                };
            }
            var compareProd = AnalyzeProductionWindowCore(request with
            {
                QueryType = HistoryQueryType.ProductionLog,
                From = comparisonFrom,
                To = from,
            });

            var trans = (_executor.QueryStatusTransitionsStrict(request.DeviceId, from, to, request.ShiftName) ?? [])
                .Select(ToDto)
                .ToList();
            var compTrans = (_executor.QueryStatusTransitionsStrict(request.DeviceId, comparisonFrom, from, request.ShiftName) ?? [])
                .Select(ToDto)
                .ToList();
            var alarms = (_executor.QueryAlarmEventsStrict(from, to, request.DeviceId, request.ShiftName) ?? [])
                .Select(ToDto)
                .ToList();
            var compAlarms = (_executor.QueryAlarmEventsStrict(comparisonFrom, from, request.DeviceId, request.ShiftName) ?? [])
                .Select(ToDto)
                .ToList();

            List<DefectSnapshotRecordDto> defects = [];
            List<DefectSnapshotRecordDto> compDefects = [];
            if (_defectStore is not null)
            {
                defects = _defectStore.QueryHourlyBounds(from, to, request.DeviceId).Select(ToDto).ToList();
                compDefects = _defectStore.QueryHourlyBounds(comparisonFrom, from, request.DeviceId).Select(ToDto).ToList();
            }

            var lastBefore = _executor.GetLatestStatusBeforeStrict(request.DeviceId, from, request.ShiftName);
            var initialState = lastBefore is null ? 1 : lastBefore.CurrentState;
            var compLastBefore = _executor.GetLatestStatusBeforeStrict(request.DeviceId, comparisonFrom, request.ShiftName);
            var compInitial = compLastBefore is null ? 1 : compLastBefore.CurrentState;
            var now = DateTime.Now;
            var effectiveTo = to > now ? now : to;
            var prod = windowProd.CompactLogs.ToList();
            var durations = StatusWindowMetrics.CalculateStateDurations(trans, from, effectiveTo, initialState, now);
            var compDurations = StatusWindowMetrics.CalculateStateDurations(compTrans, comparisonFrom, from, compInitial, now);
            var defectRows = ReviewWindowMetrics.BuildDefectConcentrations(defects, from, to);
            var (longestName, longestHours) = ReviewWindowMetrics.FindLongestAlarm(alarms, effectiveTo, now);
            var (peakHour, peakOk, valleyHour, valleyOk) = ReviewWindowMetrics.FindPeakValley(prod, from, to);

            return new ReviewWindowAnalysisDto
            {
                Truncated = truncated || windowProd.Truncated || compareProd.Truncated,
                Ok = windowProd.Ok,
                Ng = windowProd.Ng,
                QualityRate = windowProd.QualityRate,
                RunSeconds = durations.RunTime,
                AlarmSeconds = durations.AlarmTime,
                PausedSeconds = durations.PausedTime,
                OfflineSeconds = durations.OfflineTime,
                AlarmTriggered = alarms.Count(e => e.EventType == AlarmEventType.Triggered),
                AlarmRecovered = alarms.Count(e => e.EventType == AlarmEventType.Recovered),
                AlarmPending = AlarmWindowMetrics.CountPending(alarms),
                LongestAlarmName = longestName,
                LongestAlarmHours = longestHours,
                PeakHour = peakHour,
                PeakOk = peakOk,
                ValleyHour = valleyHour,
                ValleyOk = valleyOk,
                BaselineOutput = compareProd.Ok + compareProd.Ng,
                BaselineQuality = compareProd.QualityRate,
                BaselineRunSeconds = compDurations.RunTime,
                BaselineAlarmSeconds = compDurations.AlarmTime,
                PreviousAlarmTriggered = compAlarms.Count(e => e.EventType == AlarmEventType.Triggered),
                PreviousDefectCount = ReviewWindowMetrics.BuildDefectConcentrations(compDefects, comparisonFrom, from).Sum(d => d.Count),
                ChartPoints = windowProd.ChartPoints,
                Alarms = ReviewWindowMetrics.AnalyzeAlarms(alarms, prod, effectiveTo, now),
                Timeline = ReviewWindowMetrics.BuildTimeline(trans, alarms, prod, initialState, from, effectiveTo),
                Defects = defectRows,
                Shifts = ReviewWindowMetrics.BuildShiftSummaries(prod, alarms),
                CompactLogs = prod,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "复盘窗口分析失败 Device={DeviceId}", request.DeviceId);
            return new ReviewWindowAnalysisDto
            {
                ErrorCode = HistoryErrorCode.QueryFailed,
                Error = $"复盘窗口分析失败: {ex.Message}",
            };
        }
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
        // 上限足够复盘页多设备批查，同时挡住恶意/误用的批量全表请求。
        var queries = request.Queries;
        if (queries.Count > HistoryQueryLimits.MaxBatchQueries)
        {
            _logger.LogWarning(
                "批量历史查询子查询数 {Count} 超过上限 {Max}，已截断处理（防资源耗尽）",
                queries.Count, HistoryQueryLimits.MaxBatchQueries);
            queries = queries.Take(HistoryQueryLimits.MaxBatchQueries).ToList();
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
            _logger.LogInformation("批量历史查询完成：{Count} 个子查询，总耗时 {Elapsed}ms", queries.Count, sw.ElapsedMilliseconds);
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
            var (from, to, truncated) = NormalizeRange(request);
            var deviceId = request.DeviceId;

            // 工单过滤：记录量有限，全量拉取（WorkOrderId 语义，翻页路径不适用）
            if (request.WorkOrderId.HasValue)
            {
                var logs = _history.QueryProductionLogsByWorkOrder(request.WorkOrderId.Value)
                    .Select(ToDto)
                    .ToList();
                return BuildProduction(logs, request, isWindowTruncated: truncated);
            }

            return request.QueryType switch
            {
                HistoryQueryType.ProductionLog => BuildProduction(
                    _executor.QueryProductionLogsStrict(from, to, deviceId, request.ShiftName).Select(ToDto).ToList(), request, isWindowTruncated: truncated),
                HistoryQueryType.AlarmEvent => BuildAlarm(
                    _executor.QueryAlarmEventsStrict(from, to, deviceId, request.ShiftName).Select(ToDto).ToList(), request, isWindowTruncated: truncated),
                HistoryQueryType.StatusTransition => deviceId is null
                    ? Empty(request, truncated)
                    : BuildStatus(_executor.QueryStatusTransitionsStrict(deviceId, from, to, request.ShiftName).Select(ToDto).ToList(), request, isWindowTruncated: truncated),
                HistoryQueryType.DefectSnapshot or HistoryQueryType.DefectSnapshotHourly
                    => QueryDefectSnapshotsAll(request, from, to, deviceId, truncated),
                _ => Empty(request, truncated),
            };
        }
        catch (Exception ex)
        {
            // 与单查 QueryCore 相同的结构化错误语义：客户端据此区分"真实空数据"与"查询失败"
            _logger.LogError(ex, "批量历史查询失败 QueryType={QueryType} Device={DeviceId}", request.QueryType, request.DeviceId);
            return Error(request, ex);
        }
    }

    /// <summary>
    /// 缺陷快照批量查询：SQL 分组下推（每组首末 + 窗口前基线），不拉全量。
    /// 缺陷快照是高频累计值（每日数十万条），帕累托增量差分只需边界记录——
    /// 全量翻页会让复盘页每次查询拉 2 天 27 万条（数百页深分页），是复盘页耗时根因。
    /// DefectSnapshotHourly：按小时分组下推（每组小时末值 + 基线），供缺陷集中度逐小时差分。
    /// </summary>
    private HistoryQueryResponse QueryDefectSnapshotsAll(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId, bool truncated)
    {
        if (deviceId is null)
            return Empty(request, truncated);

        var snapshots = request.QueryType == HistoryQueryType.DefectSnapshotHourly
            ? _defectStore.QueryHourlyBounds(from, to, deviceId)
            : _defectStore.QueryWindowBounds(from, to, deviceId);
        return BuildDefect(snapshots.Select(ToDto).ToList(), request, isWindowTruncated: truncated);
    }

    private HistoryQueryResponse QueryCore(HistoryQueryRequest request)
    {
        try
        {
            var (from, to, truncated) = NormalizeRange(request);
            var deviceId = request.DeviceId;

            return request.QueryType switch
            {
                HistoryQueryType.ProductionLog => QueryProductionLogs(request, from, to, deviceId, truncated),
                HistoryQueryType.AlarmEvent => QueryAlarmEvents(request, from, to, deviceId, truncated),
                HistoryQueryType.StatusTransition => QueryStatusTransitions(request, from, to, deviceId, truncated),
                HistoryQueryType.DefectSnapshot => QueryDefectSnapshots(request, from, to, deviceId, truncated),
                // 小时分组下推语义（与批量路径 QueryCoreAll 一致，不再静默落空）
                HistoryQueryType.DefectSnapshotHourly => QueryDefectSnapshotsAll(request, from, to, deviceId, truncated),
                _ => Empty(request, truncated),
            };
        }
        catch (Exception ex)
        {
            // 结构化错误：客户端据此区分"真实空数据"与"查询失败"，而非把故障当空结果显示。
            // Error 只承载调试细节（仅进日志），用户可见文案由客户端按 ErrorCode 本地化渲染，
            // 避免多语言界面（en/ja）收到中文回显。
            _logger.LogError(ex, "历史查询失败 QueryType={QueryType} Device={DeviceId}", request.QueryType, request.DeviceId);
            return Error(request, ex);
        }
    }

    // ──────────── 各类型查询（均服务端 SQL 分页 + Strict 异常上抛，数据库故障不再伪装为空数据） ────────────

    private HistoryQueryResponse QueryProductionLogs(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId, bool truncated)
    {
        // 按工单查询：工单关联记录量有限，全量拉取
        if (request.WorkOrderId.HasValue)
        {
            var items = _history.QueryProductionLogsByWorkOrder(request.WorkOrderId.Value)
                .Select(ToDto)
                .ToList();
            return BuildProduction(items, request, isWindowTruncated: truncated);
        }

        // 服务端分页下推（SQL Skip/Take + Count）：历史查询不再全量 ToList 传输百万级记录；
        // LatestFirst 同样下推为 SQL 层 OrderByDescending().Take(1)
        if (request.LatestFirst)
        {
            var latest = _history.QueryLatestProductionLog(from, to, deviceId, request.ShiftName);
            var items = latest is null
                ? new List<ProductionLogDto>()
                : new List<ProductionLogDto> { ToDto(latest) };
            return BuildProduction(items, request, isWindowTruncated: truncated);
        }

        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        var (pageItems, total) = _history.QueryProductionLogsPaged(
            from, to, deviceId, request.ShiftName, page, pageSize);
        return BuildProduction(pageItems.Select(ToDto).ToList(), request, total, truncated);
    }

    private HistoryQueryResponse QueryAlarmEvents(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId, bool truncated)
    {
        // AlarmId 精确过滤（GetLatestAlarmEvent 语义）：SQL 层 Where(AlarmId)+OrderByDescending+Take(1)，
        // 不再全量拉窗口内所有报警后在内存过滤；且与 Local 模式 GetLatestAlarmEvent（全局最新）语义对齐。
        if (!string.IsNullOrWhiteSpace(request.AlarmId))
        {
            var latest = _history.GetLatestAlarmEventStrict(request.AlarmId);
            var items = latest is null
                ? new List<AlarmEventRecordDto>()
                : new List<AlarmEventRecordDto> { ToDto(latest) };
            return BuildAlarm(items, request, isWindowTruncated: truncated);
        }

        // 服务端 SQL 分页（Count + OrderByDescending + Skip/Take），替代全量 ToList
        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        if (request.LatestFirst)
        {
            var (latestItems, _) = _history.QueryAlarmEventsPaged(from, to, deviceId, request.ShiftName, 1, 1, request.AlarmName);
            return BuildAlarm(latestItems.Select(ToDto).ToList(), request, totalOverride: 1, isWindowTruncated: truncated);
        }

        var (pageItems, total) = _history.QueryAlarmEventsPaged(from, to, deviceId, request.ShiftName, page, pageSize, request.AlarmName);
        return BuildAlarm(pageItems.Select(ToDto).ToList(), request, total, truncated);
    }

    private HistoryQueryResponse QueryStatusTransitions(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId, bool truncated)
    {
        if (deviceId is null)
            return Empty(request, truncated);

        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        if (request.LatestFirst)
        {
            var (latestItems, _) = _history.QueryStatusTransitionsPaged(deviceId, from, to, request.ShiftName, 1, 1);
            return BuildStatus(latestItems.Select(ToDto).ToList(), request, totalOverride: 1, isWindowTruncated: truncated);
        }

        var (pageItems, total) = _history.QueryStatusTransitionsPaged(deviceId, from, to, request.ShiftName, page, pageSize);
        return BuildStatus(pageItems.Select(ToDto).ToList(), request, total, truncated);
    }

    private HistoryQueryResponse QueryDefectSnapshots(HistoryQueryRequest request, DateTime from, DateTime to, string? deviceId, bool truncated)
    {
        if (deviceId is null)
            return Empty(request, truncated);

        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        if (request.LatestFirst)
        {
            var (latestItems, _) = _history.QueryDefectSnapshotsPaged(from, to, deviceId, 1, 1);
            return BuildDefect(latestItems.Select(ToDto).ToList(), request, totalOverride: 1, isWindowTruncated: truncated);
        }

        var (pageItems, total) = _history.QueryDefectSnapshotsPaged(from, to, deviceId, page, pageSize);
        return BuildDefect(pageItems.Select(ToDto).ToList(), request, total, truncated);
    }

    // ──────────── 工具 ────────────

    private (DateTime From, DateTime To, bool Truncated) NormalizeRange(HistoryQueryRequest request)
    {
        // 未指定范围 → 最近 24h（替代历史 MinValue..MaxValue 全表扫描）。
        var to = request.To ?? DateTime.Now;
        var from = request.From ?? to - TimeSpan.FromHours(HistoryQueryLimits.DefaultQueryWindowHours);

        // 兜底：客户端传反 from/to（from > to）时交换，避免静默返回空结果（服务端不依赖客户端 UI 校验）。
        if (from > to)
            (from, to) = (to, from);

        // 跨度超过上限 → 截断为最近窗口（复盘页正常窗口内，同时挡住恶意全表请求）；
        // 截断标记回传给客户端（IsWindowTruncated），客户端可提示"结果已按最近 N 天截断"，避免对账误判。
        var maxWindow = TimeSpan.FromDays(HistoryQueryLimits.MaxQueryWindowDays);
        if (to - from > maxWindow)
        {
            _logger.LogWarning(
                "历史查询时间范围 {From:o}~{To:o} 超过上限 {Days} 天，已截断为最近窗口（防全表扫描）",
                from, to, HistoryQueryLimits.MaxQueryWindowDays);
            from = to - maxWindow;
            return (from, to, true);
        }
        return (from, to, false);
    }

    /// <summary>统一响应装配：回显归一化后的 Page/PageSize（而非客户端原始值），并透传窗口截断标记。</summary>
    private static HistoryQueryResponse BuildResponse(
        HistoryQueryRequest request,
        int? totalOverride,
        bool isWindowTruncated,
        List<ProductionLogDto>? productionLogs = null,
        List<AlarmEventRecordDto>? alarmEvents = null,
        List<StatusTransitionRecordDto>? statusTransitions = null,
        List<DefectSnapshotRecordDto>? defectSnapshots = null)
    {
        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        var count = productionLogs?.Count ?? alarmEvents?.Count ?? statusTransitions?.Count ?? defectSnapshots?.Count ?? 0;
        return new HistoryQueryResponse
        {
            // 分页查询时 totalOverride 为服务端 Count（全量总数）；否则等于本页 items 数量（未分页类型）
            Total = totalOverride ?? count,
            Page = page,
            PageSize = pageSize,
            IsWindowTruncated = isWindowTruncated,
            ProductionLogs = productionLogs ?? [],
            AlarmEvents = alarmEvents ?? [],
            StatusTransitions = statusTransitions ?? [],
            DefectSnapshots = defectSnapshots ?? [],
        };
    }

    private static HistoryQueryResponse BuildProduction(List<ProductionLogDto> items, HistoryQueryRequest request, int? totalOverride = null, bool isWindowTruncated = false)
        => BuildResponse(request, totalOverride, isWindowTruncated, productionLogs: items);

    private static HistoryQueryResponse BuildAlarm(List<AlarmEventRecordDto> items, HistoryQueryRequest request, int? totalOverride = null, bool isWindowTruncated = false)
        => BuildResponse(request, totalOverride, isWindowTruncated, alarmEvents: items);

    private static HistoryQueryResponse BuildStatus(List<StatusTransitionRecordDto> items, HistoryQueryRequest request, int? totalOverride = null, bool isWindowTruncated = false)
        => BuildResponse(request, totalOverride, isWindowTruncated, statusTransitions: items);

    private static HistoryQueryResponse BuildDefect(List<DefectSnapshotRecordDto> items, HistoryQueryRequest request, int? totalOverride = null, bool isWindowTruncated = false)
        => BuildResponse(request, totalOverride, isWindowTruncated, defectSnapshots: items);

    private static HistoryQueryResponse Empty(HistoryQueryRequest request, bool isWindowTruncated = false)
        => BuildResponse(request, 0, isWindowTruncated);

    private static HistoryQueryResponse Error(HistoryQueryRequest request, Exception ex)
    {
        var (page, pageSize) = HistoryPagination.Normalize(request.Page, request.PageSize);
        return new HistoryQueryResponse
        {
            ErrorCode = HistoryErrorCode.QueryFailed,
            Page = page,
            PageSize = pageSize,
            Error = $"历史查询失败: {ex.Message}",
        };
    }

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
