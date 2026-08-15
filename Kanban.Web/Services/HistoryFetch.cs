using Kanban.Contracts.Dtos;

namespace Kanban.Web.Services;

/// <summary>
/// 历史数据拉取助手（历史查询页/报警中心共用）：
/// 服务端分页并发拉取全量（8 路并发；单页上限 500；10 万条上限截断）与 LatestFirst 单条语义。
/// </summary>
public static class HistoryFetch
{
    public const int FetchPageSize = 500;
    public const int MaxFetchPages = 200;

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
            PageSize = FetchPageSize,
        }, ct);

        var first = await QueryAsync(1);
        if (first.ErrorCode != HistoryErrorCode.None)
            throw new InvalidOperationException(first.Error ?? "history query failed");

        var all = new List<T>(first.Total);
        all.AddRange(selector(first));

        var totalPages = ProductionAnalysis.CalcTotalPages(first.Total, FetchPageSize);
        if (totalPages > MaxFetchPages)
        {
            truncated = true;
            totalPages = MaxFetchPages;
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
}
