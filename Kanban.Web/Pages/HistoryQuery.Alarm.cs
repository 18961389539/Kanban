using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Web.Services;
using Microsoft.JSInterop;

namespace Kanban.Web.Pages;

/// <summary>历史查询页 Tab 2：报警（与 WPF AlarmQueryViewModel 对等）。</summary>
public partial class HistoryQuery
{
    private const int AlarmTablePageSize = 50;

    // ──────────── 报警 Tab 状态 ────────────
    private bool AlHasQueried { get; set; }
    private bool AlIsLoading { get; set; }
    private bool AlAnalysisDone { get; set; }
    private int AlPage { get; set; } = 1;
    private int AlTotalCount { get; set; }
    private int AlTotalPages { get; set; }
    private List<AlarmEventRecordDto> AlRows { get; set; } = [];
    private int AlTriggered { get; set; }
    private int AlRecovered { get; set; }
    private int AlPending { get; set; }
    private object? AlChartOption { get; set; }
    private string? AlInsight { get; set; }
    private string? AlError { get; set; }

    /// <summary>报警名称筛选（查询后按窗口实际报警名刷新下拉，WPF 同口径）。</summary>
    private string AlarmName { get; set; } = "";
    private List<string> AlarmNameOptions { get; set; } = [];

    // 窗口缓存：全量拉取一次，翻页客户端切（事件型数据量小，与 WPF Remote 一致）
    private List<AlarmEventRecordDto> _alAll = [];
    private DateTime _alFrom;
    private DateTime _alTo;
    private string? _alDevice;
    private string? _alShift;
    private string? _alAlarmName;

    private string AlTriggeredText => AlTriggered.ToString("N0");
    private string AlRecoveredText => AlRecovered.ToString("N0");
    private string AlPendingText => AlPending.ToString("N0");
    private string AlPageSummaryText => PageSummary(AlPage, AlTotalPages, AlTotalCount);

    private async Task AlSearchAsync(DateTime from, DateTime to)
    {
        AlError = null;
        if (string.IsNullOrEmpty(DeviceId))
        {
            ValidationMessage = L.T("Hq_NeedDevice");
            return;
        }
        _alFrom = from;
        _alTo = to;
        _alDevice = DeviceId;
        _alShift = string.IsNullOrEmpty(ShiftName) ? null : ShiftName;
        _alAlarmName = string.IsNullOrEmpty(AlarmName) ? null : AlarmName;
        AlPage = 1;
        await AlRunQueryAsync();
    }

