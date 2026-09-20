using Kanban.Contracts.Dtos;
using Kanban.Web.Services;
using Microsoft.JSInterop;

namespace Kanban.Web.Pages;

/// <summary>历史查询页 Tab 0：产量（与 WPF ProductionQueryViewModel 对等）。</summary>
public partial class HistoryQuery
{
    private const int ProductionPageSize = 50;

    // ──────────── 产量 Tab 状态 ────────────
    private bool ProdHasQueried { get; set; }
    private bool ProdIsLoading { get; set; }
    /// <summary>窗口全量分析进行中（表格已渲染，仅图表/KPI 区显示占位）。</summary>
    private bool ProdAnalyzing { get; set; }
    private bool ProdAnalysisDone { get; set; }
    private bool ProdTruncated { get; set; }
    private int ProdPage { get; set; } = 1;
    private int ProdTotalCount { get; set; }
    private int ProdTotalPages { get; set; }
    private List<ProductionLogDto> ProdRows { get; set; } = [];
    private int ProdTotalOk { get; set; }
    private int ProdTotalNg { get; set; }
    private double ProdQualityRate { get; set; }
    private List<(DateTime Time, int Ok, int Ng)> ProdChartData { get; set; } = [];
    private string? ProdInsight { get; set; }
    private object? ProdChartOption { get; set; }
    private string? ProdValidationMessage { get; set; }
    private string? ProdQueryError { get; set; }

    // 窗口数据缓存：KPI/图表只查一次，翻页只重查表格页
    private List<ProductionLogDto> _prodWindow = [];
    private List<ProductionLogDto> _prodBaseline = [];
    private DateTime _prodFrom;
    private DateTime _prodTo;
    private string? _prodDeviceFilter;
    private string? _prodShiftFilter;

    private string ProdTotalOkText => ProdTotalOk.ToString("N0");
    private string ProdTotalNgText => ProdTotalNg.ToString("N0");
    private string ProdOutputText => (ProdTotalOk + ProdTotalNg).ToString("N0");
    private string ProdQualityRateText => $"{ProdQualityRate:P2}";
    private double ProdNgRate => ProdTotalOk + ProdTotalNg > 0 ? (double)ProdTotalNg / (ProdTotalOk + ProdTotalNg) : 0;
    private string ProdNgRateText => $"{ProdNgRate:P2}";
    private string ProdQualityTooltip => L.Tip(
        "Hq_Tip_WindowQuality",
        ProdTotalOk,
        ProdTotalOk + ProdTotalNg,
        ProdQualityRateText);
    private string ProdOutputTooltip => L.Tip("Hq_Tip_WindowOutput", ProdTotalOk, ProdTotalNg, ProdTotalOk + ProdTotalNg);
    private string ProdNgRateTooltip => L.Tip("Hq_Tip_WindowNgRate", ProdTotalNg, ProdTotalOk + ProdTotalNg, ProdNgRateText);
    private string ProdPageSummaryText => PageSummary(ProdPage, ProdTotalPages, ProdTotalCount);

    private async Task ProdSearchAsync(DateTime from, DateTime to)
    {
        ProdValidationMessage = null;
        ProdQueryError = null;
        _prodFrom = from;
        _prodTo = to;
        _prodDeviceFilter = string.IsNullOrEmpty(DeviceId) ? null : DeviceId;
        _prodShiftFilter = string.IsNullOrEmpty(ShiftName) ? null : ShiftName;
        ProdPage = 1;
        await ProdRunQueryAsync();
    }

