using System.Collections.ObjectModel;
using MainAPP.Resources;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using NodaTime;
using Serilog;

namespace MainAPP.ViewModels;

/// <summary>
/// 上次查询条件的持久化快照。跨会话保留用户在查询页选择的 Tab/设备/时间/班次，
/// 避免每次重启应用都要重新设置筛选条件。仅保存 UI 选择状态，不含查询结果。
/// </summary>
internal sealed class LastQuerySnapshot
{
    public int TabIndex { get; set; }
    public string? DeviceId { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int QuickTimeIndex { get; set; } = -1;
    public string? ShiftName { get; set; }
    public string? AlarmName { get; set; }
}

public partial class HistoryQueryViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryService _historyService;
    private readonly DeviceRepository _deviceRepository;
    private readonly IDialogService _dialog;
    private readonly AppSettings _appSettings;

    public ProductionQueryViewModel ProductionQuery { get; private set; }
    public StatusQueryViewModel StatusQuery { get; private set; }
    public AlarmQueryViewModel AlarmQuery { get; private set; }
    public OeeQueryViewModel OeeQuery { get; private set; }

    private const int PageSize = 50;

    public const int NgAlarmThreshold = 5;
    public const double LowPerformanceThreshold = 0.6;
    /// <summary>
    /// 阈值表达式字符串（供 XAML ConverterParameter 通过 x:Static 引用）。
    /// XAML ConverterParameter 不支持 Binding，但支持 x:Static。
    /// 用 static readonly 字段从 const 拼接，避免字面值散落导致改一处漏一处。
    /// 格式需匹配 ComparisonConverter 的解析：运算符 + 数值（如 ">=5"、"&lt;0.6"）。
    /// </summary>
    public static readonly string NgAlarmThresholdExpr = ">=" + NgAlarmThreshold.ToString(CultureInfo.InvariantCulture);
    public static readonly string LowPerformanceThresholdExpr = "<" + LowPerformanceThreshold.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _hasQueried;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// 导出进行中标志：绑定到导出按钮 IsEnabled=false + 显示 LoadingCircle，避免大表导出 UI 假死与重复点击。
    /// </summary>
    [ObservableProperty]
    private bool _isExporting;

    private bool _queryPending;
    private int _queryVersion;

    public bool IsEmptyResult => HasQueried && TotalCount == 0 && !HasQueryError;

    /// <summary>当前查询结果摘要，显示在筛选栏标题区。</summary>
    public string QuerySummaryText => !HasQueried
        ? Strings.M096
        : HasQueryError
            ? Strings.M097
        : TotalCount == 0
            ? Strings.M098
            : string.Format(Strings.F069, TotalCount, CurrentPage, TotalPages);

    [ObservableProperty]
    private string _queryValidationMessage = string.Empty;

    [ObservableProperty]
    private string _queryErrorMessage = string.Empty;

    public bool HasQueryValidationError => !string.IsNullOrEmpty(QueryValidationMessage);
    public bool HasQueryError => !string.IsNullOrEmpty(QueryErrorMessage);

    partial void OnQueryValidationMessageChanged(string value)
        => OnPropertyChanged(nameof(HasQueryValidationError));

