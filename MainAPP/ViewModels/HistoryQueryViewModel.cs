using System.Windows;
using NodaTime;
using System.Collections.ObjectModel;
using MainAPP.Resources;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Helpers;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
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

/// <summary>单个历史查询页签的分页计数。不含结果行，行数据在对应子 ViewModel 里。</summary>
internal sealed class TabSnapshot
{
    public int CurrentPage = 1;
    public int TotalPages;
    public int TotalCount;
    public bool HasQueried;
    public string QueryError = string.Empty;
    public string? Signature;

    public void Clear()
    {
        CurrentPage = 1;
        TotalPages = 0;
        TotalCount = 0;
        HasQueried = false;
        QueryError = string.Empty;
        Signature = null;
    }
}

public partial class HistoryQueryViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryService _historyService;
    private readonly DeviceRepository _deviceRepository;
    private readonly IDialogService _dialog;
    private readonly AppSettings _appSettings;
    private readonly IAssistantQuestionTally? _questions;

    public ProductionQueryViewModel ProductionQuery { get; private set; }
    public StatusQueryViewModel StatusQuery { get; private set; }
    public AlarmQueryViewModel AlarmQuery { get; private set; }
    public OeeQueryViewModel OeeQuery { get; private set; }
    public SnQueryViewModel SnQuery { get; private set; }

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

    /// <summary>历史查询页统一的页面内操作反馈。</summary>
    public OperationFeedback Feedback { get; } = new();

    /// <summary>
    /// 导出进行中标志：绑定到导出按钮 IsEnabled=false + 显示 LoadingCircle，避免大表导出 UI 假死与重复点击。
    /// </summary>
    [ObservableProperty]
    private bool _isExporting;

    private bool _queryPending;
    private int _queryVersion;

    /// <summary>正在把某个页签的缓存计数写回界面，避免属性回调把别的页签的页码写进当前快照。</summary>
    private bool _applyingSnapshot;

    private const int TabCount = 5;
    private const int SnTabIndex = 4;

    /// <summary>每个页签各自的计数与页码。结果数据留在对应子 ViewModel 的内存缓存里，切页签不再重查。</summary>
    private readonly TabSnapshot[] _snapshots = Enumerable.Range(0, TabCount).Select(_ => new TabSnapshot()).ToArray();

    public bool IsEmptyResult => HasQueried && TotalCount == 0 && !HasQueryError;

    /// <summary>产量/状态/报警/OEE 共用顶部筛选；SN 追溯在页签内自己查询。</summary>
    public bool IsSharedFilterVisible => SelectedTabIndex != SnTabIndex;

    /// <summary>当前查询结果摘要，显示在筛选栏标题区。SN 页签不使用这套计数。</summary>
    public string QuerySummaryText => SelectedTabIndex == SnTabIndex
        ? string.Empty
        : !HasQueried
            ? Strings.Msg_Queried
            : HasQueryError
                ? Strings.Msg_QueryFailed
                : TotalCount == 0
                    ? Strings.Msg_NoDataFound
                    : string.Format(Strings.Prompt_TotalsUseAllRowsPage, TotalCount, CurrentPage, TotalPages);

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
        if (!_applyingSnapshot)
            _snapshots[SelectedTabIndex].CurrentPage = value;
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
        RefreshAlarmNameFilterFromDevices();
    }

    /// <summary>产量/状态/报警用历史说明，OEE 页签换成 OEE 说明。</summary>
    public string PageHelpKey => SelectedTabIndex == 3 ? PageHelpContent.HistoryOee : PageHelpContent.History;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PageHelpKey));
        OnPropertyChanged(nameof(EmptyStateCode));
        OnPropertyChanged(nameof(IsSharedFilterVisible));
        OnPropertyChanged(nameof(QuerySummaryText));
        if ((uint)value >= TabCount) return;
        var snap = _snapshots[value];
        // 筛选已经变过的页签不再把旧结果当成当前条件的答案。
        if (snap.HasQueried && snap.Signature != CurrentFilterSignature())
        {
            snap.Clear();
            ResetTabResults(value);
        }
        ApplySnapshotToView(snap);
    }

    /// <summary>用户改日期时把快捷档位收回“自定义”。查询要等点击查询，改日期本身不查。</summary>
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
    [NotifyPropertyChangedFor(nameof(IsLastHourPreset))]
    [NotifyPropertyChangedFor(nameof(IsLast4HoursPreset))]
    [NotifyPropertyChangedFor(nameof(IsLast24HoursPreset))]
    private int _quickTimeIndex = -1;

    public bool IsLastHourPreset
    {
        get => QuickTimeIndex == 9;
        set { if (value) QuickTimeIndex = 9; }
    }

    public bool IsLast4HoursPreset
    {
        get => QuickTimeIndex == 10;
        set { if (value) QuickTimeIndex = 10; }
    }

    public bool IsLast24HoursPreset
    {
        get => QuickTimeIndex == 11;
        set { if (value) QuickTimeIndex = 11; }
    }

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
            // 含今天在内的 7 / 30 个日历日。从今天往前减 7 会落成 8 个日期。
            3 => (now.Date.AddDays(-6), now.Date.AddDays(1).AddSeconds(-1)),
            4 => (now.Date.AddDays(-29), now.Date.AddDays(1).AddSeconds(-1)),
            5 => GetShiftRange(now, 0),
            6 => GetShiftRange(now, -1),
            7 => GetWeekRange(now),
            8 => GetMonthRange(now),
            9 => (now.AddHours(-1), now),
            10 => (now.AddHours(-4), now),
            11 => (now.AddHours(-24), now),
            _ => (FromDate, ToDate)
        };
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
        // P0-1 修复 2026-09-02：经锁内快照读取班次，禁止直接枚举可变集合
        var names = _appSettings.GetShiftsSnapshot()
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        ShiftFilterItems.Clear();
        ShiftFilterItems.Add(new FilterOption(null, Strings.Msg_AllShifts));
        foreach (var n in names)
            ShiftFilterItems.Add(new FilterOption(n, n));
    }

    public ObservableCollection<FilterOption> AlarmNameFilterItems { get; } = new();

    /// <summary>
    /// 报警名称下拉来自设备配置（位报警 + 计数报警），不随本次查询结果收缩。
    /// 未选设备时列出全部设备的报警名。
    /// </summary>
    private void RefreshAlarmNameFilterFromDevices()
    {
        var selected = SelectedAlarmName;
        var names = new SortedDictionary<string, string>(StringComparer.CurrentCulture);
        IEnumerable<Device> devices = _deviceRepository.Devices;
        if (!string.IsNullOrEmpty(SelectedDeviceId))
            devices = devices.Where(d => d.Id == SelectedDeviceId);

        foreach (var device in devices)
        {
            foreach (var alarm in device.Alarms)
                AddAlarmName(names, alarm.Name, AlarmNameLocalizer.Resolve(alarm));
            foreach (var counter in device.CounterAlarms)
                AddAlarmName(names, counter.Name, AlarmNameLocalizer.Resolve(counter));
        }

        AlarmNameFilterItems.Clear();
        AlarmNameFilterItems.Add(new FilterOption(null, Strings.Web_Hq_AllAlarms));
        foreach (var pair in names)
            AlarmNameFilterItems.Add(new FilterOption(pair.Key, pair.Value));
        // Clear 会让下拉框把 SelectedValue 写回 null，补回原选项。
        if (!string.Equals(SelectedAlarmName, selected, StringComparison.Ordinal))
            SelectedAlarmName = selected;

        static void AddAlarmName(SortedDictionary<string, string> target, string? name, string display)
        {
            if (string.IsNullOrWhiteSpace(name) || target.ContainsKey(name)) return;
            target[name] = string.IsNullOrWhiteSpace(display) ? name : display;
        }
    }

    private AssistantHistoryFacts CaptureAssistantFacts() => new(FromDate, ToDate);

    [ObservableProperty]
    private string _repeatedQuestionNote = "";

    public bool HasRepeatedQuestionNote => !string.IsNullOrWhiteSpace(RepeatedQuestionNote);

    partial void OnRepeatedQuestionNoteChanged(string value) => OnPropertyChanged(nameof(HasRepeatedQuestionNote));

    public void RefreshRepeatedQuestions() => RepeatedQuestionNote = string.Join(Environment.NewLine, RepeatedNotes());

    private IEnumerable<string> RepeatedNotes()
    {
        if (_questions == null)
            yield break;
        foreach (var question in _questions.Repeated())
            yield return string.Format(Strings.Assistant_RepeatedNote, question);
    }

    public HistoryQueryViewModel(
        IHistoryService historyService,
        DeviceRepository deviceRepo,
        AppSettings appSettings,
        IDialogService dialog,
        ISnEventStore? snEventStore = null,
        AssistantContextStore? assistantContext = null,
        IAssistantQuestionTally? questions = null)
    {
        _historyService = historyService;
        _deviceRepository = deviceRepo;
        _appSettings = appSettings;
        _dialog = dialog;
        _questions = questions;

        ProductionQuery = new ProductionQueryViewModel(historyService);
        StatusQuery = new StatusQueryViewModel(historyService);
        AlarmQuery = new AlarmQueryViewModel(historyService);
        OeeQuery = new OeeQueryViewModel(historyService, deviceRepo, appSettings);
        SnQuery = new SnQueryViewModel(snEventStore);
        assistantContext?.BindHistory(CaptureAssistantFacts);

        RefreshDeviceFilterItems();
        // 班次下拉只来自配置，查询结果不再改写它
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
        RefreshAlarmNameFilterFromDevices();

        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
    }

    private void OnDevicesCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // UI 线程封送（审查修复 2026-08-13）：Devices 可能被后台线程修改（RemoteRuntimeSink 数据灌入），
        // DeviceFilterItems 是 ObservableCollection——跨线程 Clear/Add 会抛 NotSupportedException。
        // 与 HomeViewModel/OverviewViewModel 的 OnDevicesCollectionChanged 同模式（含关闭守卫）。
        UiDispatcher.Dispatch(() =>
        {
            RefreshDeviceFilterItems();
            SelectedDeviceId = DeviceFilterHelper.FallbackSelected(_deviceRepository, SelectedDeviceId);
            RefreshAlarmNameFilterFromDevices();
        });
    }

    private string CurrentFilterSignature()
        => FilterSignature(NormalizeDeviceId(), FromDate, ToDate, NormalizeShiftName(), NormalizeAlarmName());

    private static string FilterSignature(QueryRequest request)
        => FilterSignature(request.DeviceId, request.From, request.To, request.ShiftName, request.AlarmName);

    private static string FilterSignature(string? deviceId, DateTime from, DateTime to, string? shiftName, string? alarmName)
        => string.Join('\u001f', deviceId ?? "", from.Ticks.ToString(CultureInfo.InvariantCulture), to.Ticks.ToString(CultureInfo.InvariantCulture), shiftName ?? "", alarmName ?? "");

    private void ResetTabResults(int tabIndex)
    {
        switch (tabIndex)
        {
            case 0: ProductionQuery.Reset(); break;
            case 1: StatusQuery.Reset(); break;
            case 2: AlarmQuery.Reset(); break;
            case 3: OeeQuery.Reset(); break;
            case SnTabIndex: SnQuery.Reset(); break;
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

    public async Task SaveLastQueryAsync()
    {
        _savedTabIndex = SelectedTabIndex;
        _savedDeviceId = SelectedDeviceId;
        _savedFromDate = FromDate;
        _savedToDate = ToDate;
        _savedQuickTimeIndex = QuickTimeIndex;
        _savedShiftName = SelectedShiftName;
        _savedAlarmName = SelectedAlarmName;
        _hasSavedState = true;
        await PersistLastQuerySnapshotAsync();
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
        _snapshots[2].CurrentPage = 1;
        CurrentPage = 1;
        _ = QueryCurrentTab();
    }

    private string LastQueryFilePath => _appSettings.GetFilePath("last_query.json");

    /// <summary>
    /// 持久化上次查询条件。审查修复 2026-09-02（P1-8）：文件 IO 移出 UI 线程
    /// （查询成功后每次都会触发，高频路径不得同步写盘）。
    /// </summary>
    private async Task PersistLastQuerySnapshotAsync()
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
            await Task.Run(() =>
            {
                var tempPath = LastQueryFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, LastQueryFilePath, overwrite: true);
            });
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
            // 构造函数内直接同步读盘（文件为几百字节的查询条件快照，读取代价可忽略；
            // 审查修复 2026-09-17：此前用 Task.Run+GetResult 做 sync-over-async，
            // 非但没减少阻塞，反而额外引入线程切换与死锁风险）。
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
        var shifts = _appSettings.GetShiftsSnapshot(); // P0-1 修复 2026-09-02
        var (currentShift, currentIdx) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (currentShift == null)
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
            PageCurrentTab();
        }
    }

    private bool CanPreviousPage() => CurrentPage > 1;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            PageCurrentTab();
        }
    }

    private bool CanNextPage() => CurrentPage < TotalPages;

    /// <summary>分页控件跳页：只切当前页签的内存缓存，不重新查询。</summary>
    public void GoToPage(int page)
    {
        if (SelectedTabIndex is 3 or SnTabIndex) return;
        if (page < 1) page = 1;
        if (TotalPages > 0 && page > TotalPages) page = TotalPages;
        if (CurrentPage != page)
            CurrentPage = page;
        PageCurrentTab();
    }

    /// <summary>
    /// 翻页：仅对当前 Tab 的已缓存全量结果做内存分页，不重新查询（KPI/图表不变，避免每次翻页全量重查）。
    /// OEE Tab 不分页，无需处理。
    /// </summary>
    private void PageCurrentTab()
    {
        switch (SelectedTabIndex)
        {
            case 0: ProductionQuery.Page(CurrentPage, PageSize); break;
            case 1: StatusQuery.Page(CurrentPage, PageSize); break;
            case 2: AlarmQuery.Page(CurrentPage, PageSize); break;
            case 3: break; // OEE 单页
            case SnTabIndex: break; // SN 追溯自包含查询，不走公共分页
        }
    }

    /// <summary>把指定页签的计数写回界面属性，并按该页码重切内存分页。</summary>
    private void ApplySnapshotToView(TabSnapshot snap)
    {
        _applyingSnapshot = true;
        try
        {
            QueryErrorMessage = snap.QueryError;
            TotalCount = snap.TotalCount;
            TotalPages = snap.TotalPages;
            HasQueried = snap.HasQueried;
            CurrentPage = snap.CurrentPage < 1 ? 1 : snap.CurrentPage;
        }
        finally
        {
            _applyingSnapshot = false;
        }

        if (snap.HasQueried && string.IsNullOrEmpty(snap.QueryError))
            PageCurrentTab();
    }

    /// <summary>
    /// 导出按钮可用性：仅在有查询结果且未在导出中时启用。
    /// </summary>
    private bool CanExport() => HasQueried && TotalCount > 0 && !IsExporting;

    [RelayCommand]
    private async Task Search()
    {
        QueryValidationMessage = string.Empty;
        QueryErrorMessage = string.Empty;
        if ((uint)SelectedTabIndex < TabCount)
            _snapshots[SelectedTabIndex].QueryError = string.Empty;
        // SN 页签使用页内查询，顶部筛选和 Ctrl+Enter 不作用在它上面。
        if (SelectedTabIndex == SnTabIndex)
            return;
        if (FromDate > ToDate)
        {
            QueryValidationMessage = Strings.Msg_StartCannotEnd;
            return;
        }
        _snapshots[SelectedTabIndex].CurrentPage = 1;
        CurrentPage = 1;
        await QueryCurrentTab();
        // 查询成功后持久化当前条件，便于下次进入页面或重启应用时恢复
        await SaveLastQueryAsync(); // P1-8 修复 2026-09-02：IO 移出 UI 线程
    }

    [RelayCommand]
    private void Reset()
    {
        QueryValidationMessage = string.Empty;
        QueryErrorMessage = string.Empty;
        SelectedShiftName = null;
        SelectedAlarmName = null;
        if (string.IsNullOrEmpty(SelectedDeviceId) && _deviceRepository.Devices.Count > 0)
            SelectedDeviceId = _deviceRepository.Devices[0].Id;
        _isUpdatingQuickTime = true;
        try
        {
            QuickTimeIndex = 0;
            FromDate = DateTime.Today.AddDays(-1);
            ToDate = DateTime.Today.AddDays(1).AddSeconds(-1);
        }
        finally
        {
            _isUpdatingQuickTime = false;
        }
        foreach (var snap in _snapshots)
            snap.Clear();
        HasQueried = false;
        TotalCount = 0;
        TotalPages = 0;
        CurrentPage = 1;
        ProductionQuery.Reset();
        StatusQuery.Reset();
        AlarmQuery.Reset();
        OeeQuery.Reset();
        SnQuery.Reset();
        Feedback.Success(Strings.Ux_ResetQuery);
    }

    [RelayCommand]
    private void ApplyQuickTimePreset(int index) => QuickTimeIndex = index;

    /// <summary>
    /// 查询当前页签。UI 线程把计算放到后台；没有 WPF 调度器时（单元测试）同一套逻辑在当前线程跑完，
    /// 调用方可以在命令返回后直接读结果。
    /// </summary>
    [RelayCommand]
    private Task QueryCurrentTab()
    {
        if (SelectedTabIndex == SnTabIndex)
            return Task.CompletedTask;
        return QueryCurrentTabAsync();
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
        Feedback.Working(Strings.Ux_Querying);
        QueryErrorMessage = string.Empty;
        try
        {
            var result = await ExecuteQueryOffUiThread(request);
            if (requestVersion != _queryVersion) return;
            ApplyQueryResult(request.TabIndex, result, FilterSignature(request));
            if (request.TabIndex != SnTabIndex)
                AuditHistoryLookup(request, result.Error == null, result.TotalCount, result.Error);
        }
        catch (Exception ex)
        {
            if (requestVersion != _queryVersion) return;
            var message = string.Format(Strings.Prompt_HistoryQueryFailed, ex.Message);
            var snap = _snapshots[request.TabIndex];
            snap.HasQueried = true;
            snap.Signature = FilterSignature(request);
            snap.QueryError = message;
            snap.TotalCount = 0;
            snap.TotalPages = 0;
            if (request.TabIndex == SelectedTabIndex)
            {
                ApplySnapshotToView(snap);
                Feedback.Error(message);
                _dialog.NotifyError(message);
            }
            AuditHistoryLookup(request, false, 0, message);
        }
        finally
        {
            if (requestVersion == _queryVersion)
                IsLoading = false;
        }
    }

    /// <summary>UI 线程上丢到线程池；测试线程上同步执行，避免测试在命令返回前读不到结果。</summary>
    private Task<QueryResult> ExecuteQueryOffUiThread(QueryRequest request)
    {
        if (!UiDispatcher.IsOnLiveUiThread)
            return Task.FromResult(ExecuteQueryInBackground(request));
        return Task.Run(() => ExecuteQueryInBackground(request));
    }

    private QueryResult ExecuteQueryInBackground(QueryRequest request)
    {
        return request.TabIndex switch
        {
            0 => CreateProductionResult(request),
            1 => CreateStatusResult(request),
            2 => CreateAlarmResult(request),
            3 => CreateOeeResult(request),
            4 => CreateSnResult(),
            _ => throw new InvalidOperationException(string.Format(Strings.Prompt_UnknownQueryTab, request.TabIndex)),
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
        var (count, pages) = vm.Query(request.DeviceId, request.From, request.To, request.ShiftName, request.Page, request.PageSize);
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

    /// <summary>SN 追溯 Tab：结果由 SnQueryViewModel 自包含管理，公共管线仅透传（无数据计数）。</summary>
    private QueryResult CreateSnResult()
        => new(0, 0, SnQuery, null);

    private void ApplyQueryResult(int tabIndex, QueryResult result, string signature)
    {
        var snap = _snapshots[tabIndex];
        snap.HasQueried = true;
        snap.Signature = signature;
        snap.QueryError = result.Error ?? string.Empty;
        snap.TotalCount = result.Error == null ? result.TotalCount : 0;
        snap.TotalPages = result.Error == null ? result.TotalPages : 0;

        switch (result.ViewModel)
        {
            case ProductionQueryViewModel production:
                ProductionQuery = production;
                OnPropertyChanged(nameof(ProductionQuery));
                break;
            case StatusQueryViewModel status:
                StatusQuery = status;
                OnPropertyChanged(nameof(StatusQuery));
                break;
            case AlarmQueryViewModel alarm:
                AlarmQuery = alarm;
                OnPropertyChanged(nameof(AlarmQuery));
                break;
            case OeeQueryViewModel oee:
                OeeQuery = oee;
                OnPropertyChanged(nameof(OeeQuery));
                break;
        }

        if (tabIndex != SelectedTabIndex) return;

        ApplySnapshotToView(snap);
        if (result.Error == null)
            Feedback.Success(string.Format(Strings.Ux_QueryFinished, result.TotalCount));
        else
            Feedback.Error(result.Error);
    }

    private void AuditHistoryLookup(QueryRequest request, bool succeeded, int count, string? error)
    {
        var device = DeviceLabel(request.DeviceId);
        var detail = string.Format(
            Strings.Audit_Detail_HistoryQuery,
            request.TabIndex,
            device,
            request.From.ToString("yyyy-MM-dd HH:mm"),
            request.To.ToString("yyyy-MM-dd HH:mm"),
            request.Page,
            count);
        if (!succeeded && !string.IsNullOrWhiteSpace(error))
            detail = detail + " " + error;
        if (detail.Length > 400)
            detail = detail[..400];
        AuditLog.Record("History.Query", "History", device, succeeded, detail);
    }

    private string DeviceLabel(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return "";
        return _deviceRepository.Devices.FirstOrDefault(device => device.Id == deviceId)?.Name ?? deviceId;
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

            var exportChoice = _dialog.Show(
                Strings.Msg_ExportAllFilteredResultsYesExports, Strings.Msg_ExportScope, System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);
            if (exportChoice == System.Windows.MessageBoxResult.Cancel)
                return;
            var exportAll = exportChoice == System.Windows.MessageBoxResult.Yes;

            var (fileName, csv) = await Task.Run<(string?, string?)>(() =>
            {
                if (exportAll)
                {
                    return tabIndex switch
                    {
                        0 => (string.Format(Strings.Prompt_OutputCsv, from, to), ProductionQuery.BuildCsvAll(from, to)),
                        1 => (string.Format(Strings.Prompt_StateDurationCsv, from, to), StatusQuery.BuildCsvAll()),
                        2 => (string.Format(Strings.Prompt_AlarmCsv, from, to), AlarmQuery.BuildCsvAll()),
                        3 => ($"OEE_{from:yyyyMMdd}_{to:yyyyMMdd}.csv", OeeQuery.BuildCsv(deviceId)),
                        _ => (null, null)
                    };
                }
                return tabIndex switch
                {
                    0 => (string.Format(Strings.Prompt_OutputCsv, from, to), ProductionQuery.BuildCsv(from, to)),
                    1 => (string.Format(Strings.Prompt_StateDurationCsv, from, to), StatusQuery.BuildCsv()),
                    2 => (string.Format(Strings.Prompt_AlarmCsv, from, to), AlarmQuery.BuildCsv()),
                    3 => ($"OEE_{from:yyyyMMdd}_{to:yyyyMMdd}.csv", OeeQuery.BuildCsv(deviceId)),
                    _ => (null, null)
                };
            }).ConfigureAwait(true);

            if (fileName == null || string.IsNullOrEmpty(csv))
            {
                _dialog.NotifyInfo(Strings.Msg_NoDataCurrentTabExport);
                Log.Debug("导出取消：Tab={TabIndex}，无数据", tabIndex);
                return;
            }

            if (!exportAll)
            {
                // 明确标注当前页导出，避免大表被误解为全量导出（审查修复 2026-08-15）
                fileName = $"{Path.GetFileNameWithoutExtension(fileName)}_page{CurrentPage}{Path.GetExtension(fileName)}";
                csv += "\n# " + string.Format(Strings.Csv_Export_PageHint, PageSize);
            }
            else
            {
                csv += "\n# " + string.Format(Strings.Msg_AllFilteredResultsRows, TotalCount);
            }

            // 异步执行 CSV 生成与文件写入，避免大表（10万行+）阻塞 UI 线程
            var exportDir = Path.Combine(AppSettings.DataRoot, _appSettings.ConfigDirectory, "Exports");
            Directory.CreateDirectory(exportDir);
            var safeFileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName != fileName)
                throw new InvalidDataException("导出文件名无效");
            var fullPath = Path.GetFullPath(Path.Combine(exportDir, safeFileName));
            var exportRoot = Path.GetFullPath(exportDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(exportRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("导出路径越界");

            await Task.Run(() =>
            {
                File.WriteAllText(fullPath, csv!, new UTF8Encoding(true));
            }).ConfigureAwait(true);

            Log.Information("已导出当前页 {CurrentPage} → {Path}", CurrentPage, fullPath);
            AuditLog.Record("Export.Csv", "Export", Path.GetFileName(fullPath), detail: string.Format(Strings.Audit_Detail_PageExport, tabIndex, CurrentPage));
            _dialog.NotifySuccess(string.Format(Strings.Msg_PageExported, fullPath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出失败");
            _dialog.NotifyError(string.Format(Strings.Prompt_ExportFailed, ex.Message));
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