    /// <summary>
    /// 产量查询主流程（表格先行）：表格页单次往返秒出（IsLoading 只覆盖表格阶段），
    /// 窗口全量并发拉取（KPI/图表/洞察）后台填充（ProdAnalyzing 标志，仅图表区显示"分析中"）。
    /// 窗口全量上限 10 万条（与 WPF Remote 模式一致）；超出时截断并提示。
    /// </summary>
    private async Task ProdRunQueryAsync()
    {
        ProdIsLoading = true;
        ProdQueryError = null;
        ProdAnalysisDone = false;
        ProdAnalyzing = false;
        ProdTruncated = false;
        ProdChartOption = null;
        try
        {
            // 1) 表格当前页（服务端分页，单次往返）
            await ProdLoadTablePageAsync();
            ProdHasQueried = true;
            ProdIsLoading = false; // 表格就绪：立即渲染结果区（KPI/图表显示占位/分析中）
            StateHasChanged();

            // 2) 窗口分析（KPI/图表/洞察）——服务端聚合，旧 Collector 自动回退分页全量
            ProdAnalyzing = true;
            var analysis = await HistoryFetch.AnalyzeProductionWindowAsync(
                Dashboard, _prodFrom, _prodTo, _prodDeviceFilter, _prodShiftFilter);
            _prodWindow = analysis.CompactLogs.ToList();
            _prodBaseline = [];
            ProdTruncated = analysis.Truncated;
            ProdApplyAnalysis(analysis);
        }
        catch (Exception)
        {
            ProdQueryError = L.T("Hq_QueryFailed");
            ProdHasQueried = true;
            ProdTotalCount = 0;
            ProdTotalPages = 0;
            ProdRows = [];
            ProdAnalysisDone = true;
        }
        finally
        {
            ProdIsLoading = false;
            ProdAnalyzing = false;
        }
    }

