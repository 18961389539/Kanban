using Kanban.Analysis;
using Kanban.Contracts.Dtos;
using Kanban.Web.Services;
using Microsoft.JSInterop;

namespace Kanban.Web.Pages;

/// <summary>历史查询页 Tab 3：OEE（与 WPF OeeQueryViewModel 对等；无班次配置 API，走无配置回退口径）。</summary>
public partial class HistoryQuery
{
    // ──────────── OEE Tab 状态 ────────────
    private bool OeHasQueried { get; set; }
    private bool OeIsLoading { get; set; }
    private bool OeAnalysisDone { get; set; }
    private double OeQ { get; set; }
    private double OeP { get; set; }
    private double OeA { get; set; }
    private double OeValue { get; set; }
    private int OeOk { get; set; }
    private int OeNg { get; set; }
    private int OeTargetCycle { get; set; }
    private double OeRunSeconds { get; set; }
    private double OeAlarmSeconds { get; set; }
    private string? OeDeviceName { get; set; }
    private List<OeeAnalysis.ShiftOee> OeShifts { get; set; } = [];
    private object? OeTrendOption { get; set; }
    private string? OeInsight { get; set; }
    private string? OeError { get; set; }

    private string OeOkText => OeOk.ToString("N0");
    private string OeNgText => OeNg.ToString("N0");
    private string OeRunText => Kanban.Contracts.Formatting.DurationFormatter.FormatCompact(OeRunSeconds);
    private string OeAlarmText => Kanban.Contracts.Formatting.DurationFormatter.FormatCompact(OeAlarmSeconds);
    private string OeTargetText => OeTargetCycle > 0 ? $"{OeTargetCycle} {L.T("Unit_PerHour")}" : "—";
    private string OeDeviceNameText => OeDeviceName ?? "—";

    private async Task OeSearchAsync(DateTime from, DateTime to)
    {
        OeError = null;
        if (string.IsNullOrEmpty(DeviceId))
        {
            ValidationMessage = L.T("Hq_NeedDevice");
            return;
        }
        await OeRunQueryAsync(from, to);
    }

    private async Task OeRunQueryAsync(DateTime from, DateTime to)
    {
        OeIsLoading = true;
        OeError = null;
        OeAnalysisDone = false;
        OeTrendOption = null;
        OeInsight = null;
        try
        {
            var device = _deviceConfigs.FirstOrDefault(d => d.Id == DeviceId)
                ?? (await Dashboard.QueryDevicesAsync()).FirstOrDefault(d => d.Id == DeviceId);
            if (device == null)
            {
                OeError = L.T("Hq_QueryFailed");
                OeHasQueried = true;
                return;
            }
            OeDeviceName = device.Name;
            OeTargetCycle = device.TargetCycle;

            var shiftFilter = string.IsNullOrEmpty(ShiftName) ? null : ShiftName;

            // 1) 窗口产量 + 基准（窗口差分，口径与 WPF SumWindowProduction 一致）
            var (window, _) = await FetchAllAsync<ProductionLogDto>(
                HistoryQueryType.ProductionLog, from, to, DeviceId, shiftFilter, r => r.ProductionLogs);
            List<ProductionLogDto> baseline = [];
            if (window.Count > 0)
            {
                var (b, _) = await FetchAllAsync<ProductionLogDto>(
                    HistoryQueryType.ProductionLog, from.AddDays(-1), from, DeviceId, null, r => r.ProductionLogs);
                baseline = b;
            }
            var (ok, ng) = ProductionAnalysis.SumWindowProduction(window, baseline, from);
            OeOk = ok;
            OeNg = ng;

            // 2) 状态转换 + 初始状态 → 时长
            var (trans, _) = await FetchAllAsync<StatusTransitionRecordDto>(
                HistoryQueryType.StatusTransition, from, to, DeviceId, shiftFilter, r => r.StatusTransitions);
            var initialState = 1;
            var lastBefore = await FetchLatestBeforeAsync<StatusTransitionRecordDto>(
                HistoryQueryType.StatusTransition, from, DeviceId, shiftFilter, r => r.StatusTransitions);
            if (lastBefore.Count > 0)
                initialState = (int)lastBefore[0].CurrentState;

            var effectiveTo = to > DateTime.Now ? DateTime.Now : to;
            var durations = StatusAnalysis.CalculateStateDurations(trans, from, effectiveTo, initialState);
            OeRunSeconds = durations.RunTime;
            OeAlarmSeconds = durations.AlarmTime;

            // 3) 四率
            OeQ = OeeCalculator.CalculateQualityRate(ok, ng);
            OeP = OeeCalculator.CalculatePerformanceRate(ok, ng, OeTargetCycle, durations.RunTime);
            OeA = OeeCalculator.CalculateAvailabilityRate(durations.RunTime, durations.AlarmTime);
            OeValue = OeeCalculator.CalculateOee(OeQ, OeP, OeA);

            // 4) 分班次 OEE + 趋势图 + 洞察
            OeShifts = OeeAnalysis.ComputePerShiftOee(window, OeTargetCycle, trans, initialState, from, to);
            OeBuildTrendOption();
            OeInsight = OeeAnalysis.BuildInsight(OeQ, OeP, OeA, OeShifts, L.T);

            OeAnalysisDone = true;
            OeHasQueried = true;
        }
        catch (Exception)
        {
            OeError = L.T("Hq_QueryFailed");
            OeHasQueried = true;
            OeAnalysisDone = true;
        }
        finally
        {
            OeIsLoading = false;
        }
    }

