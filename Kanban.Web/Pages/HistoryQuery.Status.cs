using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Kanban.Web.Services;
using Microsoft.JSInterop;

namespace Kanban.Web.Pages;

/// <summary>历史查询页 Tab 1：状态（与 WPF StatusQueryViewModel 对等）。</summary>
public partial class HistoryQuery
{
    private const int StatusTablePageSize = 50;

    // ──────────── 状态 Tab 状态 ────────────
    private bool StHasQueried { get; set; }
    private bool StIsLoading { get; set; }
    private int StPage { get; set; } = 1;
    private int StTotalCount { get; set; }
    private int StTotalPages { get; set; }
    private List<StatusTransitionRecordDto> StRows { get; set; } = [];
    private double StRunSeconds { get; set; }
    private double StAlarmSeconds { get; set; }
    private double StPauseSeconds { get; set; }
    private object? StPieOption { get; set; }
    private object? StDailyOption { get; set; }
    private object? StGanttOption { get; set; }
    private string? StInsight { get; set; }
    private string? StError { get; set; }

    // 窗口缓存：全量拉取一次（事件型数据量小，与 WPF Remote 一致），翻页客户端切
    private List<StatusTransitionRecordDto> _stAll = [];
    private int _stInitialState = 1;
    private DateTime _stFrom;
    private DateTime _stTo;
    private string? _stDevice;
    private string? _stShift;

    private string StRunText => Kanban.Contracts.Formatting.DurationFormatter.FormatCompact(StRunSeconds);
    private string StAlarmText => Kanban.Contracts.Formatting.DurationFormatter.FormatCompact(StAlarmSeconds);
    private string StPauseText => Kanban.Contracts.Formatting.DurationFormatter.FormatCompact(StPauseSeconds);
    private string StPageSummaryText => PageSummary(StPage, StTotalPages, StTotalCount);

    private async Task StSearchAsync(DateTime from, DateTime to)
    {
        StError = null;
        if (string.IsNullOrEmpty(DeviceId))
        {
            ValidationMessage = L.T("Hq_NeedDevice");
            return;
        }
        _stFrom = from;
        _stTo = to;
        _stDevice = DeviceId;
        _stShift = string.IsNullOrEmpty(ShiftName) ? null : ShiftName;
        StPage = 1;
        await StRunQueryAsync();
    }