    /// <summary>KPI/图表/洞察/班次下拉（口径与 WPF ProductionQueryViewModel.Query 对齐）。</summary>
    private void ProdApplyAnalysis(ProductionWindowAnalysisDto analysis)
    {
        ProdTotalOk = analysis.Ok;
        ProdTotalNg = analysis.Ng;
        ProdQualityRate = analysis.QualityRate;
        ProdChartData = analysis.ChartPoints.Select(p => (p.Time, p.Ok, p.Ng)).ToList();
        ProdInsight = null;
        if (_prodWindow.Count == 0 && ProdChartData.Count == 0)
        {
            ProdAnalysisDone = true;
            return;
        }

        ActiveShiftOptions = _prodWindow.Select(p => p.ShiftName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (ProdChartData.Count >= 2)
            ProdInsight = ProductionAnalysis.BuildInsight(ProdChartData, L.T);
        if (ProdChartData.Count > 0)
            ProdBuildChartOption();
        ProdAnalysisDone = true;
    }

    /// <summary>ECharts 配置：OK/NG 堆叠柱（与 WPF 堆叠面积图同语义），深色主题对齐看板调色板。</summary>
    private void ProdBuildChartOption()
    {
        ProdChartOption = new Dictionary<string, object?>
        {
            ["backgroundColor"] = "transparent",
            ["tooltip"] = Tooltip(),
            ["legend"] = new Dictionary<string, object?>
            {
                ["data"] = new[] { L.T("Hq_ChartOk"), L.T("Hq_ChartNg") },
                ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#9CA3AF" },
            },
            ["grid"] = new Dictionary<string, object?> { ["left"] = 52, ["right"] = 16, ["top"] = 36, ["bottom"] = 44 },
            ["xAxis"] = new Dictionary<string, object?>
            {
                ["type"] = "category",
                ["data"] = ProdChartData.Select(d => d.Time.ToString("MM-dd HH:mm")).ToList(),
                ["axisLabel"] = AxisLabel(),
                ["axisLine"] = AxisLine(),
            },
            ["yAxis"] = ValueAxis(),
            ["dataZoom"] = DataZoom(),
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = L.T("Hq_ChartOk"),
                    ["type"] = "bar",
                    ["stack"] = "total",
                    ["data"] = ProdChartData.Select(d => d.Ok).ToList(),
                    ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#34D399" },
                },
                new Dictionary<string, object?>
                {
                    ["name"] = L.T("Hq_ChartNg"),
                    ["type"] = "bar",
                    ["stack"] = "total",
                    ["data"] = ProdChartData.Select(d => d.Ng).ToList(),
                    ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#F87171" },
                },
            },
        };
    }

    private async Task ProdLoadTablePageAsync()
    {
        var resp = await Dashboard.QueryHistoryAsync(new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.ProductionLog,
            From = _prodFrom,
            To = _prodTo,
            DeviceId = _prodDeviceFilter,
            ShiftName = _prodShiftFilter,
            Page = ProdPage,
            PageSize = ProductionPageSize,
        });
        if (resp.ErrorCode != HistoryErrorCode.None)
            throw new InvalidOperationException(resp.Error ?? "history query failed");
        ProdTotalCount = resp.Total;
        ProdTotalPages = ProductionAnalysis.CalcTotalPages(resp.Total, ProductionPageSize);
        // 服务端按时间升序返回；表格按 WPF 口径"最新在前"展示
        ProdRows = resp.ProductionLogs.Reverse().ToList();
    }

    private async Task ProdPrevPageAsync()
    {
        if (ProdPage <= 1) return;
        ProdPage--;
        await ProdLoadTablePageAsync();
    }

    private async Task ProdNextPageAsync()
    {
        if (ProdPage >= ProdTotalPages) return;
        ProdPage++;
        await ProdLoadTablePageAsync();
    }

    /// <summary>
    /// 导出窗口全量（点击时再分页拉取），与 CSV 头部汇总行（全窗口口径）一致——
    /// 查询阶段只保留 15 分钟抽样，不能直接当明细导出。
    /// </summary>
    private async Task ProdExportCsvAsync()
    {
        if (ProdTotalCount == 0 && ProdTotalOk + ProdTotalNg == 0)
        {
            ValidationMessage = L.T("Hq_ExportEmpty");
            return;
        }

        var (all, _) = await FetchAllAsync<ProductionLogDto>(
            HistoryQueryType.ProductionLog, _prodFrom, _prodTo, _prodDeviceFilter, _prodShiftFilter, r => r.ProductionLogs);
        if (all.Count == 0)
        {
            ValidationMessage = L.T("Hq_ExportEmpty");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(L.T("Csv_SumRange", _prodFrom, _prodTo));
        sb.AppendLine(L.T("Csv_SumProd", ProdTotalOk, ProdTotalNg, ProdQualityRate.ToString("P2", System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine($"# {ProdInsight ?? "—"}");
        sb.AppendLine(string.Join(',',
            C(L.T("Csv_Time")), C(L.T("Csv_DeviceId")), C(L.T("Csv_DeviceName")), C(L.T("Csv_Shift")), C(L.T("Csv_OkCount")), C(L.T("Csv_NgCount")), C(L.T("Csv_StatusWord"))));
        foreach (var r in all.AsEnumerable().Reverse())
        {
            sb.AppendLine(string.Join(',',
                C(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")), C(r.DeviceId), C(r.DeviceName), C(r.ShiftName),
                r.OkProduction.ToString(), r.NgProduction.ToString(), r.StatusWord.ToString()));
        }

        var fileName = L.T("Csv_FileProd", _prodFrom, _prodTo);
        await JS.InvokeVoidAsync("KanbanECharts.download", fileName, "\uFEFF" + sb, "text/csv;charset=utf-8");
    }

    private void ResetProduction()
    {
        ProdHasQueried = false;
        ProdIsLoading = false;
        ProdAnalyzing = false;
        ProdAnalysisDone = false;
        ProdTruncated = false;
        ProdPage = 1;
        ProdTotalCount = 0;
        ProdTotalPages = 0;
        ProdRows = [];
        ProdTotalOk = 0;
        ProdTotalNg = 0;
        ProdQualityRate = 0;
        ProdChartData = [];
        ProdInsight = null;
        ProdChartOption = null;
        ProdValidationMessage = null;
        ProdQueryError = null;
        _prodWindow = [];
        _prodBaseline = [];
    }

    private static string C(string? value) => ProductionAnalysis.CsvEscape(value);
}