    /// <summary>班次 OEE 趋势柱状图（对齐 WPF BuildOeeShiftBarChart：班次 → OEE%）。</summary>
    private void OeBuildTrendOption()
    {
        if (OeShifts.Count == 0)
        {
            OeTrendOption = null;
            return;
        }
        OeTrendOption = new Dictionary<string, object?>
        {
            ["backgroundColor"] = "transparent",
            ["tooltip"] = Tooltip(),
            ["grid"] = new Dictionary<string, object?> { ["left"] = 48, ["right"] = 16, ["top"] = 24, ["bottom"] = 28 },
            ["xAxis"] = new Dictionary<string, object?>
            {
                ["type"] = "category",
                ["data"] = OeShifts.Select(s => $"{s.ShiftTime:MM-dd HH:mm} {s.ShiftName}").ToList(),
                ["axisLabel"] = AxisLabel(),
                ["axisLine"] = AxisLine(),
            },
            ["yAxis"] = new Dictionary<string, object?>
            {
                ["type"] = "value",
                // 数据为 Oee*100（0~100），量程必须一致，否则柱高溢出约 100 倍、刻度恒为 1%
                // （审查修复 2026-08-15：原 max=1 与 series 数据量纲不匹配）
                ["max"] = 100,
                ["axisLabel"] = new Dictionary<string, object?> { ["color"] = "#8B92A0", ["formatter"] = "{value}%" },
                ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#212834" } },
            },
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "OEE",
                    ["type"] = "bar",
                    ["data"] = OeShifts.Select(s => Math.Round(s.Oee * 100, 1)).ToList(),
                    ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#34D399" },
                    ["label"] = new Dictionary<string, object?> { ["show"] = true, ["position"] = "top", ["color"] = "#9CA3AF", ["formatter"] = "{c}%" },
                },
            },
        };
    }

    private async Task OeExportCsvAsync()
    {
        if (OeValue == 0 && OeOk == 0)
        {
            ValidationMessage = L.T("Hq_ExportEmpty");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(L.T("Csv_SumDevice", OeDeviceName ?? DeviceId));
        sb.AppendLine($"# {OeInsight ?? "—"}");
        sb.AppendLine(string.Join(',', C(L.T("Csv_Metric")), C(L.T("Csv_Value"))));
        sb.AppendLine(string.Join(',', C(L.T("Lbl_Quality")), OeQ.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(',', C(L.T("Lbl_Performance")), OeP.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(',', C(L.T("Lbl_Availability")), OeA.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(',', C("OEE"), OeValue.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(',', C(L.T("Csv_OkCount")), OeOk.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(',', C(L.T("Csv_NgCount")), OeNg.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(',', C(L.T("Csv_RunSeconds")), OeRunSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(',', C(L.T("Csv_AlarmSeconds")), OeAlarmSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(',', C(L.T("Csv_TargetCycle")), OeTargetCycle.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var fileName = $"OEE_{(OeRunSeconds > 0 ? DateTime.Now.ToString("yyyyMMdd") : "query")}.csv";
        await JS.InvokeVoidAsync("KanbanECharts.download", fileName, "\uFEFF" + sb, "text/csv;charset=utf-8");
    }

    private void ResetOee()
    {
        OeHasQueried = false;
        OeIsLoading = false;
        OeAnalysisDone = false;
        OeQ = 0;
        OeP = 0;
        OeA = 0;
        OeValue = 0;
        OeOk = 0;
        OeNg = 0;
        OeTargetCycle = 0;
        OeRunSeconds = 0;
        OeAlarmSeconds = 0;
        OeDeviceName = null;
        OeShifts = [];
        OeTrendOption = null;
        OeInsight = null;
        OeError = null;
    }
}
