using System.Collections.ObjectModel;
using MainAPP.Resources;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Helpers;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Serilog;

namespace MainAPP.ViewModels;

/// <summary>
/// 报警中心时间范围枚举（事件流查询窗口）。
/// </summary>
public enum AlarmCenterTimeRange
{
    Hour1,
    Hours4,
    Hours24,
}

/// <summary>
/// Top 报警排行项（报警中心页底部展示）。
/// </summary>
public class AlarmTopItem
{
    public string AlarmName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int TriggerCount { get; set; }
    public AlarmLevel Level { get; set; }
    /// <summary>排名序号（1-based，由 ViewModel 填充）</summary>
    public int Rank { get; set; }
}

/// <summary>事件流列表项：附带从设备配置解析的多语言显示名。</summary>
public sealed class AlarmCenterEventItem
{
    public AlarmEventRecord Record { get; }
    public string DisplayName { get; }
    public AlarmEventType EventType => Record.EventType;
    public string DeviceName => Record.DeviceName;
    public DateTime EventTime => Record.EventTime;

    public AlarmCenterEventItem(AlarmEventRecord record, string displayName)
    {
        Record = record;
        DisplayName = displayName;
    }
}

/// <summary>
/// 报警中心 ViewModel：实时活跃报警墙 + 最近 N 小时事件流 + Top 报警排行 + KPI 汇总。
/// 数据来源：DeviceRepository.Runtimes（实时活跃报警）+ HistoryService.QueryAlarmEvents（事件流与统计）。
/// 不修改数据库 schema，纯只读监控视图。
/// </summary>
public partial class AlarmCenterViewModel : ObservableObject, IDisposable
{
    private readonly IAlarmHistoryService _historyService;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDialogService _dialog;
    private readonly DispatcherTimer _activeTimer;  // 3s 刷新活跃报警
    private readonly DispatcherTimer _statsTimer;   // 60s 刷新事件流与统计
    private readonly DispatcherTimer _searchDebounceTimer;  // 搜索输入防抖（350ms）
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private int _statsRefreshVersion;
    private int _activeRefreshVersion;
    private bool _disposed;
    private int _unfilteredActiveCount;
    private List<AlarmEventRecord> _pendingDataSourceEvents = new();

    private const int MaxActiveAlarms = 200;       // 活跃报警列表上限（避免极端情况内存膨胀）
    private const int MaxRecentEvents = 500;       // 事件流展示上限
    private const int TopAlarmsCount = 10;         // Top N 报警

    // ──────────── 时间范围 ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHour1))]
    [NotifyPropertyChangedFor(nameof(IsHours4))]
    [NotifyPropertyChangedFor(nameof(IsHours24))]
    [NotifyPropertyChangedFor(nameof(MostFrequentLabel))]
    private AlarmCenterTimeRange _selectedTimeRange = AlarmCenterTimeRange.Hours4;

    // 派生属性支持双向绑定：UI RadioButton.IsChecked 直接绑定，
    // setter 内部更新 SelectedTimeRange，OnSelectedTimeRangeChanged 触发 RefreshStats。
    public bool IsHour1
    {
        get => SelectedTimeRange == AlarmCenterTimeRange.Hour1;
        set { if (value) SelectedTimeRange = AlarmCenterTimeRange.Hour1; }
    }
    public bool IsHours4
    {
        get => SelectedTimeRange == AlarmCenterTimeRange.Hours4;
        set { if (value) SelectedTimeRange = AlarmCenterTimeRange.Hours4; }
    }
    public bool IsHours24
    {
        get => SelectedTimeRange == AlarmCenterTimeRange.Hours24;
        set { if (value) SelectedTimeRange = AlarmCenterTimeRange.Hours24; }
    }

