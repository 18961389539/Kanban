using System.Globalization;
using Kanban.Contracts.Dtos;
using Kanban.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Kanban.Web.Pages;

/// <summary>
/// 历史查询页：共享筛选状态 + 通用查询助手（四 Tab 共用）。
/// 与 WPF HistoryQueryViewModel 对等：快捷时间口径、服务端分页并发拉取、LatestFirst 语义。
/// </summary>
public partial class HistoryQuery
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm";
    private const int DefaultQuickIndex = 1; // 今天（窗口越小全量拉取越快；近 7/30 天数据量大时自动降级提示）

    // ──────────── Tab 状态 ────────────
    private int TabIndex { get; set; }
    private string[] TabTitles => [L.T("Tab_Production"), L.T("Tab_Status"), L.T("Tab_Alarm"), L.T("Tab_Oee")];
    private bool IsAnyLoading => ProdIsLoading || StIsLoading || AlIsLoading || OeIsLoading;

    // ──────────── 共享筛选条件 ────────────
    private List<(string Id, string Name)> DeviceOptions { get; set; } = [];
    private string DeviceId { get; set; } = "";
    private int QuickIndex { get; set; } = DefaultQuickIndex;
    private string FromText { get; set; } = "";
    private string ToText { get; set; } = "";
    private string ShiftName { get; set; } = "";

    /// <summary>当前 Tab 的班次下拉（各 Tab 查询后按窗口实际班次刷新，WPF 同口径）。</summary>
    private List<string> ActiveShiftOptions { get; set; } = [];

    /// <summary>跨 Tab 校验消息（时间范围非法 / 未选设备等），渲染在筛选栏下方。</summary>
    private string? ValidationMessage { get; set; }

    /// <summary>设备配置缓存（OEE Tab 的目标产能/设备名）。</summary>
    private List<DeviceConfigDto> _deviceConfigs = [];

    protected override async Task OnInitializedAsync()
    {
        ApplyQuickRange(QuickIndex);
        // 连接 Collector（DashboardState 幂等 + 失败自动重试；历史查询走独立 Invoke 连接，见 ADR-1）
        await Dashboard.InitializeAsync();
        await LoadDevicesAsync();
    }

    /// <summary>设备下拉：优先快照列表（实时数据源），未就绪时兜底设备配置。</summary>
    private async Task LoadDevicesAsync()
    {
        if (Dashboard.Snapshots.Count > 0)
        {
            DeviceOptions = Dashboard.Snapshots.Select(s => (s.DeviceId, s.DeviceName)).ToList();
        }
        try
        {
            _deviceConfigs = [.. await Dashboard.QueryDevicesAsync()];
            if (DeviceOptions.Count == 0)
                DeviceOptions = _deviceConfigs.Select(d => (d.Id, d.Name)).ToList();
        }
        catch (Exception)
        {
            // Collector 未就绪：仅显示"全部设备"，查询时错误横幅会提示连接状态
        }
    }

    private void SwitchTab(int idx)
    {
        if (TabIndex == idx) return;
        TabIndex = idx;
        ValidationMessage = null;
    }

    private void OnQuickChanged(ChangeEventArgs e)
    {
        QuickIndex = int.TryParse(e.Value?.ToString(), out var v) ? v : -1;
        if (QuickIndex > 0) ApplyQuickRange(QuickIndex);
    }

    private void OnFromChanged(ChangeEventArgs e) => FromText = e.Value?.ToString() ?? "";

    private void OnToChanged(ChangeEventArgs e) => ToText = e.Value?.ToString() ?? "";

    /// <summary>快捷时间范围（与 WPF HistoryQueryViewModel 的 1..8 口径对齐，Web 取到"当前时刻"而非"当日末"）。
    /// "当前时刻"用工厂墙钟（Dashboard.ServerNow）——浏览器时区与工厂不同时，"今天/昨天"仍按工厂日历计算。</summary>
    private void ApplyQuickRange(int index)
    {
        var now = Dashboard.ServerNow;
        (FromText, ToText) = index switch
        {
            1 => (now.Date.ToString(TimeFormat), now.ToString(TimeFormat)),                                  // 今天
            2 => (now.Date.AddDays(-1).ToString(TimeFormat), now.Date.AddSeconds(-1).ToString(TimeFormat)), // 昨天
            3 => (now.Date.AddDays(-7).ToString(TimeFormat), now.ToString(TimeFormat)),                      // 近 7 天
            4 => (now.Date.AddDays(-30).ToString(TimeFormat), now.ToString(TimeFormat)),                     // 近 30 天
            5 => (StartOfWeek(now).ToString(TimeFormat), now.ToString(TimeFormat)),                          // 本周（周一为起点）
            6 => (new DateTime(now.Year, now.Month, 1).ToString(TimeFormat), now.ToString(TimeFormat)),      // 本月
            _ => (FromText, ToText), // 自定义：保持手动输入
        };
    }

    private static DateTime StartOfWeek(DateTime now)
    {
        var diff = (7 + (now.DayOfWeek - DayOfWeek.Monday)) % 7;
        return now.Date.AddDays(-diff);
    }

    private bool TryParseRange(out DateTime from, out DateTime to)
    {
        from = to = default;
        return DateTime.TryParseExact(FromText, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
            && DateTime.TryParseExact(ToText, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out to)
            && from <= to;
    }

    /// <summary>查询入口：校验共享时间范围后分发到当前 Tab。</summary>
    private async Task SearchAsync()
    {
        ValidationMessage = null;
        if (!TryParseRange(out var from, out var to))
        {
            ValidationMessage = L.T("Hq_ValidationRange");
            return;
        }
        switch (TabIndex)
        {
            case 0: await ProdSearchAsync(from, to); break;
            case 1: await StSearchAsync(from, to); break;
            case 2: await AlSearchAsync(from, to); break;
            default: await OeSearchAsync(from, to); break;
        }
    }

    // 导出口径 = 窗口全量：产量 Tab 须等全量分析完成（_prodWindow 就绪）才允许导出，
    // 否则表格页已出但全量未回，会导出空 CSV。
    private bool CanExportCurrentTab => TabIndex switch
    {
        0 => ProdHasQueried && ProdTotalCount > 0 && !ProdIsLoading && ProdAnalysisDone,
        1 => StHasQueried && StTotalCount > 0 && !StIsLoading,
        2 => AlHasQueried && AlTotalCount > 0 && !AlIsLoading,
        _ => OeHasQueried && !OeIsLoading,
    };

    private async Task ExportCurrentTabAsync()
    {
        switch (TabIndex)
        {
            case 0: await ProdExportCsvAsync(); break;
            case 1: await StExportCsvAsync(); break;
            case 2: await AlExportCsvAsync(); break;
            default: await OeExportCsvAsync(); break;
        }
    }

    private void Reset()
    {
        ValidationMessage = null;
        QuickIndex = DefaultQuickIndex;
        ApplyQuickRange(QuickIndex);
        ShiftName = "";
        ResetProduction();
        ResetStatus();
        ResetAlarm();
        ResetOee();
    }

    // ──────────── 通用查询助手 ────────────

    /// <summary>
    /// 服务端分页并发拉取全量（单源实现见 <see cref="HistoryFetch.FetchAllAsync{T}"/>，
    /// 历史查询页与报警中心共用）。返回 (数据, 是否超出上限截断)。
    /// </summary>
    private async Task<(List<T> Items, bool Truncated)> FetchAllAsync<T>(
        HistoryQueryType type, DateTime from, DateTime to, string? deviceId, string? shiftName,
        Func<HistoryQueryResponse, IReadOnlyList<T>> selector)
        => await HistoryFetch.FetchAllAsync(Dashboard, type, from, to, deviceId, shiftName, selector);

    /// <summary>LatestFirst 语义（单源实现见 <see cref="HistoryFetch.FetchLatestBeforeAsync{T}"/>）。</summary>
    private async Task<List<T>> FetchLatestBeforeAsync<T>(
        HistoryQueryType type, DateTime before, string? deviceId, string? shiftName,
        Func<HistoryQueryResponse, IReadOnlyList<T>> selector)
        => await HistoryFetch.FetchLatestBeforeAsync(Dashboard, type, before, deviceId, shiftName, selector);

    /// <summary>客户端分页（状态/报警 Tab 与 WPF Remote 模式一致：全量拉取后客户端翻页）。</summary>
    private static (List<T> Rows, int TotalPages) PageItems<T>(List<T> all, int page, int pageSize)
    {
        var totalPages = ProductionAnalysis.CalcTotalPages(all.Count, pageSize);
        if (page > totalPages) page = Math.Max(1, totalPages);
        if (page < 1) page = 1;
        var rows = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (rows, totalPages);
    }

    // 百分比口径单源：UiPalette.Pct（P1，与 WPF 一致；此前本页 P0 与复盘页 P1 不一致）
    private static string Pct(double v) => UiPalette.Pct(v);

    /// <summary>设备状态字 → 当前语言文本（对齐 WPF HistoryQueryHelper.GetStateText）。</summary>
    private static string StateText(int state) => state switch
    {
        (int)Kanban.Contracts.Enums.DeviceStatus.Running => L.T("Status_Running"),
        (int)Kanban.Contracts.Enums.DeviceStatus.Alarm => L.T("Status_Alarm"),
        (int)Kanban.Contracts.Enums.DeviceStatus.Paused => L.T("Status_Paused"),
        (int)Kanban.Contracts.Enums.DeviceStatus.Offline => L.T("Status_Offline"),
        _ => L.T("Hq_StateUnknown"),
    };

    private string AlarmTypeText(Kanban.Contracts.Enums.AlarmEventType type)
        => AlarmAnalysis.GetEventTypeText(type, L.T);

    private static string PageSummary(int page, int totalPages, int total)
        => L.T("Hq_PageInfo", page, totalPages, total);

    // ──────────── 共享 ECharts 选项片段（深色主题对齐看板调色板） ────────────

    protected static Dictionary<string, object?> Tooltip() => new()
    {
        ["trigger"] = "axis",
        ["backgroundColor"] = "#212834",
        ["borderColor"] = "#2A323F",
        ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#E5E7EB" },
    };

    protected static Dictionary<string, object?> AxisLabel() => new()
    {
        ["color"] = "#8B92A0",
        ["hideOverlap"] = true,
    };

    protected static Dictionary<string, object?> AxisLine() => new()
    {
        ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#2A323F" },
    };

    protected static Dictionary<string, object?> ValueAxis() => new()
    {
        ["type"] = "value",
        ["axisLabel"] = new Dictionary<string, object?> { ["color"] = "#8B92A0" },
        ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#212834" } },
    };

    protected static object[] DataZoom() =>
    [
        new Dictionary<string, object?> { ["type"] = "inside" },
        new Dictionary<string, object?> { ["type"] = "slider", ["height"] = 14, ["bottom"] = 6, ["borderColor"] = "#2A323F" },
    ];

    protected static Dictionary<string, object?> Legend(string[] data) => new()
    {
        ["data"] = data,
        ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#9CA3AF" },
    };
}