    private async Task AlRunQueryAsync()
    {
        AlIsLoading = true;
        AlError = null;
        AlAnalysisDone = false;
        AlChartOption = null;
        AlInsight = null;
        try
        {
            var (all, _) = await FetchAllAsync<AlarmEventRecordDto>(
                HistoryQueryType.AlarmEvent, _alFrom, _alTo, _alDevice, _alShift, r => r.AlarmEvents);

            // 报警名称过滤（WPF Remote 同语义：服务端不支持按名过滤，拉全量后客户端过滤）
            var filtered = _alAlarmName is null
                ? all
                : all.Where(e => e.AlarmName == _alAlarmName).ToList();
            _alAll = filtered;
            AlTotalCount = filtered.Count;

            AlTriggered = filtered.Count(e => e.EventType == AlarmEventType.Triggered);
            AlRecovered = filtered.Count(e => e.EventType == AlarmEventType.Recovered);
            AlPending = AlarmAnalysis.CountPending(filtered);

            AlarmNameOptions = filtered
                .Where(e => !string.IsNullOrEmpty(e.AlarmName))
                .Select(e => e.AlarmName!)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            ActiveShiftOptions = filtered.Select(e => e.ShiftName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            var stats = AlarmAnalysis.BuildStats(filtered);
            AlBuildChartOption(stats);

            var effectiveTo = _alTo > Dashboard.ServerNow ? Dashboard.ServerNow : _alTo;
            if (stats.Count > 0 && AlTriggered > 0)
                AlInsight = AlarmAnalysis.BuildInsight(stats, AlTriggered, filtered, effectiveTo, L.T);

            (AlRows, AlTotalPages) = PageItems(filtered, AlPage, AlarmTablePageSize);
            AlAnalysisDone = true;
            AlHasQueried = true;
        }
        catch (Exception)
        {
            AlError = L.T("Hq_QueryFailed");
            AlHasQueried = true;
            AlTotalCount = 0;
            AlTotalPages = 0;
            AlRows = [];
            AlAnalysisDone = true;
        }
        finally
        {
            AlIsLoading = false;
        }
    }

    /// <summary>报警频次排行柱状图（对齐 WPF BuildAlarmChart：按报警名触发次数降序）。</summary>
    private void AlBuildChartOption(List<(string AlarmName, int TriggerCount, double AvgDurationMin)> stats)
    {
        var top = stats.Take(20).ToList(); // 只画 Top20，避免超长轴
        AlChartOption = new Dictionary<string, object?>
        {
            ["backgroundColor"] = "transparent",
            ["tooltip"] = Tooltip(),
            ["grid"] = new Dictionary<string, object?> { ["left"] = 48, ["right"] = 16, ["top"] = 24, ["bottom"] = 28 },
            ["xAxis"] = new Dictionary<string, object?>
            {
                ["type"] = "category",
                ["data"] = top.Select(s => s.AlarmName).ToList(),
                ["axisLabel"] = AxisLabel(),
                ["axisLine"] = AxisLine(),
            },
            ["yAxis"] = ValueAxis(),
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = L.T("Hq_AlarmTriggered"),
                    ["type"] = "bar",
                    ["data"] = top.Select(s => s.TriggerCount).ToList(),
                    ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#F87171" },
                },
            },
        };
    }

    private void AlPrevPage()
    {
        if (AlPage <= 1) return;
        AlPage--;
        (AlRows, AlTotalPages) = PageItems(_alAll, AlPage, AlarmTablePageSize);
    }

    private void AlNextPage()
    {
        if (AlPage >= AlTotalPages) return;
        AlPage++;
        (AlRows, AlTotalPages) = PageItems(_alAll, AlPage, AlarmTablePageSize);
    }

    /// <summary>导出窗口全量（_alAll，含报警名过滤），与头部汇总行口径一致（旧实现只导出当前页）。</summary>
    private async Task AlExportCsvAsync()
    {
        if (_alAll.Count == 0)
        {
            ValidationMessage = L.T("Hq_ExportEmpty");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(L.T("Csv_SumAlarm", AlTriggered, AlRecovered, AlPending));
        sb.AppendLine($"# {AlInsight ?? "—"}");
        sb.AppendLine(string.Join(',',
            C(L.T("Csv_EventTime")), C(L.T("Csv_DeviceId")), C(L.T("Csv_DeviceName")), C(L.T("Csv_AlarmId")), C(L.T("Csv_AlarmName")), C(L.T("Csv_PlcAddress")), C(L.T("Csv_EventType")), C(L.T("Csv_EventTypeText")), C(L.T("Csv_Shift"))));
        foreach (var r in _alAll)
        {
            sb.AppendLine(string.Join(',',
                C(r.EventTime.ToString("yyyy-MM-dd HH:mm:ss")), C(r.DeviceId), C(r.DeviceName),
                C(r.AlarmId), C(r.AlarmName), C(r.PlcAddress),
                ((int)r.EventType).ToString(), C(AlarmTypeText(r.EventType)), C(r.ShiftName)));
        }

        var fileName = L.T("Csv_FileAlarm", _alFrom, _alTo);
        await JS.InvokeVoidAsync("KanbanECharts.download", fileName, "\uFEFF" + sb, "text/csv;charset=utf-8");
    }

    private void ResetAlarm()
    {
        AlHasQueried = false;
        AlIsLoading = false;
        AlAnalysisDone = false;
        AlPage = 1;
        AlTotalCount = 0;
        AlTotalPages = 0;
        AlRows = [];
        AlTriggered = 0;
        AlRecovered = 0;
        AlPending = 0;
        AlChartOption = null;
        AlInsight = null;
        AlError = null;
        AlarmName = "";
        AlarmNameOptions = [];
        _alAll = [];
    }
}