    // ──────────── KPI 属性 ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveEmptyStateMessage))]
    private int _activeCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayTriggerCountDisplay))]
    private int _todayTriggerCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayRecoverCountDisplay))]
    private int _todayRecoverCount;
    [ObservableProperty] private string _longestDurationText = "—";
    [ObservableProperty] private string _longestAlarmText = "—";
    [ObservableProperty] private string _mostFrequentAlarm = "—";
    [ObservableProperty] private int _affectedDeviceCount;
    [ObservableProperty] private DateTime _activeLastUpdateTime;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatsLastUpdateTime))]
    [NotifyPropertyChangedFor(nameof(StatsLastUpdateTimeText))]
    private DateTime? _statsLastUpdateTime;

    /// <summary>过滤后活跃报警条数（截断前全集，非列表可见条数）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTruncation))]
    [NotifyPropertyChangedFor(nameof(ActiveTruncationHint))]
    private int _filteredAlarmCount;

    /// <summary>事件流/统计加载失败提示（null=正常；非 null 显示红色横幅，DB 故障不再静默）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatsError))]
    [NotifyPropertyChangedFor(nameof(TodayTriggerCountDisplay))]
    [NotifyPropertyChangedFor(nameof(TodayRecoverCountDisplay))]
    [NotifyPropertyChangedFor(nameof(TodayTriggerKpiToolTip))]
    [NotifyPropertyChangedFor(nameof(TodayRecoverKpiToolTip))]
    [NotifyPropertyChangedFor(nameof(MostFrequentKpiToolTip))]
    private string? _statsErrorText;

    public bool HasStatsError => !string.IsNullOrWhiteSpace(StatsErrorText);

    /// <summary>今日触发 KPI 展示值（统计失败时显示 —，避免与真实 0 混淆）。</summary>
    public string TodayTriggerCountDisplay => HasStatsError ? "—" : TodayTriggerCount.ToString();

    /// <summary>今日恢复 KPI 展示值（统计失败时显示 —）。</summary>
    public string TodayRecoverCountDisplay => HasStatsError ? "—" : TodayRecoverCount.ToString();

    public string TodayTriggerKpiToolTip => HasStatsError ? StatsErrorText! : Strings.K710;

    public string TodayRecoverKpiToolTip => HasStatsError ? StatsErrorText! : Strings.K711;

    public string MostFrequentKpiToolTip => HasStatsError ? StatsErrorText! : Strings.K714;

    public bool HasTruncation => FilteredAlarmCount > MaxActiveAlarms;

    public string ActiveTruncationHint => HasTruncation
        ? string.Format(Strings.K708, MaxActiveAlarms)
        : string.Empty;

    public bool ShowStatsLastUpdateTime => StatsLastUpdateTime.HasValue;

    public string StatsLastUpdateTimeText => StatsLastUpdateTime.HasValue
        ? string.Format(Strings.F715, StatsLastUpdateTime.Value)
        : string.Empty;

    /// <summary>最频繁报警 KPI 标签：带时间范围标注，避免与"今日"口径混淆。</summary>
    public string MostFrequentLabel => string.Format(Strings.K707, SelectedTimeRange switch
    {
        AlarmCenterTimeRange.Hour1 => Strings.K042,
        AlarmCenterTimeRange.Hours4 => Strings.K043,
        _ => Strings.K025,
    });

    public string ActiveEmptyStateMessage => ActiveAlarms.Count == 0
        ? (_unfilteredActiveCount > 0 ? Strings.M060 : Strings.M061)
        : Strings.M061;

    // ──────────── 列表数据 ────────────

    /// <summary>实时活跃报警列表（按级别降序 + 触发时间升序）。</summary>
    public ObservableCollection<ActiveAlarmInfo> ActiveAlarms { get; } = new();

    /// <summary>最近事件流（按时间倒序，最新在最前）。</summary>
    public ObservableCollection<AlarmCenterEventItem> RecentEvents { get; } = new();

    /// <summary>Top N 报警排行（按触发次数降序）。</summary>
    public ObservableCollection<AlarmTopItem> TopAlarms { get; } = new();

    // ──────────── 级别筛选 ────────────

    [ObservableProperty] private bool _showHighAlarms = true;
    [ObservableProperty] private bool _showMediumAlarms = true;
    [ObservableProperty] private bool _showLowAlarms = true;

    [ObservableProperty] private string _alarmSearchText = string.Empty;
    [ObservableProperty] private string? _selectedDeviceId;

    public ObservableCollection<Device> DeviceFilterItems { get; } = new();

    public event Action<string, string>? ViewAlarmHistoryRequested;

    public AlarmCenterViewModel(
        IAlarmHistoryService historyService,
        IDeviceRepository deviceRepository,
        IDialogService dialog)
    {
        _historyService = historyService;
        _deviceRepository = deviceRepository;
        _dialog = dialog;

        foreach (var device in _deviceRepository.GetDevicesSnapshot().OrderBy(d => d.Name))
            DeviceFilterItems.Add(device);
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;

        _activeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _activeTimer.Tick += OnActiveTimerTick;

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(60),
        };
        _statsTimer.Tick += OnStatsTimerTick;

        // 搜索防抖：输入停止 350ms 后才重扫活跃列表，避免每个按键触发完整设备遍历
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _searchDebounceTimer.Tick += OnSearchDebounceTick;

        // 首次立即刷新一次，确保页面打开即有数据
        RefreshActiveAlarms(blockUntilApplied: true);
        RefreshStats();
    }

    /// <summary>
    /// 启动定时刷新（页面被切到时调用）。
    /// </summary>
    public void Start()
    {
        if (!_activeTimer.IsEnabled) _activeTimer.Start();
        if (!_statsTimer.IsEnabled) _statsTimer.Start();
        // 切回页面时立即刷新一次，避免显示过期数据
        RefreshActiveAlarms(blockUntilApplied: true);
        RefreshStats();
    }

    /// <summary>
    /// 停止定时刷新（页面切走时调用，减少无谓 CPU/DB 开销）。
    /// </summary>
    public void Stop()
    {
        _activeTimer.Stop();
        _statsTimer.Stop();
    }

    private void OnActiveTimerTick(object? sender, EventArgs e) => RefreshActiveAlarms();

    private void OnStatsTimerTick(object? sender, EventArgs e) => RefreshStats();

    /// <summary>
    /// 释放定时器资源：停止定时器并取消 Tick 事件订阅，避免 ViewModel 释放后仍被定时器回调持有。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _statsRefreshVersion++;
        _activeRefreshVersion++;
        _activeTimer.Stop();
        _statsTimer.Stop();
        _searchDebounceTimer.Stop();
        _activeTimer.Tick -= OnActiveTimerTick;
        _statsTimer.Tick -= OnStatsTimerTick;
        _searchDebounceTimer.Tick -= OnSearchDebounceTick;
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
    }

    partial void OnSelectedTimeRangeChanged(AlarmCenterTimeRange value) => RefreshStats();

    partial void OnShowHighAlarmsChanged(bool value)
    {
        RefreshActiveAlarms(blockUntilApplied: true);
        RefreshStats();
    }

    partial void OnShowMediumAlarmsChanged(bool value)
    {
        RefreshActiveAlarms(blockUntilApplied: true);
        RefreshStats();
    }

    partial void OnShowLowAlarmsChanged(bool value)
    {
        RefreshActiveAlarms(blockUntilApplied: true);
        RefreshStats();
    }
    partial void OnAlarmSearchTextChanged(string value)
    {
        // 防抖：停止已有计时，输入停顿 350ms 后才重扫，避免 IME 每个按键触发完整遍历
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }
    partial void OnSelectedDeviceIdChanged(string? value)
    {
        RefreshActiveAlarms(blockUntilApplied: true);
        RefreshStats();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        RefreshActiveAlarms();
        RefreshStats();
    }

    private void OnDevicesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 设备列表可能在后台线程变更（Remote 模式从 Collector 拉取/删除设备、DeviceManager 移除），
        // 而 DeviceFilterItems 是绑定 UI 的集合（未注册线程同步），必须封送 UI 线程——
        // 与 Home/Overview/ProductionLine 的同类回调保持一致，否则跨线程改集合抛异常。
        _uiDispatcher.BeginInvoke(new Action(() =>
        {
            DeviceFilterItems.Clear();
            foreach (var device in _deviceRepository.GetDevicesSnapshot().OrderBy(d => d.Name))
                DeviceFilterItems.Add(device);
            if (SelectedDeviceId != null && !DeviceFilterItems.Any(d => d.Id == SelectedDeviceId))
                SelectedDeviceId = null;
            RefreshActiveAlarms(blockUntilApplied: true);
        }));
    }

    [RelayCommand]
    private void RefreshAll()
    {
        RefreshActiveAlarms(blockUntilApplied: true);
        RefreshStats();
    }

    /// <summary>
    /// 刷新实时活跃报警列表：后台遍历设备快照 + 缓存的数据源未恢复报警，
    /// 收集 PLC 边沿报警、计数报警与历史未恢复的数据源报警。
    /// </summary>
    private void RefreshActiveAlarms(bool blockUntilApplied = false)
    {
        var requestVersion = ++_activeRefreshVersion;
        var context = CaptureActiveRefreshContext();

        if (blockUntilApplied)
        {
            var pending = ResolvePendingDataSourceForActive(context);
            var collected = BuildActiveAlarmList(context, pending);
            ApplyActiveAlarms(context.Now, collected, context);
            return;
        }

        Task.Run(() =>
        {
            var pending = ResolvePendingDataSourceForActive(context);
            var collected = BuildActiveAlarmList(context, pending);
            _uiDispatcher.BeginInvoke(() =>
            {
                if (_disposed || requestVersion != _activeRefreshVersion) return;
                ApplyActiveAlarms(context.Now, collected, context);
            }, DispatcherPriority.Background);
        }).Forget();
    }

    private ActiveRefreshContext CaptureActiveRefreshContext()
    {
        return new ActiveRefreshContext
        {
            Now = DateTime.Now,
            SelectedDeviceId = SelectedDeviceId,
            SearchText = AlarmSearchText,
            ShowHigh = ShowHighAlarms,
            ShowMedium = ShowMediumAlarms,
            ShowLow = ShowLowAlarms,
            Devices = _deviceRepository.GetDevicesSnapshot(),
            DeviceRepository = _deviceRepository,
        };
    }

    private List<AlarmEventRecord> ResolvePendingDataSourceForActive(ActiveRefreshContext context)
    {
        var pending = PendingDataSourceAlarmQuery.TryQueryPending(
            _historyService, context.Now, context.SelectedDeviceId, PendingDataSourceAlarmQuery.ActiveLookback);
        if (pending != null)
        {
            lock (_pendingDataSourceEvents)
                _pendingDataSourceEvents = pending;
            return pending;
        }

        lock (_pendingDataSourceEvents)
            return _pendingDataSourceEvents.ToList();
    }

    private List<ActiveAlarmInfo> BuildActiveAlarmList(
        ActiveRefreshContext context,
        IReadOnlyList<AlarmEventRecord> pendingDataSourceEvents)
    {
        var collected = new List<ActiveAlarmInfo>();

        foreach (var device in context.Devices)
        {
            foreach (var alarm in device.Alarms)
            {
                if (alarm.StartTime != default && alarm.EndTime == default)
                {
                    collected.Add(new ActiveAlarmInfo(
                        alarm.StartTime, device.Id, device.Name, alarm.Name, alarm.Level, AlarmKind.Plc,
                        alarm.NameEn, alarm.NameJa, alarm.NamePt));
                }
            }

            foreach (var ca in device.CounterAlarms)
            {
                if (!ca.Enabled || !ca.IsTriggered) continue;
                var triggerTime = ca.StartTime != default ? ca.StartTime : context.Now;
                collected.Add(new ActiveAlarmInfo(
                    triggerTime, device.Id, device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count,
                    ca.NameEn, ca.NameJa, ca.NamePt));
            }
        }

        foreach (var record in pendingDataSourceEvents)
        {
            if (context.SelectedDeviceId != null
                && !string.Equals(record.DeviceId, context.SelectedDeviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            var (nameEn, nameJa, namePt) = AlarmCenterDisplayHelper.ResolveEventLocalizedFields(
                context.DeviceRepository, record.DeviceId, record.AlarmId);
            collected.Add(new ActiveAlarmInfo(
                record.EventTime, record.DeviceId, record.DeviceName, record.AlarmName,
                AlarmLevel.Medium, AlarmKind.DataSource, nameEn, nameJa, namePt));
        }

        collected.Sort((a, b) =>
        {
            int c = b.Level.CompareTo(a.Level);
            return c != 0 ? c : a.EventTime.CompareTo(b.EventTime);
        });

        return collected;
    }

    private void ApplyActiveAlarms(DateTime now, List<ActiveAlarmInfo> collected, ActiveRefreshContext context)
    {
        _unfilteredActiveCount = collected.Count;

        var filtered = collected
            .Where(a => IsLevelVisible(a.Level, context.ShowHigh, context.ShowMedium, context.ShowLow)
                        && (context.SelectedDeviceId is null || a.DeviceId == context.SelectedDeviceId)
                        && MatchesActiveSearch(a, context.SearchText))
            .ToList();

        ActiveCount = filtered.Count;
        OnPropertyChanged(nameof(ActiveEmptyStateMessage));
        AffectedDeviceCount = filtered.Select(a => a.DeviceId).Distinct().Count();
        FilteredAlarmCount = filtered.Count;

        if (filtered.Count > 0)
        {
            var longest = filtered.OrderBy(a => a.EventTime).First();
            var ts = now - longest.EventTime;
            LongestDurationText = ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
                : $"{ts.Minutes}m {ts.Seconds}s";
            LongestAlarmText = $"{longest.DeviceName} · {longest.DisplayName}";
        }
        else
        {
            LongestDurationText = "—";
            LongestAlarmText = "—";
        }

        var visible = filtered.Take(MaxActiveAlarms).ToList();
        ObservableCollectionSyncHelper.Sync(ActiveAlarms, visible);

        foreach (var item in ActiveAlarms)
            item.RefreshDuration(now);

        ActiveLastUpdateTime = now;
    }

    private static bool MatchesActiveSearch(ActiveAlarmInfo alarm, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return alarm.DeviceName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || alarm.AlarmName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || alarm.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesEventSearch(
        IDeviceRepository deviceRepository,
        AlarmEventRecord record,
        string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var display = AlarmCenterDisplayHelper.ResolveEventDisplayName(deviceRepository, record);
        return record.DeviceName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || record.AlarmName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || display.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 刷新事件流与统计：单次查询 [min(窗口起点, 今日0点, 30天前), now]，内存切分窗口事件与今日事件，
    /// 计算今日触发/恢复数、Top N 报警（按触发次数降序）、最频繁报警名。
    /// 级别/搜索筛选与左栏活跃列表口径一致。
    /// </summary>
    private void RefreshStats()
    {
        var requestVersion = ++_statsRefreshVersion;
        var now = DateTime.Now;
        var from = SelectedTimeRange switch
        {
            AlarmCenterTimeRange.Hour1 => now.AddHours(-1),
            AlarmCenterTimeRange.Hours4 => now.AddHours(-4),
            AlarmCenterTimeRange.Hours24 => now.AddHours(-24),
            _ => now.AddHours(-4),
        };
        var todayStart = now.Date;
        var queryFrom = from < todayStart ? from : todayStart;
        var extendedFrom = queryFrom < now.AddDays(-30) ? queryFrom : now.AddDays(-30);
        var deviceId = SelectedDeviceId;
        var search = AlarmSearchText;
        var showHigh = ShowHighAlarms;
        var showMedium = ShowMediumAlarms;
        var showLow = ShowLowAlarms;
        var deviceRepository = _deviceRepository;

        void RefreshStatsCore()
        {
            try
            {
                var allEvents = _historyService.QueryAlarmEventsStrict(extendedFrom, now, deviceId);
                var windowEvents = allEvents.Where(e => e.EventTime >= from).ToList();
                var todayEvents = allEvents.Where(e => e.EventTime >= todayStart).ToList();

                bool MatchesFilters(AlarmEventRecord e) =>
                    IsLevelVisible(LookupAlarmLevel(deviceRepository, e.AlarmName, e.DeviceId, e.AlarmId), showHigh, showMedium, showLow)
                    && MatchesEventSearch(deviceRepository, e, search);

                var filteredWindow = windowEvents.Where(MatchesFilters).ToList();
                var filteredToday = todayEvents.Where(MatchesFilters).ToList();

                var triggerCount = filteredToday.Count(e => e.EventType == AlarmEventType.Triggered);
                var recoverCount = filteredToday.Count(e => e.EventType == AlarmEventType.Recovered);

                var topItems = filteredWindow
                    .Where(e => e.EventType == AlarmEventType.Triggered)
                    .GroupBy(e => new { e.AlarmName, e.DeviceName, e.DeviceId, e.AlarmId })
                    .Select(g =>
                    {
                        var sample = g.First();
                        return new AlarmTopItem
                        {
                            AlarmName = g.Key.AlarmName,
                            DisplayName = AlarmCenterDisplayHelper.ResolveEventDisplayName(deviceRepository, sample),
                            DeviceName = g.Key.DeviceName,
                            TriggerCount = g.Count(),
                            Level = LookupAlarmLevel(deviceRepository, g.Key.AlarmName, g.Key.DeviceId, g.Key.AlarmId),
                        };
                    })
                    .OrderByDescending(x => x.TriggerCount)
                    .Take(TopAlarmsCount)
                    .ToList();

                for (int i = 0; i < topItems.Count; i++)
                    topItems[i].Rank = i + 1;

                var recent = filteredWindow
                    .OrderByDescending(e => e.EventTime)
                    .Take(MaxRecentEvents)
                    .Select(e => new AlarmCenterEventItem(
                        e, AlarmCenterDisplayHelper.ResolveEventDisplayName(deviceRepository, e)))
                    .ToList();

                var mostFrequent = topItems.FirstOrDefault();

                var pendingSource = PendingDataSourceAlarmQuery.ExtractPending(allEvents);

                void ApplyStats()
                {
                    if (_disposed || requestVersion != _statsRefreshVersion) return;
                    StatsErrorText = null;
                    lock (_pendingDataSourceEvents)
                        _pendingDataSourceEvents = pendingSource;
                    ObservableCollectionSyncHelper.Sync(RecentEvents, recent);
                    ObservableCollectionSyncHelper.Sync(TopAlarms, topItems);

                    TodayTriggerCount = triggerCount;
                    TodayRecoverCount = recoverCount;
                    MostFrequentAlarm = mostFrequent != null
                        ? string.Format(Strings.F033, mostFrequent.DisplayName, mostFrequent.TriggerCount)
                        : "—";
                    StatsLastUpdateTime = now;
                    RefreshActiveAlarms(blockUntilApplied: Application.Current != null);
                }

                if (Application.Current != null)
                    _uiDispatcher.Invoke(ApplyStats);
                else
                    ApplyStats();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "报警中心刷新统计失败");
                void ApplyError()
                {
                    if (_disposed || requestVersion != _statsRefreshVersion) return;
                    StatsErrorText = string.Format(Strings.F073, ex.Message);
                    lock (_pendingDataSourceEvents)
                        _pendingDataSourceEvents = new List<AlarmEventRecord>();
                    RecentEvents.Clear();
                    TopAlarms.Clear();
                    TodayTriggerCount = 0;
                    TodayRecoverCount = 0;
                    MostFrequentAlarm = "—";
                    StatsLastUpdateTime = now;
                }

                if (Application.Current != null)
                    _uiDispatcher.Invoke(ApplyError);
                else
                    ApplyError();
            }
        }

        if (Application.Current != null)
            Task.Run(RefreshStatsCore).Forget();
        else
            RefreshStatsCore();
    }

    /// <summary>
    /// 根据报警名与设备 Id 查找配置中的报警级别（Top 排行展示用）。
    /// 数据源报警固定为 Medium；未找到返回 Low（保守显示）。
    /// </summary>
    private static AlarmLevel LookupAlarmLevel(
        IDeviceRepository deviceRepository,
        string alarmName,
        string deviceId,
        string? alarmId = null)
    {
        if (alarmId != null && alarmId.StartsWith("src:", StringComparison.OrdinalIgnoreCase))
            return AlarmLevel.Medium;

        var device = deviceRepository.GetDeviceById(deviceId);
        if (device == null) return AlarmLevel.Low;
        var alarm = device.Alarms.FirstOrDefault(a => a.Name == alarmName);
        if (alarm != null) return alarm.Level;
        var counter = device.CounterAlarms.FirstOrDefault(a => a.Name == alarmName);
        return counter != null ? AlarmLevel.Medium : AlarmLevel.Low;
    }

    private static bool IsLevelVisible(AlarmLevel level, bool showHigh, bool showMedium, bool showLow) => level switch
    {
        AlarmLevel.High => showHigh,
        AlarmLevel.Medium => showMedium,
        AlarmLevel.Low => showLow,
        _ => true,
    };

    private bool IsLevelVisible(AlarmLevel level) =>
        IsLevelVisible(level, ShowHighAlarms, ShowMediumAlarms, ShowLowAlarms);

    [RelayCommand]
    private void CopyAlarm(ActiveAlarmInfo? alarm)
    {
        if (alarm == null) return;
        Clipboard.SetText(string.Format(Strings.F032, alarm.DeviceName, alarm.AlarmName, alarm.Level, alarm.EventTime, alarm.DurationText));
        _dialog.NotifySuccess(Strings.M007);
    }

    [RelayCommand]
    private void ClearDeviceFilter() => SelectedDeviceId = null;

    [RelayCommand]
    private void ViewAlarmHistory(ActiveAlarmInfo? alarm)
    {
        if (alarm == null) return;
        ViewAlarmHistoryRequested?.Invoke(alarm.DeviceId, alarm.AlarmName);
    }

    public static string EventTypeText(AlarmEventType eventType) => eventType switch
    {
        AlarmEventType.Triggered => Strings.EventType_Triggered,
        AlarmEventType.Recovered => Strings.EventType_Recovered,
        AlarmEventType.ShiftChange => Strings.EventType_ShiftChange,
        _ => eventType.ToString(),
    };

    private sealed class ActiveRefreshContext
    {
        public DateTime Now { get; init; }
        public string? SelectedDeviceId { get; init; }
        public string SearchText { get; init; } = string.Empty;
        public bool ShowHigh { get; init; }
        public bool ShowMedium { get; init; }
        public bool ShowLow { get; init; }
        public IReadOnlyList<Device> Devices { get; init; } = Array.Empty<Device>();
        public IDeviceRepository DeviceRepository { get; init; } = null!;
    }
}