    partial void OnQueryErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasQueryError));
        OnPropertyChanged(nameof(IsEmptyResult));
        OnPropertyChanged(nameof(QuerySummaryText));
    }

    /// <summary>
    /// KPI 卡片可见性：仅在已查询且有数据时显示。
    /// 重置后 HasQueried=false，KPI 卡片隐藏，避免显示 0/0/0% 与"无数据"混淆。
    /// </summary>
    public bool IsKpiVisible => HasQueried && TotalCount > 0;

    public int EmptyStateCode
    {
        get
        {
            if (!HasQueried) return 0;
            if (string.IsNullOrEmpty(SelectedDeviceId)) return 1;
            return 2;
        }
    }

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsEmptyResult));
        OnPropertyChanged(nameof(EmptyStateCode));
        OnPropertyChanged(nameof(IsKpiVisible));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(QuerySummaryText));
    }

    partial void OnHasQueriedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmptyResult));
        OnPropertyChanged(nameof(EmptyStateCode));
        OnPropertyChanged(nameof(IsKpiVisible));
        ExportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(QuerySummaryText));
    }

    partial void OnCurrentPageChanged(int value)
    {
        PreviousPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(QuerySummaryText));
        NextPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnTotalPagesChanged(int value)
    {
        NextPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(QuerySummaryText));
    }

    partial void OnSelectedDeviceIdChanged(string? value)
    {
        OnPropertyChanged(nameof(EmptyStateCode));
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(EmptyStateCode));
        // Tab 切换后页面容量可能不同（产量/状态/报警分页，OEE 不分页），
        // 不重置会导致显示"第 3 页/共 1 页"等错位，且 QueryCurrentTab 跳过 (page-1)*pageSize 行。
        // 仅在已查询过时重置并自动查询当前 Tab，避免用户切 Tab 后看到旧数据/空表格的错位状态。
        if (HasQueried)
        {
            CurrentPage = 1;
            QueryCurrentTab();
        }
    }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string? _selectedDeviceId;

    [ObservableProperty]
    private DateTime _fromDate = DateTime.Today.AddDays(-1);

    [ObservableProperty]
    private DateTime _toDate = DateTime.Today.AddDays(1).AddSeconds(-1);

    [ObservableProperty]
    private string? _selectedShiftName;

    [ObservableProperty]
    private string? _selectedAlarmName;

    [ObservableProperty]
    private int _quickTimeIndex = -1;

    /// <summary>
    /// 标志位：防止 OnQuickTimeIndexChanged 设置 FromDate/ToDate 时反向触发
    /// OnFromDateChanged/OnToDateChanged 把 QuickTimeIndex 重置为 0（自定义）造成循环。
    /// RestoreLastQuery 也会设置此标志以避免恢复逻辑被干扰。
    /// </summary>
    private bool _isUpdatingQuickTime;

    partial void OnQuickTimeIndexChanged(int value)
    {
        if (_isUpdatingQuickTime) return;

        _isUpdatingQuickTime = true;
        try
        {
            (FromDate, ToDate) = GetQuickTimeRange(value, DateTime.Now);
        }
        finally
        {
            _isUpdatingQuickTime = false;
        }
    }

    /// <summary>
    /// 按快捷时间档位计算查询区间：1=今天 2=昨天 3=近7天 4=近30天 5/6=本/上一班次 7=本周 8=本月；
    /// 其他值（自定义/未选）原样返回当前 FromDate/ToDate。
    /// 供 OnQuickTimeIndexChanged 与 RestoreLastQuery 复用（审查修复 2026-08-13：
    /// 恢复时守卫抑制了变更回调，必须显式按档位重算日期，否则档位与日期错位）。
    /// </summary>
    private (DateTime From, DateTime To) GetQuickTimeRange(int index, DateTime now)
    {
        return index switch
        {
            1 => (now.Date, now.Date.AddDays(1).AddSeconds(-1)),
            2 => (now.Date.AddDays(-1), now.Date.AddSeconds(-1)),
            3 => (now.Date.AddDays(-7), now.Date.AddDays(1).AddSeconds(-1)),
            4 => (now.Date.AddDays(-30), now.Date.AddDays(1).AddSeconds(-1)),
            5 => GetShiftRange(now, 0),
            6 => GetShiftRange(now, -1),
            7 => GetWeekRange(now),
            8 => GetMonthRange(now),
            _ => (FromDate, ToDate)
        };
    }

    partial void OnFromDateChanged(DateTime value)
    {
        // 用户手动修改 FromDate 时，QuickTimeIndex 应回到"自定义"(0)，
        // 避免下拉还显示"今天/昨天"造成视觉欺骗。
        // _isUpdatingQuickTime=true 时表示是 OnQuickTimeIndexChanged 主动设的，不重置。
        if (!_isUpdatingQuickTime && QuickTimeIndex != 0 && QuickTimeIndex != -1)
            QuickTimeIndex = 0;
    }

    partial void OnToDateChanged(DateTime value)
    {
        if (!_isUpdatingQuickTime && QuickTimeIndex != 0 && QuickTimeIndex != -1)
            QuickTimeIndex = 0;
    }

    public ObservableCollection<DeviceFilterItem> DeviceFilterItems { get; } = new();

    public void RefreshDeviceFilterItems()
        => DeviceFilterHelper.Refresh(DeviceFilterItems, _deviceRepository);

    public ObservableCollection<FilterOption> ShiftFilterItems { get; } = new();

    /// <summary>
    /// 从 AppSettings.Shifts 初始化班次下拉，保证所有 Tab 在查询前就有完整班次列表。
    /// 之前只在产量 Tab 查询后才刷新，导致其他 Tab 的班次下拉为空或滞后。
    /// </summary>
    private void RefreshShiftFilterFromConfig()
    {
        var names = (_appSettings.Shifts ?? Enumerable.Empty<ShiftConfig>())
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        ShiftFilterItems.Clear();
        ShiftFilterItems.Add(new FilterOption(null, Strings.M099));
        foreach (var n in names)
            ShiftFilterItems.Add(new FilterOption(n, n));
    }

    private void RefreshShiftFilterItems(IEnumerable<string?> shiftNames)
    {
        var distinct = shiftNames.Where(n => !string.IsNullOrEmpty(n))
                                  .Select(n => n!)
                                  .Distinct()
                                  .OrderBy(n => n)
                                  .ToList();
        ShiftFilterItems.Clear();
        ShiftFilterItems.Add(new FilterOption(null, Strings.M099));
        foreach (var n in distinct)
            ShiftFilterItems.Add(new FilterOption(n, n));
    }

    public ObservableCollection<FilterOption> AlarmNameFilterItems { get; } = new();

    private void RefreshAlarmNameFilterItems(IEnumerable<string> alarmNames)
    {
        AlarmNameFilterItems.Clear();
        AlarmNameFilterItems.Add(new FilterOption(null, Strings.Web_Hq_AllAlarms));
        foreach (var n in alarmNames)
            AlarmNameFilterItems.Add(new FilterOption(n, n));
    }

    public HistoryQueryViewModel(
        IHistoryService historyService,
        DeviceRepository deviceRepo,
        AppSettings appSettings,
        IDialogService dialog)
    {
        _historyService = historyService;
        _deviceRepository = deviceRepo;
        _appSettings = appSettings;
        _dialog = dialog;

        ProductionQuery = new ProductionQueryViewModel(historyService);
        StatusQuery = new StatusQueryViewModel(historyService);
        AlarmQuery = new AlarmQueryViewModel(historyService);
        OeeQuery = new OeeQueryViewModel(historyService, deviceRepo, appSettings);

        RefreshDeviceFilterItems();
        // 班次下拉从配置初始化（所有 Tab 通用），不再依赖产量 Tab 查询结果
        RefreshShiftFilterFromConfig();

        // 跨会话恢复上次查询条件（设备 ID 校验存在性，避免引用已删除的设备）
        RestoreLastQuery();
        if (!string.IsNullOrEmpty(SelectedDeviceId) &&
            !_deviceRepository.Devices.Any(d => d.Id == SelectedDeviceId))
        {
            SelectedDeviceId = null;
        }
        if (string.IsNullOrEmpty(SelectedDeviceId) && _deviceRepository.Devices.Count > 0)
            SelectedDeviceId = _deviceRepository.Devices[0].Id;

        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
    }

    private void OnDevicesCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // UI 线程封送（审查修复 2026-08-13）：Devices 可能被后台线程修改（RemoteRuntimeSink 数据灌入），
        // DeviceFilterItems 是 ObservableCollection——跨线程 Clear/Add 会抛 NotSupportedException。
        // 与 HomeViewModel/OverviewViewModel 的 OnDevicesCollectionChanged 同模式（含关闭守卫）。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            RefreshDeviceFilterItems();
            SelectedDeviceId = DeviceFilterHelper.FallbackSelected(_deviceRepository, SelectedDeviceId);
            return;
        }
        if (dispatcher.CheckAccess())
        {
            RefreshDeviceFilterItems();
            SelectedDeviceId = DeviceFilterHelper.FallbackSelected(_deviceRepository, SelectedDeviceId);
        }
        else
        {
            dispatcher.BeginInvoke(() =>
            {
                RefreshDeviceFilterItems();
                SelectedDeviceId = DeviceFilterHelper.FallbackSelected(_deviceRepository, SelectedDeviceId);
            });
        }
    }

    private string? NormalizeDeviceId() =>
        string.IsNullOrEmpty(SelectedDeviceId) ? null : SelectedDeviceId;

    private string? NormalizeShiftName() =>
        string.IsNullOrEmpty(SelectedShiftName) ? null : SelectedShiftName;

    private string? NormalizeAlarmName() =>
        string.IsNullOrEmpty(SelectedAlarmName) ? null : SelectedAlarmName;

    private int _savedTabIndex;
    private string? _savedDeviceId;
    private DateTime _savedFromDate;
    private DateTime _savedToDate;
    private int _savedQuickTimeIndex;
    private string? _savedShiftName;
    private string? _savedAlarmName;
    private bool _hasSavedState;

    public void SaveLastQuery()
    {
        _savedTabIndex = SelectedTabIndex;
        _savedDeviceId = SelectedDeviceId;
        _savedFromDate = FromDate;
        _savedToDate = ToDate;
        _savedQuickTimeIndex = QuickTimeIndex;
        _savedShiftName = SelectedShiftName;
        _savedAlarmName = SelectedAlarmName;
        _hasSavedState = true;
        PersistLastQuerySnapshot();
    }

    public void RestoreLastQuery()
    {
        // 优先从内存恢复（页面切换场景），其次从磁盘恢复（跨会话场景）
        if (!_hasSavedState)
        {
            TryRestoreFromDisk();
            if (!_hasSavedState) return;
        }

        SelectedTabIndex = _savedTabIndex;
        SelectedDeviceId = _savedDeviceId;
        // 恢复期间禁用 QuickTimeIndex 的反向覆盖逻辑，避免 OnFromDateChanged 重置 QuickTimeIndex
        _isUpdatingQuickTime = true;
        try
        {
            if (_savedQuickTimeIndex > 0)
            {
                // 快捷档位：按当前时间重算区间（守卫已抑制 OnQuickTimeIndexChanged 的回调），
                // 修复恢复后"档位显示近7天、日期停留在默认值"的错位（审查修复 2026-08-13）。
                (FromDate, ToDate) = GetQuickTimeRange(_savedQuickTimeIndex, DateTime.Now);
            }
            else
            {
                FromDate = _savedFromDate;
                ToDate = _savedToDate;
            }
            QuickTimeIndex = _savedQuickTimeIndex;
            SelectedShiftName = _savedShiftName;
            SelectedAlarmName = _savedAlarmName;
        }
        finally
        {
            _isUpdatingQuickTime = false;
        }
    }

    public void PrepareAlarmHistory(string deviceId, string alarmName)
    {
        SelectedTabIndex = 2;
        SelectedDeviceId = deviceId;
        SelectedAlarmName = alarmName;
        QuickTimeIndex = 3;
        HasQueried = true;
        CurrentPage = 1;
        QueryCurrentTab();
    }

    private string LastQueryFilePath => _appSettings.GetFilePath("last_query.json");

    private void PersistLastQuerySnapshot()
    {
        try
        {
            _appSettings.EnsureDirectory();
            var snapshot = new LastQuerySnapshot
            {
                TabIndex = _savedTabIndex,
                DeviceId = _savedDeviceId,
                FromDate = _savedFromDate,
                ToDate = _savedToDate,
                QuickTimeIndex = _savedQuickTimeIndex,
                ShiftName = _savedShiftName,
                AlarmName = _savedAlarmName
            };
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            // 原子写入：先写临时文件再重命名，避免断电产生半截 JSON
            var tempPath = LastQueryFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, LastQueryFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 持久化失败不应阻断查询流程，仅记录日志
            Log.Warning(ex, "持久化上次查询条件失败");
        }
    }

    private void TryRestoreFromDisk()
    {
        try
        {
            if (!File.Exists(LastQueryFilePath)) return;
            var json = File.ReadAllText(LastQueryFilePath);
            var snapshot = JsonSerializer.Deserialize<LastQuerySnapshot>(json);
            if (snapshot == null) return;

            _savedTabIndex = snapshot.TabIndex;
            _savedDeviceId = snapshot.DeviceId;
            _savedFromDate = snapshot.FromDate;
            _savedToDate = snapshot.ToDate;
            _savedQuickTimeIndex = snapshot.QuickTimeIndex;
            _savedShiftName = snapshot.ShiftName;
            _savedAlarmName = snapshot.AlarmName;
            _hasSavedState = true;
        }
        catch (Exception ex)
        {
            // 恢复失败时静默回退到默认值（不影响首次进入查询页的体验）
            Log.Warning(ex, "恢复上次查询条件失败，将使用默认值");
            _hasSavedState = false;
        }
    }

    private (DateTime From, DateTime To) GetShiftRange(DateTime now, int shiftOffset)
    {
        var shifts = _appSettings.Shifts;
        var (currentShift, currentIdx) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (currentShift == null || shifts == null)
            return (FromDate, ToDate);

        int n = shifts.Count;
        int targetIdx = shiftOffset == 0 ? currentIdx : ((currentIdx + shiftOffset) % n + n) % n;
        return shifts[targetIdx].ResolveRange(now);
    }

    private static (DateTime From, DateTime To) GetWeekRange(DateTime now)
    {
        int diff = (7 + (now.DayOfWeek - DayOfWeek.Monday)) % 7;
        var monday = now.Date.AddDays(-diff);
        var sundayEnd = monday.AddDays(7).AddSeconds(-1);
        return (monday, sundayEnd);
    }

    private static (DateTime From, DateTime To) GetMonthRange(DateTime now)
    {
        var firstDay = new DateTime(now.Year, now.Month, 1);
        var lastDay = firstDay.AddMonths(1).AddSeconds(-1);
        return (firstDay, lastDay);
    }

    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    private void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            QueryCurrentTab();
        }
    }

    private bool CanPreviousPage() => CurrentPage > 1;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            QueryCurrentTab();
        }
    }

    private bool CanNextPage() => CurrentPage < TotalPages;

    /// <summary>
    /// 导出按钮可用性：仅在有查询结果且未在导出中时启用。
    /// </summary>
    private bool CanExport() => HasQueried && TotalCount > 0 && !IsExporting;

    [RelayCommand]
    private void Search()
    {
        QueryValidationMessage = string.Empty;
        QueryErrorMessage = string.Empty;
        if (FromDate > ToDate)
        {
            QueryValidationMessage = Strings.M101;
            return;
        }
        CurrentPage = 1;
        HasQueried = true;
        QueryCurrentTab();
        // 查询成功后持久化当前条件，便于下次进入页面或重启应用时恢复
        SaveLastQuery();
    }

    [RelayCommand]
    private void Reset()
    {
        QueryValidationMessage = string.Empty;
        SelectedDeviceId = null;
        SelectedShiftName = null;
        SelectedAlarmName = null;
        QuickTimeIndex = -1;
        FromDate = DateTime.Today.AddDays(-1);
        ToDate = DateTime.Today.AddDays(1).AddSeconds(-1);
        HasQueried = false;
        QueryErrorMessage = string.Empty;
        TotalCount = 0; TotalPages = 0;
        ProductionQuery.Reset();
        StatusQuery.Reset();
        AlarmQuery.Reset();
        OeeQuery.Reset();
    }

    [RelayCommand]
    private void QueryCurrentTab()
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            _ = QueryCurrentTabAsync();
            return;
        }

        QueryCurrentTabSync();
    }

    private void QueryCurrentTabSync()
    {
        QueryErrorMessage = string.Empty;
        if (CurrentPage < 1) CurrentPage = 1;
        switch (SelectedTabIndex)
        {
            case 0: QueryProduction(); break;
            case 1: QueryStatus(); break;
            case 2: QueryAlarm(); break;
            case 3: QueryOee(); break;
        }
    }

    private sealed record QueryRequest(
        int TabIndex,
        string? DeviceId,
        DateTime From,
        DateTime To,
        string? ShiftName,
        string? AlarmName,
        int Page,
        int PageSize);

    private sealed record QueryResult(int TotalCount, int TotalPages, object ViewModel, string? Error);

    private async Task QueryCurrentTabAsync()
    {
        if (IsLoading)
        {
            _queryPending = true;
            return;
        }

        do
        {
            _queryPending = false;
            await QueryCurrentTabOnceAsync();
        }
        while (_queryPending);
    }

    private async Task QueryCurrentTabOnceAsync()
    {
        var requestVersion = ++_queryVersion;
        var request = new QueryRequest(
            SelectedTabIndex,
            NormalizeDeviceId(),
            FromDate,
            ToDate,
            NormalizeShiftName(),
            NormalizeAlarmName(),
            CurrentPage,
            PageSize);

        IsLoading = true;
        QueryErrorMessage = string.Empty;
        try
        {
            var result = await Task.Run(() => ExecuteQueryInBackground(request));
            if (requestVersion != _queryVersion) return;
            ApplyQueryResult(result);
        }
        catch (Exception ex)
        {
            if (requestVersion != _queryVersion) return;
            QueryErrorMessage = string.Format(Strings.F077, ex.Message);
            _dialog.NotifyError(QueryErrorMessage);
        }
        finally
        {
            if (requestVersion == _queryVersion)
                IsLoading = false;
        }
    }

    private QueryResult ExecuteQueryInBackground(QueryRequest request)
    {
        return request.TabIndex switch
        {
            0 => CreateProductionResult(request),
            1 => CreateStatusResult(request),
            2 => CreateAlarmResult(request),
            3 => CreateOeeResult(request),
            _ => throw new InvalidOperationException(string.Format(Strings.F144, request.TabIndex)),
        };
    }

    private QueryResult CreateProductionResult(QueryRequest request)
    {
        var vm = new ProductionQueryViewModel(_historyService);
        var (count, pages) = vm.Query(request.DeviceId, request.From, request.To, request.ShiftName, request.Page, request.PageSize);
        return new QueryResult(count, pages, vm, vm.QueryError);
    }

    private QueryResult CreateStatusResult(QueryRequest request)
    {
        var vm = new StatusQueryViewModel(_historyService);
        var (count, pages) = vm.Query(request.DeviceId ?? string.Empty, request.From, request.To, request.ShiftName, request.Page, request.PageSize);
        return new QueryResult(count, pages, vm, vm.QueryError);
    }

    private QueryResult CreateAlarmResult(QueryRequest request)
    {
        var vm = new AlarmQueryViewModel(_historyService);
        var (count, pages) = vm.Query(request.DeviceId, request.From, request.To, request.ShiftName, request.AlarmName, request.Page, request.PageSize);
        return new QueryResult(count, pages, vm, vm.QueryError);
    }

    private QueryResult CreateOeeResult(QueryRequest request)
    {
        var vm = new OeeQueryViewModel(_historyService, _deviceRepository, _appSettings);
        var (count, pages) = vm.Query(request.DeviceId, request.From, request.To, request.ShiftName);
        return new QueryResult(count, pages, vm, vm.QueryError);
    }

    private void ApplyQueryResult(QueryResult result)
    {
        TotalCount = result.Error == null ? result.TotalCount : 0;
        TotalPages = result.Error == null ? result.TotalPages : 0;
        QueryErrorMessage = result.Error ?? string.Empty;

        switch (result.ViewModel)
        {
            case ProductionQueryViewModel production:
                ProductionQuery = production;
                OnPropertyChanged(nameof(ProductionQuery));
                if (result.Error == null && result.TotalCount > 0)
                    RefreshShiftFilterItems(production.LastQueryShiftNames);
                break;
            case StatusQueryViewModel status:
                StatusQuery = status;
                OnPropertyChanged(nameof(StatusQuery));
                break;
            case AlarmQueryViewModel alarm:
                AlarmQuery = alarm;
                OnPropertyChanged(nameof(AlarmQuery));
                if (result.Error == null && alarm.LastQueryAlarmNames.Count > 0)
                    RefreshAlarmNameFilterItems(alarm.LastQueryAlarmNames);
                break;
            case OeeQueryViewModel oee:
                OeeQuery = oee;
                OnPropertyChanged(nameof(OeeQuery));
                break;
        }
    }

    private void QueryProduction()
        => ExecuteQuery(() =>
        {
            var (totalCount, totalPages) = ProductionQuery.Query(
                NormalizeDeviceId(), FromDate, ToDate, NormalizeShiftName(), CurrentPage, PageSize);
            TotalCount = totalCount;
            TotalPages = totalPages;
            QueryErrorMessage = ProductionQuery.QueryError ?? string.Empty;
            // 产量查询后，按本批数据中的实际班次刷新下拉（覆盖配置初始值），
            // 使下拉可筛选数据中出现的、但配置未显式定义的班次（如临时班次）。
            // 仅在存在数据时刷新：无数据时保留配置初始的班次项，避免清为仅“全部班次”。
            if (totalCount > 0)
                RefreshShiftFilterItems(ProductionQuery.LastQueryShiftNames);
        });

    private void QueryStatus()
        => ExecuteQuery(() =>
        {
            var (totalCount, totalPages) = StatusQuery.Query(
                NormalizeDeviceId(), FromDate, ToDate, NormalizeShiftName(), CurrentPage, PageSize);
            TotalCount = totalCount;
            TotalPages = totalPages;
            QueryErrorMessage = StatusQuery.QueryError ?? string.Empty;
        });

    private void QueryAlarm()
        => ExecuteQuery(() =>
        {
            var (totalCount, totalPages) = AlarmQuery.Query(
                NormalizeDeviceId(), FromDate, ToDate, NormalizeShiftName(), NormalizeAlarmName(),
                CurrentPage, PageSize);
            TotalCount = totalCount;
            TotalPages = totalPages;
            QueryErrorMessage = AlarmQuery.QueryError ?? string.Empty;
            if (AlarmQuery.LastQueryAlarmNames.Count > 0)
                RefreshAlarmNameFilterItems(AlarmQuery.LastQueryAlarmNames);
        });

    private void QueryOee()
        => ExecuteQuery(() =>
        {
            var (totalCount, _) = OeeQuery.Query(
                NormalizeDeviceId(), FromDate, ToDate, NormalizeShiftName());
            TotalCount = totalCount;
            TotalPages = totalCount > 0 ? 1 : 0;
            QueryErrorMessage = OeeQuery.QueryError ?? string.Empty;
        });

    /// <summary>
    /// 查询模板：统一处理 IsLoading 状态、异常捕获和 Growl 提示。
    /// </summary>
    private void ExecuteQuery(Action queryAction)
    {
        IsLoading = true;
        QueryErrorMessage = string.Empty;
        try
        {
            queryAction();
        }
        catch (Exception ex)
        {
            QueryErrorMessage = string.Format(Strings.F077, ex.Message);
            _dialog.NotifyError(string.Format(Strings.F154, ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (IsExporting) return; // 防重复点击

        // IsExporting 前置（审查修复 2026-08-13）：原置位于 CSV 构建之后，构建期间的
        // 双击窗口可并发触发两次构建；且 BuildCsv 在 UI 线程同步执行，10 万行大表卡 UI 数秒
        IsExporting = true;
        ExportCommand.NotifyCanExecuteChanged();
        try
        {
            var tabIndex = SelectedTabIndex;
            var from = FromDate; var to = ToDate; var deviceId = SelectedDeviceId;
            var (fileName, csv) = await Task.Run<(string?, string?)>(() => tabIndex switch
            {
                0 => (string.Format(Strings.F327, from, to), ProductionQuery.BuildCsv(from, to)),
                1 => (string.Format(Strings.F328, from, to), StatusQuery.BuildCsv()),
                2 => (string.Format(Strings.F329, from, to), AlarmQuery.BuildCsv()),
                3 => ($"OEE_{from:yyyyMMdd}_{to:yyyyMMdd}.csv", OeeQuery.BuildCsv(deviceId)),
                _ => (null, null)
            }).ConfigureAwait(true);

            if (fileName == null || string.IsNullOrEmpty(csv))
            {
                _dialog.NotifyInfo(Strings.M011);
                Log.Debug("导出取消：Tab={TabIndex}，无数据", tabIndex);
                return;
            }

            // 明确标注当前页导出，避免大表被误解为全量导出（审查修复 2026-08-15）
            fileName = $"{Path.GetFileNameWithoutExtension(fileName)}_page{CurrentPage}{Path.GetExtension(fileName)}";
            csv += $"\n# 本文件仅包含当前页数据（每页最多 {PageSize} 条），并非全量导出。";

            // 异步执行 CSV 生成与文件写入，避免大表（10万行+）阻塞 UI 线程
            var exportDir = Path.Combine(AppSettings.DataRoot, _appSettings.ConfigDirectory, "Exports");
            Directory.CreateDirectory(exportDir);
            var fullPath = Path.Combine(exportDir, fileName);

            await Task.Run(() =>
            {
                File.WriteAllText(fullPath, csv!, new UTF8Encoding(true));
            }).ConfigureAwait(true);

            Log.Information("已导出当前页 {CurrentPage} → {Path}", CurrentPage, fullPath);
            AuditLog.Record("Export.Csv", "Export", Path.GetFileName(fullPath), detail: $"当前页导出 Tab={tabIndex} Page={CurrentPage}");
            _dialog.NotifySuccess($"已导出当前页数据 → {fullPath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出失败");
            _dialog.NotifyError(string.Format(Strings.F090, ex.Message));
        }
        finally
        {
            IsExporting = false;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 解绑外部事件订阅，防止 ViewModel 被 DI 容器释放后仍持有
    /// DeviceRepository.Devices.CollectionChanged 的事件引用，
    /// 避免因事件未解绑导致的内存泄漏与僵尸回调。
    /// </summary>
    public void Dispose()
    {
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
    }
}
