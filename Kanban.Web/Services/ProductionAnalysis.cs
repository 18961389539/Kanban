using Kanban.Analysis;
using Kanban.Contracts;
using Kanban.Contracts.Dtos;

namespace Kanban.Web.Services;

/// <summary>
/// 产量历史分析（Web 端）：从 WPF HistoryQueryHelper / ProductionQueryViewModel 移植，
/// 操作对象改为 <see cref="ProductionLogDto"/>（跨进程契约），逻辑口径与 WPF 逐条对齐：
/// 班次实例切分、窗口差分（基准 = 窗口前同班次实例累计值）、15 分钟分桶图表数据、产量洞察。
/// 注：WPF 侧仍以 Core 实体实现，后续可收敛为 Contracts 单源（见 ADR 分层约定）。
/// </summary>
public static class ProductionAnalysis
{
    /// <summary>全量拉取每页大小（收敛到跨进程契约 HistoryQueryLimits.MaxPageSize，防止 clamp 截断）。</summary>
    public const int FetchPageSize = HistoryQueryLimits.MaxPageSize;

    /// <summary>全量拉取最大页数（10 万条上限）：防服务端 Total 语义异常时无限翻页；与 WPF Remote 模式一致。</summary>
    public const int MaxFetchPages = HistoryQueryLimits.MaxFetchAllPages;

    public const int TablePageSize = 50;

    /// <summary>
    /// 按「班次实例」切分：OK/NG 累计值递增且班次名不变视为同一实例；
    /// 累计值回落或班次切换 → 新实例。与 WPF HistoryQueryHelper.SplitShiftInstances 一致。
    /// </summary>
    public static List<List<ProductionLogDto>> SplitShiftInstances(List<ProductionLogDto> sortedLogs)
        => ProductionWindowMetrics.SplitShiftInstances(sortedLogs);

    /// <summary>
    /// 窗口内产量（窗口差分）：Ok/NG 存的是「班次内累计值」，窗口内产量 = 窗口末条累计 − 窗口起点同班次实例的累计。
    /// 修复 2026-08-26（与 WPF HistoryQueryHelper.SumWindowProduction 同步，P1）：统一为「实例基线差分」——
    /// 合并窗口内 + 窗口前日志按班次实例分组（累计回落/班次切换即切组，组内窗口前日志天然属于同一实例），
    /// 每实例基线 = 窗口起点快照 或 窗口前同实例末条 或 实例首条，产量 = 实例窗口内末条 − 基线。
    /// 旧实现取窗口前同班次最近快照作基线（<see cref="FindBaselineBeforeWindow"/>），基线池仅覆盖
    /// [from-1天, from)，看不到窗口内后续班次切换——长窗口/数据缺口下会把更早班次实例的累计值误作基线，
    /// 产量被砍 0 或虚高。
    /// </summary>
    public static (int Ok, int Ng) SumWindowProduction(
        List<ProductionLogDto> logsInWindow,
        List<ProductionLogDto> baselineCandidates,
        DateTime windowFrom)
    {
        return ProductionWindowMetrics.SumWindowProduction(logsInWindow, baselineCandidates, windowFrom);
    }

    /// <summary>
    /// 在窗口前基准候选中找「与目标班次同一实例且最接近窗口起点」的快照。
    /// 入参须为按时间**倒序**排列的列表（调用方预排序一次）：命中目标班次名且此前未出现其它班次名 → 同一实例基准；
    /// 否则返回 null（窗口前无同实例数据）。O(1) 判定，无内部排序。
    /// </summary>
    public static ProductionLogDto? FindBaselineBeforeWindow(List<ProductionLogDto> baselineDescending, string shiftName)
    {
        foreach (var c in baselineDescending)
        {
            if (c.ShiftName == shiftName) return c;
            return null; // 最新一条已是其它班次 → 目标班次属更早实例，无同实例基准
        }
        return null;
    }

    /// <summary>
    /// 图表数据：按班次实例分组 → 15 分钟桶取桶末条（累计值末态）→ 时间升序。
    /// 与 WPF ProductionQueryViewModel.Query 中 chartData 构建逐条一致。
    /// </summary>
    public static List<(DateTime Time, int Ok, int Ng)> BuildChartData(List<ProductionLogDto> logs)
    {
        return ProductionWindowMetrics.BuildChartData(logs);
    }

    /// <summary>
    /// 产量洞察文本（按需本地化，返回 L.T key + args 的格式化委托）。
    /// 逻辑与 WPF BuildProductionInsight 一致：均值/峰值偏差 + 突降检测（< 前桶 80%）。
    /// </summary>
    public static string? BuildInsight(
        List<(DateTime Time, int Ok, int Ng)> chartData,
        Func<string, object[], string> localize)
    {
        if (chartData.Count < 2) return null;

        var okValues = chartData.Select(d => (double)d.Ok).ToList();
        var avgOk = okValues.Average();
        var peakIdx = okValues.IndexOf(okValues.Max());
        var peak = chartData[peakIdx];

        string main;
        if (avgOk <= 0) return null; // 全 0 数据无均值口径，与 WPF BuildProductionInsight 一致

        var deviation = (peak.Ok - avgOk) / avgOk;
        main = deviation switch
        {
            > 0.2 => localize("Hq_InsightPeakHigh", [peak.Ok, peak.Time.ToString("MM-dd HH:mm"), deviation]),
            < -0.2 => localize("Hq_InsightPeakLow", [peak.Ok, peak.Time.ToString("MM-dd HH:mm"), -deviation]),
            _ => localize("Hq_InsightSteady", [avgOk, peak.Ok, peak.Time.ToString("MM-dd HH:mm")]),
        };

        // 突降检测（与图表标注同判断）：curr.Ok < prev.Ok * 0.8
        List<(DateTime Time, int PrevOk, int CurrOk)> drops = [];
        for (int i = 1; i < chartData.Count; i++)
        {
            var prev = chartData[i - 1];
            var curr = chartData[i];
            if (prev.Ok > 0 && curr.Ok < prev.Ok * 0.8)
                drops.Add((curr.Time, prev.Ok, curr.Ok));
        }
        if (drops.Count > 0)
        {
            var worst = drops.MaxBy(d => (d.PrevOk - d.CurrOk) / (double)d.PrevOk);
            var dropPct = 1.0 - worst.CurrOk / (double)worst.PrevOk;
            var suffix = drops.Count == 1
                ? localize("Hq_InsightDrop", [drops.Count, worst.Time.ToString("MM-dd HH:mm"), dropPct, worst.PrevOk, worst.CurrOk])
                : localize("Hq_InsightDrop", [drops.Count, worst.Time.ToString("MM-dd HH:mm"), dropPct, worst.PrevOk, worst.CurrOk]);
            return $"{main}\n{suffix}";
        }

        return main;
    }

    /// <summary>RFC 4180 转义——委托 Kanban.Analysis.CsvUtil（ADR-4 单源，2026-08-13 收敛；
    /// 此前 CsvUtil 在 MainAPP 不可被 Web 引用而本地复制，Core 去 WPF 化后共享分析层已可被 WASM 引用）。</summary>
    public static string CsvEscape(string? value) => CsvUtil.Escape(value);

    public static int CalcTotalPages(int totalCount, int pageSize)
        => totalCount <= 0 ? 0 : (totalCount + pageSize - 1) / pageSize;
}
