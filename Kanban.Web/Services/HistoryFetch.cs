using Kanban.Analysis;
using Kanban.Contracts;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Microsoft.AspNetCore.SignalR;

namespace Kanban.Web.Services;

/// <summary>
/// 历史数据拉取助手（历史查询页/报警中心共用）：
/// 服务端分页并发拉取全量（4 路并发；单页上限 500；10 万条上限截断）与 LatestFirst 单条语义。
/// 每页大小/最大页数收敛到 <see cref="HistoryQueryLimits"/>（跨进程契约单一来源）。
/// </summary>
public static class HistoryFetch
{
    /// <summary>
    /// 并发拉取全量：首页先取 Total，剩余页并发拉取（服务端单页上限 500）。
    /// 大窗口（数万条）下 WASM 单线程是瓶颈：SignalR 响应反序列化 + GC 会冻结 UI，
    /// 故每批之间让出主线程（渲染帧插入），并限制并发以降低同时到达的响应积压。
    /// 返回 (数据, 是否超出上限截断)。
    /// </summary>
    public static async Task<(List<T> Items, bool Truncated)> FetchAllAsync<T>(
        DashboardState dashboard,
        HistoryQueryType type,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        Func<HistoryQueryResponse, IReadOnlyList<T>> selector,
        CancellationToken ct = default)
    {
        const int concurrency = 4; // WASM 单线程：过高并发导致响应积压 + GC 停顿（实测 8 路下 7 天窗口 9 分钟）
        var truncated = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        async Task<HistoryQueryResponse> QueryAsync(int page) => await dashboard.QueryHistoryAsync(new HistoryQueryRequest
        {
            QueryType = type,
            From = from,
            To = to,
            DeviceId = deviceId,
            ShiftName = shiftName,
            Page = page,
            PageSize = HistoryQueryLimits.MaxPageSize,
        }, ct);

        var first = await QueryAsync(1);
        if (first.ErrorCode != HistoryErrorCode.None)
            throw new InvalidOperationException(first.Error ?? "history query failed");
        // 服务端时间窗口截断（请求跨度超过 31 天被收窄）同样视为结果不完整
        if (first.IsWindowTruncated)
        {
            truncated = true;
            Console.WriteLine($"[HistoryFetch] {type} 时间窗口被服务端截断");
        }

        var all = new List<T>(first.Total);
        all.AddRange(selector(first));

        var totalPages = ProductionAnalysis.CalcTotalPages(first.Total, HistoryQueryLimits.MaxPageSize);
        if (totalPages > HistoryQueryLimits.MaxFetchAllPages)
        {
            truncated = true;
            totalPages = HistoryQueryLimits.MaxFetchAllPages;
        }
        for (var start = 2; start <= totalPages; start += concurrency)
        {
            var batch = Enumerable.Range(start, Math.Min(concurrency, totalPages - start + 1)).Select(QueryAsync);
            var results = await Task.WhenAll(batch);
            foreach (var r in results)
            {
                if (r.ErrorCode != HistoryErrorCode.None)
                    throw new InvalidOperationException(r.Error ?? "history query failed");
                all.AddRange(selector(r));
            }
            // 渲染让路：WASM 单线程上 SignalR 响应处理会饿死渲染帧，批间让出让 loading 指示/表格先渲染
            await Task.Delay(20, ct);
        }
        sw.Stop();
        Console.WriteLine($"[HistoryFetch] {type} 全量拉取完成 {all.Count} 条，{totalPages} 页，{concurrency} 路并发，耗时 {sw.ElapsedMilliseconds}ms");
        return (all, truncated);
    }

    /// <summary>LatestFirst 语义：取指定时刻之前最近一条（GetLatestStatusBefore 等价，WPF Remote 同实现）。</summary>
    public static async Task<List<T>> FetchLatestBeforeAsync<T>(
        DashboardState dashboard,
        HistoryQueryType type,
        DateTime before,
        string? deviceId,
        string? shiftName,
        Func<HistoryQueryResponse, IReadOnlyList<T>> selector,
        CancellationToken ct = default)
    {
        var resp = await dashboard.QueryHistoryAsync(new HistoryQueryRequest
        {
            QueryType = type,
            To = before,
            DeviceId = deviceId,
            ShiftName = shiftName,
            LatestFirst = true,
            Page = 1,
            PageSize = 1,
        }, ct);
        if (resp.ErrorCode != HistoryErrorCode.None)
            throw new InvalidOperationException(resp.Error ?? "history query failed");
        return selector(resp).ToList();
    }

