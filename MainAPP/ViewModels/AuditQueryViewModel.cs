using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvHelper.Configuration.Attributes;
using Kanban.Core.Entities;
using Kanban.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using Serilog;

namespace MainAPP.ViewModels;

/// <summary>
/// 操作审计查询页 ViewModel：按时间/操作人/操作类型/结果过滤审计记录，分页展示；
/// 支持将当前过滤结果导出 CSV（可读）/JSON（完整归档，含前后值）到本地文件。
/// 仅 Admin 可访问（NavigationPageCatalog 的 RequiredRole 控制）。
/// </summary>
public partial class AuditQueryViewModel : ObservableObject
{
    private readonly IAuditService _auditService;
    private readonly IDialogService _dialog;

    [ObservableProperty]
    private ObservableCollection<AuditEntry> _entries = new();

    [ObservableProperty]
    private DateTime _from = DateTime.Today.AddDays(-7);

    [ObservableProperty]
    private DateTime _to = DateTime.Now;

    [ObservableProperty]
    private string _operatorFilter = string.Empty;

    [ObservableProperty]
    private string _actionFilter = string.Empty;

    /// <summary>结果筛选：0=全部 1=成功 2=失败（ComboBox SelectedIndex 直接绑定）。</summary>
    [ObservableProperty]
    private int _resultFilterIndex;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private int _pageSize = 100;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private int _totalPages;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private int _pageSucceeded;

    [ObservableProperty]
    private int _pageFailed;

    /// <summary>快捷时间预设：0=自定义 1=今天 2=近7天(默认) 3=近30天 4=本月；手动改日期自动回到 0。</summary>
    [ObservableProperty]
    private int _presetIndex = 2;

    /// <summary>当前选中的审计记录（详情面板展示完整前后值）。</summary>
    [ObservableProperty]
    private AuditEntry? _selectedEntry;

    private bool _applyingPreset;

    public double PageSuccessRate => Entries.Count == 0 ? 0 : PageSucceeded * 100d / Entries.Count;

    public AuditQueryViewModel(IAuditService auditService, IDialogService dialog)
    {
        _auditService = auditService;
        _dialog = dialog;
    }

    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
    public string PageSummary => string.Format(Strings.K624, Page, TotalPages, Total);