    private async Task StRunQueryAsync()
    {
        StIsLoading = true;
        StError = null;
        StPieOption = null;
        StDailyOption = null;
        StGanttOption = null;
        StInsight = null;
        try
        {
            var (all, _) = await FetchAllAsync<StatusTransitionRecordDto>(
                HistoryQueryType.StatusTransition, _stFrom, _stTo, _stDevice, _stShift, r => r.StatusTransitions);
            _stAll = all;
            StTotalCount = all.Count;

            // 初始状态：窗口前最近一条的 CurrentState（LatestFirst 语义，WPF Remote 同实现）
            var initialState = 1;
            var lastBefore = await FetchLatestBeforeAsync<StatusTransitionRecordDto>(
                HistoryQueryType.StatusTransition, _stFrom, _stDevice, _stShift, r => r.StatusTransitions);
            if (lastBefore.Count > 0)
                initialState = (int)lastBefore[0].CurrentState;
            _stInitialState = initialState;

            var effectiveTo = _stTo > DateTime.Now ? DateTime.Now : _stTo;
            var durations = StatusAnalysis.CalculateStateDurations(_stAll, _stFrom, effectiveTo, initialState);
            StRunSeconds = durations.RunTime;
            StAlarmSeconds = durations.AlarmTime;
            StPauseSeconds = durations.PausedTime;

            var daily = StatusAnalysis.BuildDailyDurations(_stAll, _stFrom, effectiveTo, initialState);
            StBuildPieOption();
            StBuildDailyOption(daily);
            StBuildGanttOption(StatusAnalysis.BuildSegments(_stAll, _stFrom, _stTo, initialState));

            if (StTotalCount > 0)
                StInsight = StatusAnalysis.BuildInsight(_stAll, _stFrom, _stTo, initialState, L.T);

            ActiveShiftOptions = _stAll.Select(t => t.ShiftName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            (StRows, StTotalPages) = PageItems(_stAll, StPage, StatusTablePageSize);
            StHasQueried = true;
        }
        catch (Exception)
        {
            StError = L.T("Hq_QueryFailed");
            StHasQueried = true;
            StTotalCount = 0;
            StTotalPages = 0;
            StRows = [];
        }
        finally
        {
            StIsLoading = false;
        }
    }

    /// <summary>状态时长占比饼图（对齐 WPF BuildStatusChart 的 PieSeries）。</summary>
    private void StBuildPieOption()
    {
        StPieOption = new Dictionary<string, object?>
        {
            ["backgroundColor"] = "transparent",
            ["tooltip"] = new Dictionary<string, object?>
            {
                ["trigger"] = "item",
                ["backgroundColor"] = "#212834",
                ["borderColor"] = "#2A323F",
                ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#E5E7EB" },
            },
            ["legend"] = Legend([L.T("Status_Running"), L.T("Status_Alarm"), L.T("Status_Paused")]),
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "pie",
                    ["radius"] = new[] { "45%", "72%" },
                    ["center"] = new[] { "50%", "52%" },
                    ["label"] = new Dictionary<string, object?> { ["color"] = "#9CA3AF", ["formatter"] = "{b}: {c} h" },
                    ["data"] = new object[]
                    {
                        new Dictionary<string, object?> { ["name"] = L.T("Status_Running"), ["value"] = Math.Round(StRunSeconds / 3600.0, 2), ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#34D399" } },
                        new Dictionary<string, object?> { ["name"] = L.T("Status_Alarm"), ["value"] = Math.Round(StAlarmSeconds / 3600.0, 2), ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#F87171" } },
                        new Dictionary<string, object?> { ["name"] = L.T("Status_Paused"), ["value"] = Math.Round(StPauseSeconds / 3600.0, 2), ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#FBBF24" } },
                    },
                },
            },
        };
    }

    /// <summary>按天堆叠柱状图（对齐 WPF BuildStatusBarChart：运行/报警/待机每天分布，小时）。</summary>
    private void StBuildDailyOption(List<(DateTime Date, double RunHours, double AlarmHours, double PauseHours)> daily)
    {
        StDailyOption = new Dictionary<string, object?>
        {
            ["backgroundColor"] = "transparent",
            ["tooltip"] = Tooltip(),
            ["legend"] = Legend([L.T("Status_Running"), L.T("Status_Alarm"), L.T("Status_Paused")]),
            ["grid"] = new Dictionary<string, object?> { ["left"] = 48, ["right"] = 16, ["top"] = 36, ["bottom"] = 28 },
            ["xAxis"] = new Dictionary<string, object?>
            {
                ["type"] = "category",
                ["data"] = daily.Select(d => d.Date.ToString("MM-dd")).ToList(),
                ["axisLabel"] = AxisLabel(),
                ["axisLine"] = AxisLine(),
            },
            ["yAxis"] = ValueAxis(),
            ["series"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = L.T("Status_Running"), ["type"] = "bar", ["stack"] = "st", ["data"] = daily.Select(d => Math.Round(d.RunHours, 2)).ToList(), ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#34D399" } },
                new Dictionary<string, object?> { ["name"] = L.T("Status_Alarm"), ["type"] = "bar", ["stack"] = "st", ["data"] = daily.Select(d => Math.Round(d.AlarmHours, 2)).ToList(), ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#F87171" } },
                new Dictionary<string, object?> { ["name"] = L.T("Status_Paused"), ["type"] = "bar", ["stack"] = "st", ["data"] = daily.Select(d => Math.Round(d.PauseHours, 2)).ToList(), ["itemStyle"] = new Dictionary<string, object?> { ["color"] = "#FBBF24" } },
            },
        };
    }

    /// <summary>
    /// 状态甘特图：横轴时间、纵轴三状态（运行/报警/待机）。
    /// 用 ECharts 时间轴上 [起点, 时长] 的 bar 数据项实现（纯数据方案，无需 custom series）。
    /// 对齐 WPF BuildStatusGanttChart 语义。
    /// </summary>
    private void StBuildGanttOption(List<(DateTime Start, DateTime End, int State)> segments)
    {
        var valid = segments.Where(s => s.State >= (int)DeviceStatus.Running && s.State <= (int)DeviceStatus.Paused).ToList();
        var byState = new Dictionary<int, List<object[]>>();
        foreach (var s in valid)
        {
            var startMs = new DateTimeOffset(s.Start).ToUnixTimeMilliseconds();
            var durMs = (long)(s.End - s.Start).TotalMilliseconds;
            if (durMs <= 0) continue;
            if (!byState.TryGetValue(s.State, out var list))
                byState[s.State] = list = [];
            list.Add([startMs, durMs]);
        }

        Dictionary<string, object?> MakeSeries(int state, string name, string color) => new()
        {
            ["name"] = name,
            ["type"] = "bar",
            ["data"] = byState.TryGetValue(state, out var d) ? d : [],
            ["itemStyle"] = new Dictionary<string, object?> { ["color"] = color },
            ["barCategoryGap"] = "30%",
        };

        StGanttOption = new Dictionary<string, object?>
        {
            ["backgroundColor"] = "transparent",
            ["tooltip"] = new Dictionary<string, object?>
            {
                ["trigger"] = "item",
                ["backgroundColor"] = "#212834",
                ["borderColor"] = "#2A323F",
                ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#E5E7EB" },
            },
            ["legend"] = Legend([L.T("Status_Running"), L.T("Status_Alarm"), L.T("Status_Paused")]),
            ["grid"] = new Dictionary<string, object?> { ["left"] = 16, ["right"] = 24, ["top"] = 36, ["bottom"] = 44 },
            ["xAxis"] = new Dictionary<string, object?>
            {
                ["type"] = "time",
                ["axisLabel"] = AxisLabel(),
                ["axisLine"] = AxisLine(),
            },
            ["yAxis"] = new Dictionary<string, object?>
            {
                ["type"] = "category",
                ["data"] = new[] { L.T("Status_Running"), L.T("Status_Alarm"), L.T("Status_Paused") },
                ["axisLabel"] = new Dictionary<string, object?> { ["color"] = "#9CA3AF" },
                ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#212834" } },
            },
            ["dataZoom"] = DataZoom(),
            ["series"] = new object[]
            {
                MakeSeries((int)DeviceStatus.Running, L.T("Status_Running"), "#34D399"),
                MakeSeries((int)DeviceStatus.Alarm, L.T("Status_Alarm"), "#F87171"),
                MakeSeries((int)DeviceStatus.Paused, L.T("Status_Paused"), "#FBBF24"),
            },
        };
    }

    private void StPrevPage()
    {
        if (StPage <= 1) return;
        StPage--;
        (StRows, StTotalPages) = PageItems(_stAll, StPage, StatusTablePageSize);
    }

    private void StNextPage()
    {
        if (StPage >= StTotalPages) return;
        StPage++;
        (StRows, StTotalPages) = PageItems(_stAll, StPage, StatusTablePageSize);
    }

    private async Task StExportCsvAsync()
    {
        if (StRows.Count == 0)
        {
            ValidationMessage = L.T("Hq_ExportEmpty");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# 运行时长：{StRunSeconds / 3600.0:F2}h，报警时长：{StAlarmSeconds / 3600.0:F2}h，待机时长：{StPauseSeconds / 3600.0:F2}h");
        sb.AppendLine($"# {StInsight ?? "—"}");
        sb.AppendLine(string.Join(',',
            C("事件时间"), C("设备ID"), C("设备名称"), C("前一状态"), C("当前状态"), C("前一状态文本"), C("当前状态文本"), C("班次")));
        foreach (var r in StRows)
        {
            sb.AppendLine(string.Join(',',
                C(r.EventTime.ToString("yyyy-MM-dd HH:mm:ss")), C(r.DeviceId), C(r.DeviceName),
                ((int)r.PreviousState).ToString(), ((int)r.CurrentState).ToString(),
                C(StateText((int)r.PreviousState)), C(StateText((int)r.CurrentState)), C(r.ShiftName)));
        }

        var fileName = $"状态时长_{_stFrom:yyyyMMdd}_{_stTo:yyyyMMdd}.csv";
        await JS.InvokeVoidAsync("KanbanECharts.download", fileName, "\uFEFF" + sb, "text/csv;charset=utf-8");
    }

    private void ResetStatus()
    {
        StHasQueried = false;
        StIsLoading = false;
        StPage = 1;
        StTotalCount = 0;
        StTotalPages = 0;
        StRows = [];
        StRunSeconds = 0;
        StAlarmSeconds = 0;
        StPauseSeconds = 0;
        StPieOption = null;
        StDailyOption = null;
        StGanttOption = null;
        StInsight = null;
        StError = null;
        _stAll = [];
    }
}