    /// <summary>
    /// 产量窗口分析：优先走 Collector 聚合；旧服务无 Hub 方法时回退分页全量并在本地抽样。
    /// </summary>
    public static async Task<ProductionWindowAnalysisDto> AnalyzeProductionWindowAsync(
        DashboardState dashboard,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        CancellationToken ct = default)
    {
        var request = new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.ProductionLog,
            From = from,
            To = to,
            DeviceId = deviceId,
            ShiftName = shiftName,
            Page = 1,
            PageSize = HistoryQueryLimits.MaxPageSize,
        };
        try
        {
            var dto = await dashboard.QueryProductionWindowAnalysisAsync(request, ct);
            if (dto.ErrorCode != HistoryErrorCode.None)
                throw new InvalidOperationException(dto.Error ?? "production window analysis failed");
            return dto;
        }
        catch (Exception ex) when (IsMissingHubMethod(ex) || ex is NotSupportedException)
        {
            Console.WriteLine("[HistoryFetch] 产量窗口分析接口不可用，回退分页全量");
            return await AnalyzeProductionWindowLocalAsync(dashboard, from, to, deviceId, shiftName, ct);
        }
    }

    /// <summary>
    /// 报警窗口统计：优先走 Collector 聚合；旧服务无 Hub 方法时回退分页全量并在本地汇总。
    /// </summary>
    public static async Task<AlarmWindowStatsDto> AnalyzeAlarmWindowAsync(
        DashboardState dashboard,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        CancellationToken ct = default,
        string? alarmName = null)
    {
        var request = Request(HistoryQueryType.AlarmEvent, from, to, deviceId, shiftName, alarmName);
        try
        {
            var dto = await dashboard.QueryAlarmWindowStatsAsync(request, ct);
            if (dto.ErrorCode != HistoryErrorCode.None)
                throw new InvalidOperationException(dto.Error ?? "alarm window stats failed");
            return dto;
        }
        catch (Exception ex) when (IsMissingHubMethod(ex) || ex is NotSupportedException)
        {
            Console.WriteLine("[HistoryFetch] 报警窗口统计接口不可用，回退分页全量");
            var yesterdayStart = to.Date.AddDays(-1);
            var spanFrom = from < yesterdayStart ? from : yesterdayStart;
            var (events, truncated) = await FetchAllAsync<AlarmEventRecordDto>(
                dashboard, HistoryQueryType.AlarmEvent, spanFrom, to, deviceId, shiftName,
                r => r.AlarmEvents, ct);
            if (!string.IsNullOrEmpty(alarmName))
                events = events.Where(e => e.AlarmName == alarmName).ToList();
            return AlarmWindowMetrics.Build(events, from, to, truncated);
        }
    }

    /// <summary>
    /// 状态窗口分析：优先走 Collector 聚合；旧服务无 Hub 方法时回退分页全量。
    /// </summary>
    public static async Task<StatusWindowAnalysisDto> AnalyzeStatusWindowAsync(
        DashboardState dashboard,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        CancellationToken ct = default)
    {
        var request = Request(HistoryQueryType.StatusTransition, from, to, deviceId, shiftName);
        try
        {
            var dto = await dashboard.QueryStatusWindowAnalysisAsync(request, ct);
            if (dto.ErrorCode != HistoryErrorCode.None)
                throw new InvalidOperationException(dto.Error ?? "status window analysis failed");
            return dto;
        }
        catch (Exception ex) when (IsMissingHubMethod(ex) || ex is NotSupportedException)
        {
            Console.WriteLine("[HistoryFetch] 状态窗口分析接口不可用，回退分页全量");
            return await AnalyzeStatusWindowLocalAsync(dashboard, from, to, deviceId, shiftName, ct);
        }
    }

    /// <summary>
    /// 复盘窗口分析：优先走 Collector 聚合；旧服务无 Hub 方法时回退批量全量。
    /// </summary>
    public static async Task<ReviewWindowAnalysisDto> AnalyzeReviewWindowAsync(
        DashboardState dashboard,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        CancellationToken ct = default)
    {
        var request = Request(HistoryQueryType.ProductionLog, from, to, deviceId, shiftName);
        try
        {
            var dto = await dashboard.QueryReviewAnalysisAsync(request, ct);
            if (dto.ErrorCode != HistoryErrorCode.None)
                throw new InvalidOperationException(dto.Error ?? "review window analysis failed");
            return dto;
        }
        catch (Exception ex) when (IsMissingHubMethod(ex) || ex is NotSupportedException)
        {
            Console.WriteLine("[HistoryFetch] 复盘分析接口不可用，回退批量全量");
            return await AnalyzeReviewWindowLocalAsync(dashboard, from, to, deviceId, shiftName, ct);
        }
    }

    /// <summary>
    /// 批量历史查询：优先一次 Invoke；旧 Collector 无方法时逐条 FetchAll。
    /// 任一子查询 ErrorCode != None 视为整体失败。
    /// </summary>
    public static async Task<BatchHistoryQueryResponse> QueryBatchAsync(
        DashboardState dashboard,
        IReadOnlyList<HistoryQueryRequest> queries,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await dashboard.QueryHistoryBatchAsync(
                new BatchHistoryQueryRequest { Queries = queries }, ct);
            if (resp.Results.Count != queries.Count)
                throw new InvalidOperationException("batch history result count mismatch");
            foreach (var result in resp.Results)
            {
                if (result.ErrorCode != HistoryErrorCode.None)
                    throw new InvalidOperationException(result.Error ?? "batch history query failed");
            }
            return resp;
        }
        catch (Exception ex) when (IsMissingHubMethod(ex) || ex is NotSupportedException)
        {
            Console.WriteLine("[HistoryFetch] 批量查询接口不可用，回退逐条全量");
            var results = new List<HistoryQueryResponse>(queries.Count);
            foreach (var query in queries)
                results.Add(await FetchOneAsResponseAsync(dashboard, query, ct));
            return new BatchHistoryQueryResponse { Results = results };
        }
    }

    public static HistoryQueryRequest Request(
        HistoryQueryType type,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName = null,
        string? alarmName = null)
        => new()
        {
            QueryType = type,
            From = from,
            To = to,
            DeviceId = deviceId,
            ShiftName = shiftName,
            AlarmName = alarmName,
            Page = 1,
            PageSize = HistoryQueryLimits.MaxPageSize,
        };

    public static bool IsMissingHubMethod(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is HubException or NotSupportedException)
            {
                var m = e.Message ?? "";
                if (m.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("not defined", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || e is NotSupportedException)
                    return true;
            }
        }
        return false;
    }

    private static async Task<HistoryQueryResponse> FetchOneAsResponseAsync(
        DashboardState dashboard, HistoryQueryRequest query, CancellationToken ct)
    {
        var from = query.From ?? DateTime.Now.AddHours(-HistoryQueryLimits.DefaultQueryWindowHours);
        var to = query.To ?? DateTime.Now;
        if (from > to) (from, to) = (to, from);

        return query.QueryType switch
        {
            HistoryQueryType.ProductionLog => await PackAsync(
                dashboard, query, HistoryQueryType.ProductionLog, from, to,
                r => r.ProductionLogs,
                (items, truncated) => new HistoryQueryResponse
                {
                    Total = items.Count,
                    IsWindowTruncated = truncated,
                    ProductionLogs = items,
                }, ct),
            HistoryQueryType.AlarmEvent => await PackAsync(
                dashboard, query, HistoryQueryType.AlarmEvent, from, to,
                r => r.AlarmEvents,
                (items, truncated) => new HistoryQueryResponse
                {
                    Total = items.Count,
                    IsWindowTruncated = truncated,
                    AlarmEvents = items,
                }, ct),
            HistoryQueryType.StatusTransition => await PackAsync(
                dashboard, query, HistoryQueryType.StatusTransition, from, to,
                r => r.StatusTransitions,
                (items, truncated) => new HistoryQueryResponse
                {
                    Total = items.Count,
                    IsWindowTruncated = truncated,
                    StatusTransitions = items,
                }, ct),
            HistoryQueryType.DefectSnapshot or HistoryQueryType.DefectSnapshotHourly => await PackAsync(
                dashboard, query, query.QueryType, from, to,
                r => r.DefectSnapshots,
                (items, truncated) => new HistoryQueryResponse
                {
                    Total = items.Count,
                    IsWindowTruncated = truncated,
                    DefectSnapshots = items,
                }, ct),
            _ => new HistoryQueryResponse(),
        };
    }

    private static async Task<HistoryQueryResponse> PackAsync<T>(
        DashboardState dashboard,
        HistoryQueryRequest query,
        HistoryQueryType type,
        DateTime from,
        DateTime to,
        Func<HistoryQueryResponse, IReadOnlyList<T>> selector,
        Func<List<T>, bool, HistoryQueryResponse> pack,
        CancellationToken ct)
    {
        var (items, truncated) = await FetchAllAsync(
            dashboard, type, from, to, query.DeviceId, query.ShiftName, selector, ct);
        return pack(items, truncated);
    }

    private static async Task<ProductionWindowAnalysisDto> AnalyzeProductionWindowLocalAsync(
        DashboardState dashboard,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        CancellationToken ct)
    {
        var (window, windowTruncated) = await FetchAllAsync<ProductionLogDto>(
            dashboard, HistoryQueryType.ProductionLog, from, to, deviceId, shiftName,
            r => r.ProductionLogs, ct);
        List<ProductionLogDto> baseline = [];
        var baselineTruncated = false;
        if (window.Count > 0)
        {
            (baseline, baselineTruncated) = await FetchAllAsync<ProductionLogDto>(
                dashboard, HistoryQueryType.ProductionLog, from.AddDays(-1), from, deviceId, null,
                r => r.ProductionLogs, ct);
        }
        var (ok, ng) = ProductionWindowMetrics.SumWindowProduction(window, baseline, from);
        var compact = ProductionWindowMetrics.Sample15Min(window);
        var chart = ProductionWindowMetrics.BuildChartData(window);
        return new ProductionWindowAnalysisDto
        {
            Truncated = windowTruncated || baselineTruncated,
            Ok = ok,
            Ng = ng,
            QualityRate = OeeCalculator.CalculateQualityRate(ok, ng),
            ChartPoints = chart
                .Select(p => new ProductionChartPointDto { Time = p.Time, Ok = p.Ok, Ng = p.Ng })
                .ToList(),
            CompactLogs = compact,
        };
    }

    private static async Task<StatusWindowAnalysisDto> AnalyzeStatusWindowLocalAsync(
        DashboardState dashboard,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        CancellationToken ct)
    {
        var (trans, truncated) = await FetchAllAsync<StatusTransitionRecordDto>(
            dashboard, HistoryQueryType.StatusTransition, from, to, deviceId, shiftName,
            r => r.StatusTransitions, ct);
        var lastBefore = await FetchLatestBeforeAsync<StatusTransitionRecordDto>(
            dashboard, HistoryQueryType.StatusTransition, from, deviceId, shiftName,
            r => r.StatusTransitions, ct);
        var initialState = lastBefore.Count > 0 ? (int)lastBefore[0].CurrentState : 1;
        var now = dashboard.ServerNow;
        var durations = StatusWindowMetrics.CalculateStateDurations(trans, from, to, initialState, now);
        return new StatusWindowAnalysisDto
        {
            Truncated = truncated,
            InitialState = initialState,
            TotalCount = trans.Count,
            RunSeconds = durations.RunTime,
            AlarmSeconds = durations.AlarmTime,
            PausedSeconds = durations.PausedTime,
            OfflineSeconds = durations.OfflineTime,
            Daily = StatusWindowMetrics.BuildDailyDurations(trans, from, to, initialState, now),
            Segments = StatusWindowMetrics.BuildSegments(trans, from, to, initialState, now),
            ShiftNames = trans.Select(t => t.ShiftName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList(),
        };
    }

    private static async Task<ReviewWindowAnalysisDto> AnalyzeReviewWindowLocalAsync(
        DashboardState dashboard,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        CancellationToken ct)
    {
        var comparisonFrom = from - (to - from);
        var queries = new HistoryQueryRequest[]
        {
            Request(HistoryQueryType.StatusTransition, from, to, deviceId, shiftName),
            Request(HistoryQueryType.AlarmEvent, from, to, deviceId, shiftName),
            Request(HistoryQueryType.DefectSnapshotHourly, from, to, deviceId, shiftName),
            Request(HistoryQueryType.StatusTransition, comparisonFrom, from, deviceId, shiftName),
            Request(HistoryQueryType.AlarmEvent, comparisonFrom, from, deviceId, shiftName),
            Request(HistoryQueryType.DefectSnapshotHourly, comparisonFrom, from, deviceId, shiftName),
        };
        var windowAnalysisTask = AnalyzeProductionWindowAsync(dashboard, from, to, deviceId, shiftName, ct);
        var compareAnalysisTask = AnalyzeProductionWindowAsync(dashboard, comparisonFrom, from, deviceId, shiftName, ct);
        var batchTask = QueryBatchAsync(dashboard, queries, ct);
        var lastBeforeTask = FetchLatestBeforeAsync<StatusTransitionRecordDto>(
            dashboard, HistoryQueryType.StatusTransition, from, deviceId, shiftName, r => r.StatusTransitions, ct);
        var compLastBeforeTask = FetchLatestBeforeAsync<StatusTransitionRecordDto>(
            dashboard, HistoryQueryType.StatusTransition, comparisonFrom, deviceId, shiftName, r => r.StatusTransitions, ct);
        await Task.WhenAll(windowAnalysisTask, compareAnalysisTask, batchTask, lastBeforeTask, compLastBeforeTask);

        var windowAnalysis = await windowAnalysisTask;
        var compareAnalysis = await compareAnalysisTask;
        var batch = await batchTask;
        var lastBefore = await lastBeforeTask;
        var compLastBefore = await compLastBeforeTask;
        var prod = windowAnalysis.CompactLogs.ToList();
        var trans = batch.Results[0].StatusTransitions.ToList();
        var alarms = batch.Results[1].AlarmEvents.ToList();
        var defects = batch.Results[2].DefectSnapshots.ToList();
        var compAlarms = batch.Results[4].AlarmEvents.ToList();
        var compDefects = batch.Results[5].DefectSnapshots.ToList();
        var initialState = lastBefore.Count > 0 ? (int)lastBefore[0].CurrentState : 1;
        var compInitial = compLastBefore.Count > 0 ? (int)compLastBefore[0].CurrentState : 1;
        var now = dashboard.ServerNow;
        var effectiveTo = to > now ? now : to;
        var durations = StatusWindowMetrics.CalculateStateDurations(trans, from, effectiveTo, initialState, now);
        var compDurations = StatusWindowMetrics.CalculateStateDurations(
            batch.Results[3].StatusTransitions.ToList(), comparisonFrom, from, compInitial, now);
        var defectRows = ReviewWindowMetrics.BuildDefectConcentrations(defects, from, to);
        var (longestName, longestHours) = ReviewWindowMetrics.FindLongestAlarm(alarms, effectiveTo, now);
        var (peakHour, peakOk, valleyHour, valleyOk) = ReviewWindowMetrics.FindPeakValley(prod, from, to);
        return new ReviewWindowAnalysisDto
        {
            Truncated = windowAnalysis.Truncated || compareAnalysis.Truncated
                || batch.Results.Any(item => item.IsWindowTruncated),
            Ok = windowAnalysis.Ok,
            Ng = windowAnalysis.Ng,
            QualityRate = windowAnalysis.QualityRate,
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
            BaselineOutput = compareAnalysis.Ok + compareAnalysis.Ng,
            BaselineQuality = compareAnalysis.QualityRate,
            BaselineRunSeconds = compDurations.RunTime,
            BaselineAlarmSeconds = compDurations.AlarmTime,
            PreviousAlarmTriggered = compAlarms.Count(e => e.EventType == AlarmEventType.Triggered),
            PreviousDefectCount = ReviewWindowMetrics.BuildDefectConcentrations(compDefects, comparisonFrom, from).Sum(d => d.Count),
            ChartPoints = windowAnalysis.ChartPoints,
            Alarms = ReviewWindowMetrics.AnalyzeAlarms(alarms, prod, effectiveTo, now),
            Timeline = ReviewWindowMetrics.BuildTimeline(trans, alarms, prod, initialState, from, effectiveTo),
            Defects = defectRows,
            Shifts = ReviewWindowMetrics.BuildShiftSummaries(prod, alarms),
            CompactLogs = prod,
        };
    }
}