    [RelayCommand]
    private void Query()
    {
        Page = 1;
        RunQuery();
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private void PreviousPage()
    {
        if (Page <= 1) return;
        Page--;
        RunQuery();
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private void NextPage()
    {
        if (Page >= TotalPages) return;
        Page++;
        RunQuery();
    }

    [RelayCommand]
    private void Reset()
    {
        OperatorFilter = string.Empty;
        ActionFilter = string.Empty;
        ResultFilterIndex = 0;
        Page = 1;
        // 先置 0 再置默认预设：绕开 setter 值相同不触发回调的短路，保证总是应用默认预设并重新查询
        PresetIndex = 0;
        PresetIndex = 2;
    }

    partial void OnPresetIndexChanged(int value) => ApplyPresetCore(value);

    /// <summary>应用快捷时间预设（设置起止时间 + 回第一页查询）。</summary>
    private void ApplyPresetCore(int preset)
    {
        if (preset <= 0) return;
        var now = DateTime.Now;
        _applyingPreset = true;
        try
        {
            From = preset switch
            {
                1 => now.Date,
                2 => now.Date.AddDays(-7),
                3 => now.Date.AddDays(-30),
                _ => new DateTime(now.Year, now.Month, 1),
            };
            To = now;
        }
        finally
        {
            _applyingPreset = false;
        }
        Page = 1;
        RunQuery();
    }

    /// <summary>手动修改日期 → 取消预设高亮（当前为自定义范围）。</summary>
    partial void OnFromChanged(DateTime value) => ClearPresetIfCustom();

    partial void OnToChanged(DateTime value) => ClearPresetIfCustom();

    private void ClearPresetIfCustom()
    {
        if (_applyingPreset || PresetIndex == 0) return;
        PresetIndex = 0;
    }

    /// <summary>当前结果筛选条件（导出与查询共用，保证口径一致）。</summary>
    private bool? CurrentSucceededFilter => ResultFilterIndex switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    /// <summary>归档导出上限（条）。与 AuditService.QueryAll 默认 maxResults 保持一致。</summary>
    private const int ExportMaxResults = 10000;

    /// <summary>
    /// 按当前过滤条件拉取全部审计记录（归档用，最多 10000 条）。
    /// 后台线程安全：命中上限时返回 Truncated=true，由调用方在 UI 线程提示，禁止无感知的截断归档。
    /// </summary>
    private (List<AuditEntry>? Items, bool Truncated, int Total) QueryAllForExportCore()
    {
        var (items, total) = _auditService.QueryAll(
            From, To, OperatorFilter, ActionFilter, null, CurrentSucceededFilter, ExportMaxResults);
        if (total <= items.Count) return (items, false, total);
        return (null, true, total);
    }

    /// <summary>导出当前过滤结果为 CSV（表格视角，含前后值 JSON 列）。</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var path = _dialog.ShowSaveFileDialog(
            Strings.K633,
            $"audit_{DateTime.Now:yyyyMMddHHmm}.csv",
            Strings.M310);
        if (string.IsNullOrWhiteSpace(path)) return;

        IsLoading = true;
        try
        {
            // 后台执行（审查修复 2026-08-13）：此前 QueryAllForExport（同步 EF 全量 10000 条）
            // 与 CSV 构建都在 UI 线程，大审计库导出瞬间卡死 UI；IsLoading 接线一并修复
            var (items, truncated, total) = await Task.Run(QueryAllForExportCore);
            if (truncated)
            {
                Log.Warning("审计归档截断：匹配 {Total} 条，超过导出上限 {Limit} 条", total, ExportMaxResults);
                _dialog.NotifyWarning(string.Format(Strings.K635, total, ExportMaxResults));
                return;
            }
            if (items is null || items.Count == 0)
            {
                _dialog.NotifyInfo(Strings.K634);
                return;
            }

            var csv = await Task.Run(() =>
            {
                var rows = items.Select(e => new AuditCsvRow
                {
                    Timestamp = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    // 用户可控文本（操作人/对象标识/详情）做公式注入防护
                    Operator = HistoryQueryHelper.SanitizeCsvCell(e.Operator),
                    Action = HistoryQueryHelper.SanitizeCsvCell(e.Action),
                    TargetType = HistoryQueryHelper.SanitizeCsvCell(e.TargetType),
                    TargetId = HistoryQueryHelper.SanitizeCsvCell(e.TargetId),
                    Succeeded = e.Succeeded ? "Success" : "Failed",
                    Detail = HistoryQueryHelper.SanitizeCsvCell(e.Detail),
                    Before = HistoryQueryHelper.SanitizeCsvCell(e.BeforeJson),
                    After = HistoryQueryHelper.SanitizeCsvCell(e.AfterJson),
                }).ToList();
                return HistoryQueryHelper.BuildCsv(rows);
            });
            await Task.Run(() => File.WriteAllText(path, csv, new UTF8Encoding(true)));
            AuditLog.Record("Export.Csv", "Export", Path.GetFileName(path), detail: $"审计归档 {items.Count} 条");
            _dialog.NotifySuccess(string.Format(Strings.K631, items.Count));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "审计 CSV 导出失败");
            _dialog.NotifyError(string.Format(Strings.K632, ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>导出当前过滤结果为 JSON（完整归档，保留结构化前后值，供后续导入/审计系统消费）。</summary>
    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var path = _dialog.ShowSaveFileDialog(
            Strings.K633,
            $"audit_{DateTime.Now:yyyyMMddHHmm}.json",
            Strings.K695);
        if (string.IsNullOrWhiteSpace(path)) return;

        IsLoading = true;
        try
        {
            // 后台执行（审查修复 2026-08-13）：此前 10000 条大 JSON 序列化在 UI 线程
            var (items, truncated, total) = await Task.Run(QueryAllForExportCore);
            if (truncated)
            {
                Log.Warning("审计归档截断：匹配 {Total} 条，超过导出上限 {Limit} 条", total, ExportMaxResults);
                _dialog.NotifyWarning(string.Format(Strings.K635, total, ExportMaxResults));
                return;
            }
            if (items is null || items.Count == 0)
            {
                _dialog.NotifyInfo(Strings.K634);
                return;
            }

            var json = await Task.Run(() => JsonSerializer.Serialize(items,
                new JsonSerializerOptions { WriteIndented = true }));
            await Task.Run(() => File.WriteAllText(path, json, new UTF8Encoding(true)));
            AuditLog.Record("Export.Json", "Export", Path.GetFileName(path), detail: $"审计归档 {items.Count} 条");
            _dialog.NotifySuccess(string.Format(Strings.K631, items.Count));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "审计 JSON 导出失败");
            _dialog.NotifyError(string.Format(Strings.K632, ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private sealed class AuditCsvRow
    {
        [Name("Timestamp")] public string Timestamp { get; init; } = string.Empty;
        [Name("Operator")] public string Operator { get; init; } = string.Empty;
        [Name("Action")] public string Action { get; init; } = string.Empty;
        [Name("TargetType")] public string TargetType { get; init; } = string.Empty;
        [Name("TargetId")] public string? TargetId { get; init; }
        [Name("Result")] public string Succeeded { get; init; } = string.Empty;
        [Name("Detail")] public string? Detail { get; init; }
        [Name("Before")] public string? Before { get; init; }
        [Name("After")] public string? After { get; init; }
    }

    /// <summary>查询版本号（审查修复 2026-08-13）：后台查询返回时校验，旧查询结果直接丢弃防覆盖新查询。</summary>
    private int _queryVersion;

    /// <summary>封送回 UI 线程（Dispatcher 缺失/关闭时直接执行，兼容测试环境）。</summary>
    private void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.BeginInvoke(action);
    }

    private void RunQuery()
    {
        // 后台执行（审查修复 2026-08-13）：审计查询是同步 EF/SQLite，此前在 UI 线程执行，
        // 大审计库翻页/查询可卡死 UI；_isLoading 声明后从未接线一并修复
        QueryError = null;
        IsLoading = true;
        var requestVersion = ++_queryVersion;
        var from = From; var to = To;
        var op = OperatorFilter; var action = ActionFilter; var succeeded = CurrentSucceededFilter;
        var page = Page; var pageSize = PageSize;
        Task.Run(() =>
        {
            try
            {
                var (items, total) = _auditService.QueryPaged(from, to, op, action, null, succeeded, page, pageSize);
                Dispatch(() =>
                {
                    if (requestVersion != _queryVersion) return;
                    Entries.Clear();
                    foreach (var item in items)
                        Entries.Add(item);

                    PageSucceeded = items.Count(item => item.Succeeded);
                    PageFailed = items.Count - PageSucceeded;
                    OnPropertyChanged(nameof(PageSuccessRate));

                    Total = total;
                    TotalPages = Math.Max(1, (total + pageSize - 1) / pageSize);
                    OnPropertyChanged(nameof(HasPreviousPage));
                    OnPropertyChanged(nameof(HasNextPage));
                    OnPropertyChanged(nameof(PageSummary));
                    PreviousPageCommand.NotifyCanExecuteChanged();
                    NextPageCommand.NotifyCanExecuteChanged();
                    IsLoading = false;
                });
            }
            catch (Exception ex)
            {
                Dispatch(() =>
                {
                    if (requestVersion != _queryVersion) return;
                    Log.Error(ex, "审计查询失败");
                    QueryError = ex.Message;
                    Total = 0;
                    TotalPages = 1;
                    OnPropertyChanged(nameof(HasPreviousPage));
                    OnPropertyChanged(nameof(HasNextPage));
                    OnPropertyChanged(nameof(PageSummary));
                    IsLoading = false;
                });
            }
        });
    }
}
