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
    /// <summary>本班次：按班次配置解析当前班次起止（无有效班次时回退今日 0:00 起）。</summary>
    CurrentShift,
}

/// <summary>Top 排行排序维度：按触发次数 或 按报警持续总时长。</summary>
public enum AlarmTopSortMode
{
    ByTriggerCount,
    ByTotalDuration,
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
    /// <summary>窗口内报警总时长（由触发/恢复事件对推算；未恢复的按至 now 计）。</summary>
    public TimeSpan TotalDuration { get; set; }
    /// <summary>排行右侧数值文案：按触发次数时为触发数，按持续时长时为格式化时长（由 VM 按当前维度填充）。</summary>
    public string RankValueText { get; set; } = string.Empty;
    public AlarmLevel Level { get; set; }
    /// <summary>排名序号（1-based，由 ViewModel 填充）</summary>
    public int Rank { get; set; }
}

/// <summary>事件流列表项：附带从设备配置解析的多语言显示名。
/// 报警风暴合并组（RepeatCount &gt; 1）以组内最新事件为主记录，其余次数由附加字段承载。</summary>
public sealed class AlarmCenterEventItem
{
    public AlarmEventRecord Record { get; }
    public string DisplayName { get; }
    public AlarmEventType EventType => Record.EventType;
    public string DeviceName => Record.DeviceName;
    public DateTime EventTime => Record.EventTime;
    /// <summary>组内最早事件时间（单条事件时与 EventTime 相同）。</summary>
    public DateTime FirstEventTime { get; }
    /// <summary>组内事件总数（风暴合并后 &gt;1，单条事件为 1）。</summary>
    public int RepeatCount { get; }
    /// <summary>组内触发次数（仅风暴合并组填写，单条事件为 0）。</summary>
    public int TriggerCount { get; }
    /// <summary>组内恢复次数（仅风暴合并组填写，单条事件为 0）。</summary>
    public int RecoverCount { get; }
    /// <summary>是否风暴合并组，UI 据此显示 "×N" 角标。</summary>
    public bool IsStormGroup => RepeatCount > 1;
    /// <summary>"×N" 角标文本（语言无关数字符号，规避 XAML StringFormat 转义）。</summary>
    public string RepeatBadge => IsStormGroup ? $"×{RepeatCount}" : string.Empty;
    /// <summary>风暴合并说明悬浮文案。</summary>
    public string RepeatTooltip => IsStormGroup
        ? string.Format(MainAPP.Resources.Strings.K718, RepeatCount)
        : string.Empty;
    /// <summary>相对时间文案（刚刚/N 分钟前/N 小时前/MM-dd HH:mm），随 60s 统计刷新滚动更新。</summary>
    public string EventRelativeText
    {
        get
        {
            var elapsed = DateTime.Now - EventTime;
            if (elapsed.TotalSeconds < 60) return MainAPP.Resources.Strings.K720;
            if (elapsed.TotalMinutes < 60) return string.Format(MainAPP.Resources.Strings.K721, (int)elapsed.TotalMinutes);
            if (elapsed.TotalHours < 24) return string.Format(MainAPP.Resources.Strings.K722, (int)elapsed.TotalHours);
            return EventTime.ToString("MM-dd HH:mm");
        }
    }
    /// <summary>相对时间悬浮提示：完整绝对时间。</summary>
    public string EventRelativeTooltip => EventTime.ToString("MM-dd HH:mm:ss");

    public AlarmCenterEventItem(AlarmEventRecord record, string displayName)
        : this(record, displayName, repeatCount: 1, triggerCount: 0, recoverCount: 0)
    {
    }

    public AlarmCenterEventItem(
        AlarmEventRecord record,
        string displayName,
        int repeatCount,
        int triggerCount,
        int recoverCount,
        DateTime firstEventTime = default)
    {
        Record = record;
        DisplayName = displayName;
        RepeatCount = repeatCount;
        TriggerCount = triggerCount;
        RecoverCount = recoverCount;
        FirstEventTime = firstEventTime == default ? record.EventTime : firstEventTime;
    }
}

/// <summary>
/// 报警中心 ViewModel：实时活跃报警墙 + 最近 N 小时事件流 + Top 报警排行 + KPI 汇总。
/// 数据来源：DeviceRepository.Runtimes（实时活跃报警）+ HistoryService.QueryAlarmEvents（事件流与统计）。
/// 不修改数据库 schema，纯只读监控视图。
/// </summary>
public partial class AlarmCenterViewModel : ObservableObject, IDisposable, INavigationPageLifecycle
{
    private readonly IAlarmHistoryService _historyService;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDialogService _dialog;
    private readonly PageRefreshTimer _activeTimer;  // 3s 刷新活跃报警
    private readonly PageRefreshTimer _statsTimer;   // 60s 刷新事件流与统计
    private readonly PageRefreshTimer _searchDebounceTimer;  // 搜索输入防抖（350ms）
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private int _statsRefreshVersion;
    private int _activeRefreshVersion;
    private bool _disposed;
    private int _unfilteredActiveCount;
    private List<ActiveAlarmStateRecord> _pendingDataSourceEvents = new();
    private readonly AppSettings? _appSettings;
    private readonly IAlarmSessionMute? _alarmSessionMute;

    private const int MaxActiveAlarms = 200;       // 活跃报警列表上限（避免极端情况内存膨胀）
    private const int MaxRecentEvents = 500;       // 事件流展示上限
    private const int TopAlarmsCount = 10;         // Top N 报警
    /// <summary>
    /// 报警风暴合并窗口（秒）：同一设备+同一报警的相邻事件时间间隔不超过该窗口并入同一组，
    /// 避免"触发→恢复→触发"高频循环在事件流刷屏（现场突发/报警振荡场景常见）。
    /// </summary>
    private const int StormMergeWindowSeconds = 120;

    // ──────────── 时间范围 ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHour1))]
    [NotifyPropertyChangedFor(nameof(IsHours4))]
    [NotifyPropertyChangedFor(nameof(IsHours24))]
    [NotifyPropertyChangedFor(nameof(IsCurrentShift))]
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
    public bool IsCurrentShift
    {
        get => SelectedTimeRange == AlarmCenterTimeRange.CurrentShift;
        set { if (value) SelectedTimeRange = AlarmCenterTimeRange.CurrentShift; }
    }

    // ──────────── KPI 属性 ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveEmptyStateMessage))]
    [NotifyPropertyChangedFor(nameof(ActiveCountTooltip))]
    private int _activeCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayTriggerCountDisplay))]
    [NotifyPropertyChangedFor(nameof(TodayTriggerKpiToolTip))]
    private int _todayTriggerCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayRecoverCountDisplay))]
    [NotifyPropertyChangedFor(nameof(TodayRecoverKpiToolTip))]
    private int _todayRecoverCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LongestDurationTooltip))]
    private string _longestDurationText = "—";
    [ObservableProperty] private string _longestAlarmText = "—";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostFrequentKpiToolTip))]
    private string _mostFrequentAlarm = "—";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AffectedDeviceTooltip))]
    private int _affectedDeviceCount;
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

    public string ActiveCountTooltip => FormatHelper.Tip("{0}\\n{1}", Strings.K709, ActiveCount);
    public string AffectedDeviceTooltip => FormatHelper.Tip("{0}\\n{1}", Strings.K713, AffectedDeviceCount);
    public string LongestDurationTooltip => FormatHelper.Tip("{0}\\n{1}", Strings.K712, LongestDurationText);

    public string TodayTriggerKpiToolTip => HasStatsError
        ? StatsErrorText!
        : FormatHelper.Tip("{0}\\n{1}", Strings.K710, TodayTriggerCount);

    public string TodayRecoverKpiToolTip => HasStatsError
        ? StatsErrorText!
        : FormatHelper.Tip("{0}\\n{1}", Strings.K711, TodayRecoverCount);

    public string MostFrequentKpiToolTip => HasStatsError
        ? StatsErrorText!
        : FormatHelper.Tip("{0}\\n{1}", Strings.K714, MostFrequentAlarm);

    public bool HasTruncation => FilteredAlarmCount > MaxActiveAlarms;

    public string ActiveTruncationHint => HasTruncation
        ? string.Format(Strings.K708, MaxActiveAlarms)
        : string.Empty;

    /// <summary>窗口内事件流合并后的总条数（截断前，已按级别/设备/搜索筛选）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentEventTruncation))]
    [NotifyPropertyChangedFor(nameof(RecentEventTruncationHint))]
    private int _recentEventTotalCount;

    /// <summary>Top 聚类组总数（截断前），仅用于判断是否超过展示上限。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopAlarmTruncation))]
    [NotifyPropertyChangedFor(nameof(TopAlarmTruncationHint))]
    private int _topAlarmGroupCount;

    // ──────────── P0-3 修复 2026-09-02：事件流 / Top 排行的截断提示 ────────────
    // 左栏活跃报警已有"仅显示前 200 条"提示，但右栏事件流（500 条合并项上限）与
    // Top 排行（10 条上限）此前无任何提示，数据超限时用户无从得知列表被截断，
    // 容易误读为"总共就这么多"。故补齐同级提示，与 ActiveTruncationHint 口径一致。
    public bool HasRecentEventTruncation => RecentEventTotalCount > MaxRecentEvents;

    public string RecentEventTruncationHint => HasRecentEventTruncation
        ? string.Format(Strings.K708, MaxRecentEvents)
        : string.Empty;

    public bool HasTopAlarmTruncation => TopAlarmGroupCount > TopAlarmsCount;

    public string TopAlarmTruncationHint => HasTopAlarmTruncation
        ? string.Format(Strings.K708, TopAlarmsCount)
        : string.Empty;

    public bool ShowStatsLastUpdateTime => StatsLastUpdateTime.HasValue;

    public string StatsLastUpdateTimeText => StatsLastUpdateTime.HasValue
        ? string.Format(Strings.F715, StatsLastUpdateTime.Value)
        : string.Empty;

    // ──────────── 今日 KPI 较昨日同期对比（昨日 0 点 至"今日已过时长"同窗口径） ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayTriggerCompareText))]
    [NotifyPropertyChangedFor(nameof(TodayTriggerCompareTooltip))]
    [NotifyPropertyChangedFor(nameof(TodayTriggerCompareBetter))]
    private int _yesterdayTriggerCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayRecoverCompareText))]
    [NotifyPropertyChangedFor(nameof(TodayRecoverCompareTooltip))]
    [NotifyPropertyChangedFor(nameof(TodayRecoverCompareBetter))]
    private int _yesterdayRecoverCount;

    /// <summary>今日触发较昨日同期变化文案（▲/▼ + 百分比；不可比时 —）。</summary>
    public string TodayTriggerCompareText => FormatCompareText(TodayTriggerCount, YesterdayTriggerCount);
    /// <summary>今日触发较昨日同期是否"更好"（触发减少为好）。</summary>
    public bool TodayTriggerCompareBetter => !HasStatsError && TodayTriggerCount <= YesterdayTriggerCount;
    public string TodayTriggerCompareTooltip => HasStatsError
        ? StatsErrorText!
        : string.Format(Strings.K719, YesterdayTriggerCount, TodayTriggerCount);

    /// <summary>今日恢复较昨日同期变化文案（▲/▼ + 百分比；不可比时 —）。</summary>
    public string TodayRecoverCompareText => FormatCompareText(TodayRecoverCount, YesterdayRecoverCount);
    /// <summary>今日恢复较昨日同期是否"更好"（恢复增加为好）。</summary>
    public bool TodayRecoverCompareBetter => !HasStatsError && TodayRecoverCount >= YesterdayRecoverCount;
    public string TodayRecoverCompareTooltip => HasStatsError
        ? StatsErrorText!
        : string.Format(Strings.K719, YesterdayRecoverCount, TodayRecoverCount);

    private string FormatCompareText(int today, int yesterday)
    {
        if (HasStatsError || yesterday <= 0) return "—";
        var delta = today - yesterday;
        if (delta == 0) return "±0%";
        var pct = (int)Math.Round(Math.Abs(delta) * 100.0 / yesterday);
        return delta > 0 ? $"▲ {pct}%" : $"▼ {pct}%";
    }

    /// <summary>最频繁报警 KPI 标签：带时间范围标注，避免与"今日"口径混淆。</summary>
    public string MostFrequentLabel => string.Format(Strings.K707, SelectedTimeRange switch
    {
        AlarmCenterTimeRange.Hour1 => Strings.K042,
        AlarmCenterTimeRange.Hours4 => Strings.K043,
        AlarmCenterTimeRange.CurrentShift => Strings.K078,
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

    // ──────────── 页面级静音（与会话级 IAlarmSessionMute 同步，主页静音按钮同源） ────────────

    [ObservableProperty] private bool _isAlarmMuted;

    partial void OnIsAlarmMutedChanged(bool value)
    {
        if (_alarmSessionMute != null)
            _alarmSessionMute.IsMuted = value;
    }

    /// <summary>切换页面级报警静音。</summary>
    [RelayCommand]
    private void ToggleAlarmMute() => IsAlarmMuted = !IsAlarmMuted;

    // ──────────── Top 排行维度（按触发次数 / 按持续时长） ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTopSortByCount))]
    [NotifyPropertyChangedFor(nameof(IsTopSortByDuration))]
    private AlarmTopSortMode _topSortMode = AlarmTopSortMode.ByTriggerCount;

    public bool IsTopSortByCount
    {
        get => TopSortMode == AlarmTopSortMode.ByTriggerCount;
        set { if (value) TopSortMode = AlarmTopSortMode.ByTriggerCount; }
    }
    public bool IsTopSortByDuration
    {
        get => TopSortMode == AlarmTopSortMode.ByTotalDuration;
        set { if (value) TopSortMode = AlarmTopSortMode.ByTotalDuration; }
    }

    partial void OnTopSortModeChanged(AlarmTopSortMode value) => RefreshStats();

    public ObservableCollection<Device> DeviceFilterItems { get; } = new();

    public event Action<string, string>? ViewAlarmHistoryRequested;

    public AlarmCenterViewModel(
        IAlarmHistoryService historyService,
        IDeviceRepository deviceRepository,
        IDialogService dialog,
        AppSettings? appSettings = null,
        IAlarmSessionMute? alarmSessionMute = null)
    {
        _historyService = historyService;
        _deviceRepository = deviceRepository;
        _dialog = dialog;
        _appSettings = appSettings;
        _alarmSessionMute = alarmSessionMute;
        _isAlarmMuted = _alarmSessionMute?.IsMuted ?? false;

        foreach (var device in _deviceRepository.GetDevicesSnapshot().OrderBy(d => d.Name))
            DeviceFilterItems.Add(device);
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;

        _activeTimer = new PageRefreshTimer(TimeSpan.FromSeconds(3), OnActiveTimerTick);

        _statsTimer = new PageRefreshTimer(TimeSpan.FromSeconds(60), OnStatsTimerTick);

        // 搜索防抖：输入停止 350ms 后才重扫活跃列表，避免每个按键触发完整设备遍历
        _searchDebounceTimer = new PageRefreshTimer(TimeSpan.FromMilliseconds(350), OnSearchDebounceTick);
    }

    /// <summary>
    /// 启动定时刷新（页面被切到时调用）。
    /// </summary>
    public void Start()
    {
        if (!_activeTimer.IsEnabled) _activeTimer.Start();
        if (!_statsTimer.IsEnabled) _statsTimer.Start();
        // 切回页面时立即刷新一次，避免显示过期数据（异步应用，避免 blockUntilApplied 在 UI 线程同步扫设备+历史库）
        RefreshActiveAlarms(blockUntilApplied: false);
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

    // ──────────── 页面生命周期（审查修复 2026-08-30：N-3/P2-7 定时器泄漏） ────────────
    // 此前由 AlarmCenterView 的 Loaded/Unloaded 驱动 Start/Stop，但 NavigationPageHost 常驻
    // （视图加载后永不卸载，Unloaded 永不触发），定时器在切走后持续运行、逐页叠加。
    // 改由 MainWindow.ActivatePage 经 INavigationPageLifecycle 驱动，页面切走立即停止。

    /// <inheritdoc />
    public void OnPageEnter() => Start();

    /// <inheritdoc />
    public void OnPageExit() => Stop();

    private void OnActiveTimerTick() => RefreshActiveAlarms();

    private void OnStatsTimerTick() => RefreshStats();

    /// <summary>
    /// 释放定时器资源：停止并解绑定时器（PageRefreshTimer 内部解绑 Tick），避免 ViewModel 释放后仍被回调持有。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _statsRefreshVersion++;
        _activeRefreshVersion++;
        _activeTimer.Dispose();
        _statsTimer.Dispose();
        _searchDebounceTimer.Dispose();
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

    private void OnSearchDebounceTick()
    {
        // 单次语义：防抖到期执行一次即停，下次输入变化时由 OnAlarmSearchTextChanged 重新 Start
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
            var pending = ResolveActiveSourceStates(context);
            var collected = BuildActiveAlarmList(context, pending);
            ApplyActiveAlarms(context.Now, collected, context);
            return;
        }

        Task.Run(() =>
        {
            var pending = ResolveActiveSourceStates(context);
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

    /// <summary>
    /// 解析当前活跃的数据源报警：直查报警活跃状态表（失败回退上一轮缓存）。
    /// 状态行由采集端边沿 Upsert 维护，IsActive 即当前事实，不再回溯历史事件推断。
    /// </summary>
    private List<ActiveAlarmStateRecord> ResolveActiveSourceStates(ActiveRefreshContext context)
    {
        var pending = PendingDataSourceAlarmQuery.TryQueryActiveSourceStates(_historyService, context.SelectedDeviceId);
        if (pending != null)
        {
            // 存在性校验：仅保留当前 PLC 配置中仍存在的值项行，残留孤儿行不再进活跃墙
            pending = PendingDataSourceAlarmQuery.FilterByCurrentState(pending, context.Devices);
            lock (_pendingDataSourceEvents)
                _pendingDataSourceEvents = pending;
            return pending;
        }

        lock (_pendingDataSourceEvents)
            return _pendingDataSourceEvents.ToList();
    }

    private List<ActiveAlarmStateRecord> SnapshotPendingDataSourceEvents()
    {
        lock (_pendingDataSourceEvents)
            return _pendingDataSourceEvents.ToList();
    }

    private List<ActiveAlarmInfo> BuildActiveAlarmList(
        ActiveRefreshContext context,
        IReadOnlyList<ActiveAlarmStateRecord> pendingDataSourceEvents)
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
                record.TriggeredAt, record.DeviceId, record.DeviceName, record.AlarmName,
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
        // 值差分预处理（身份键含 EventTime）：静止报警复用既有实例 → Sync 引用差分零集合事件，
        // 虚拟化容器不重建、滚动位置保留。原先每次 new 全部对象，引用比较必然全部失配，
        // 等价于 Clear+AddRange：每 3s 约 400 次集合通知 + 200 个 DataTemplate 重建。
        // 重新触发的报警 EventTime 变化 → 键不同 → 走 Replace，UI 取到新触发时刻。
        // DurationText 是 [ObservableProperty]，复用实例后靠下方 RefreshDuration 原地刷新驱动 UI。
        ObservableCollectionSyncHelper.ReuseExisting(ActiveAlarms, visible,
            a => (a.DeviceId, a.AlarmName, a.Level, a.Kind, a.EventTime, a.AlarmNameEn, a.AlarmNameJa, a.AlarmNamePt));
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
    /// 解析查询窗口起点：按所选时间范围；"本班次"通过班次配置解析当前班次起点
    /// （跨午夜班次由 ShiftConfigResolver.ResolveRange 处理），无班次配置或不在班次时段时回退今日 0:00。
    /// </summary>
    private DateTime ResolveWindowStart(DateTime now, DateTime todayStart)
    {
        if (SelectedTimeRange == AlarmCenterTimeRange.Hour1) return now.AddHours(-1);
        if (SelectedTimeRange == AlarmCenterTimeRange.Hours24) return now.AddHours(-24);
        if (SelectedTimeRange == AlarmCenterTimeRange.CurrentShift)
        {
            if (_appSettings != null)
            {
                var snap = ShiftConfigResolver.ResolveCurrentShift(_appSettings.GetShiftsSnapshot(), now); // P0-1 修复 2026-09-02
                if (snap.Shift != null) return snap.Start;
            }
            return todayStart;
        }
        return now.AddHours(-4);
    }

    /// <summary>
    /// 推算同一（设备+报警）事件组的总持续时间：按时间升序将 触发→恢复 配对累加，
    /// 未闭合的触发按至 now 计（报警仍持续中）。供 Top 排行"按持续时长"维度排序。
    /// </summary>
    private static TimeSpan ComputeAlarmTotalDuration(
        IEnumerable<AlarmEventRecord> events,
        DateTime now)
    {
        var sorted = events.OrderBy(e => e.EventTime).ToList();
        double totalSeconds = 0;
        DateTime? openStart = null;
        foreach (var e in sorted)
        {
            if (e.EventType == AlarmEventType.Triggered)
            {
                if (openStart == null) openStart = e.EventTime;
            }
            else if (e.EventType == AlarmEventType.Recovered && openStart.HasValue)
            {
                totalSeconds += (e.EventTime - openStart.Value).TotalSeconds;
                openStart = null;
            }
        }
        if (openStart.HasValue)
            totalSeconds += (now - openStart.Value).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
    }

    /// <summary>
    /// 刷新事件流与统计：单次查询 [窗口起点 ∪ 今日0点, now]，内存切分窗口事件与今日事件，
    /// 计算今日触发/恢复数（含较昨日同期对比）、Top N 报警（按触发次数/持续时长维度）、最频繁报警名。
    /// 级别/搜索筛选与左栏活跃列表口径一致。
    /// 修复（2026-08-30）：原实现恒按"今日0点与30天前取更早"回退 30 天全量拉取再内存过滤，
    /// 每次 60s 刷新都全表扫 30 天；现只查询所需窗口（≤24h，默认 4h）。
    /// </summary>
    private void RefreshStats()
    {
        var requestVersion = ++_statsRefreshVersion;
        var now = DateTime.Now;
        var todayStart = now.Date;
        var from = ResolveWindowStart(now, todayStart);
        var queryFrom = from < todayStart ? from : todayStart;   // 覆盖窗口与今日 KPI 两个口径的最小并集
        var deviceId = SelectedDeviceId;
        var search = AlarmSearchText;
        var showHigh = ShowHighAlarms;
        var showMedium = ShowMediumAlarms;
        var showLow = ShowLowAlarms;
        var deviceRepository = _deviceRepository;
        var sortMode = TopSortMode;

        void RefreshStatsCore()
        {
            try
            {
                // 修复（2026-08-30）：只拉 [queryFrom, now]，不再回退 30 天全量
                var allEvents = _historyService.QueryAlarmEventsStrict(queryFrom, now, deviceId);
                var windowEvents = allEvents.Where(e => e.EventTime >= from).ToList();
                var todayEvents = allEvents.Where(e => e.EventTime >= todayStart).ToList();

                bool MatchesFilters(AlarmEventRecord e) =>
                    IsLevelVisible(LookupAlarmLevel(deviceRepository, e.AlarmName, e.DeviceId, e.AlarmId), showHigh, showMedium, showLow)
                    && MatchesEventSearch(deviceRepository, e, search);

                var filteredWindow = windowEvents.Where(MatchesFilters).ToList();
                var filteredToday = todayEvents.Where(MatchesFilters).ToList();

                var triggerCount = filteredToday.Count(e => e.EventType == AlarmEventType.Triggered);
                var recoverCount = filteredToday.Count(e => e.EventType == AlarmEventType.Recovered);

                // 较昨日同期：昨日 [0 点, 0 点 + 今日已过时长] 同窗口径
                var yesterdayTrigger = 0;
                var yesterdayRecover = 0;
                try
                {
                    var elapsedToday = now - todayStart;
                    var yesterdayStart = todayStart.AddDays(-1);
                    var yesterdayEnd = yesterdayStart + elapsedToday;
                    var yesterdayEvents = _historyService.QueryAlarmEventsStrict(yesterdayStart, yesterdayEnd, deviceId)
                        .Where(MatchesFilters)
                        .ToList();
                    yesterdayTrigger = yesterdayEvents.Count(e => e.EventType == AlarmEventType.Triggered);
                    yesterdayRecover = yesterdayEvents.Count(e => e.EventType == AlarmEventType.Recovered);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "报警中心昨日同期对比查询失败，仅展示今日数值");
                }

                // Top 排行：按（设备+报警）聚类，同时统计触发次数与总时长，再按当前维度排序
                var keyGroups = filteredWindow
                    .GroupBy(e => new { e.AlarmName, e.DeviceName, e.DeviceId, e.AlarmId })
                    .Select(g =>
                    {
                        var sample = g.First();
                        return new AlarmTopItem
                        {
                            AlarmName = g.Key.AlarmName,
                            DisplayName = AlarmCenterDisplayHelper.ResolveEventDisplayName(deviceRepository, sample),
                            DeviceName = g.Key.DeviceName,
                            TriggerCount = g.Count(e => e.EventType == AlarmEventType.Triggered),
                            TotalDuration = ComputeAlarmTotalDuration(g, now),
                            Level = LookupAlarmLevel(deviceRepository, g.Key.AlarmName, g.Key.DeviceId, g.Key.AlarmId),
                        };
                    })
                    .ToList();

                // P0-3：先统计截断前总数（Top 聚类组数 / 事件流合并条数），供截断提示展示
                var topGroupCount = keyGroups.Count;
                var recentAll = BuildRecentStream(filteredWindow, deviceRepository);
                var recentTotal = recentAll.Count;

                var topItems = (sortMode == AlarmTopSortMode.ByTotalDuration
                        ? keyGroups.OrderByDescending(x => x.TotalDuration.TotalSeconds)
                        : keyGroups.OrderByDescending(x => x.TriggerCount))
                    .Take(TopAlarmsCount)
                    .ToList();

                for (int i = 0; i < topItems.Count; i++)
                {
                    topItems[i].Rank = i + 1;
                    topItems[i].RankValueText = sortMode == AlarmTopSortMode.ByTotalDuration
                        ? Kanban.Contracts.Formatting.DurationFormatter.FormatCompact(topItems[i].TotalDuration.TotalSeconds)
                        : topItems[i].TriggerCount.ToString();
                }

                var recent = recentAll
                    .Take(MaxRecentEvents)
                    .ToList();

                // 最频繁报警保持"触发次数最多"口径（与 Top 显示维度解耦）
                var mostFrequent = keyGroups.OrderByDescending(x => x.TriggerCount).FirstOrDefault();

                // 活跃报警数据源：直查状态表（不再从窗口事件流回溯 7/30 天推断），失败时回退上一轮缓存
                var activeStates = PendingDataSourceAlarmQuery.TryQueryActiveSourceStates(_historyService, deviceId);
                var pendingSource = activeStates != null
                    ? PendingDataSourceAlarmQuery.FilterByCurrentState(activeStates, deviceRepository.GetDevicesSnapshot())
                    : SnapshotPendingDataSourceEvents();

                void ApplyStats()
                {
                    if (_disposed || requestVersion != _statsRefreshVersion) return;
                    StatsErrorText = null;
                    lock (_pendingDataSourceEvents)
                        _pendingDataSourceEvents = pendingSource;
                    ObservableCollectionSyncHelper.Sync(RecentEvents, recent);
                    ObservableCollectionSyncHelper.Sync(TopAlarms, topItems);
                    RecentEventTotalCount = recentTotal;
                    TopAlarmGroupCount = topGroupCount;

                    TodayTriggerCount = triggerCount;
                    TodayRecoverCount = recoverCount;
                    YesterdayTriggerCount = yesterdayTrigger;
                    YesterdayRecoverCount = yesterdayRecover;
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
                        _pendingDataSourceEvents = new List<ActiveAlarmStateRecord>();
                    RecentEvents.Clear();
                    TopAlarms.Clear();
                    RecentEventTotalCount = 0;
                    TopAlarmGroupCount = 0;
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
    /// 构建最近事件流（含报警风暴抑制）。
    /// 按事件时间倒序遍历，以【设备+报警】为 key 做窗口聚类：同一 key 的事件只要与该 key
    /// 最近一条事件的间隔 ≤ StormMergeWindowSeconds 即并入同组——即使中间穿插了其他设备的
    /// 报警也不断组（多设备交错振荡是报警风暴的典型形态，相邻合并将完全失效）。
    /// 每个 key 可因时间断档形成多组；整组合并为一条展示项，主记录取组内最新事件
    /// （图标/颜色/时间戳语义不变），剩余次数与触发/恢复计数放在附加字段，UI 显示 "×N" 角标。
    /// 完成后按各组最新事件时间倒序返回。
    /// internal：供 MainAPP.Tests 单元测试直接验证合并语义。
    /// </summary>
    internal static List<AlarmCenterEventItem> BuildRecentStream(
        IReadOnlyList<AlarmEventRecord> filteredWindow,
        IDeviceRepository deviceRepository)
    {
        var windowTs = TimeSpan.FromSeconds(StormMergeWindowSeconds);
        // key → 该 key 当前打开的组：Items（倒序累积，[0]=组内最新，[^1]=组内最旧）、
        // Newest=组内最新事件时间（恒定）、LastAdded=最近并入事件时间（断档判断用）
        var groupsByKey = new Dictionary<string, (List<AlarmEventRecord> Items, DateTime Newest, DateTime LastAdded)>();
        var allGroups = new List<(List<AlarmEventRecord> Items, DateTime Newest, DateTime LastAdded)>();

        foreach (var e in filteredWindow.OrderByDescending(x => x.EventTime))
        {
            var key = $"{e.DeviceId}|{e.AlarmName}";
            if (groupsByKey.TryGetValue(key, out var g) && (g.LastAdded - e.EventTime) <= windowTs)
            {
                g.Items.Add(e);
                g.LastAdded = e.EventTime;          // 倒序迭代：相邻并入，间隙用上次并入时间判断
                groupsByKey[key] = g;
            }
            else
            {
                // 该 key 无打开组或已断档：先归档旧组，再开新组
                if (g.Items is not null)
                    allGroups.Add(g);
                groupsByKey[key] = (new List<AlarmEventRecord> { e }, e.EventTime, e.EventTime);
            }
        }

        foreach (var kvp in groupsByKey)
            allGroups.Add(kvp.Value);

        // 每组以组内最新事件时间为准，倒序输出
        var result = new List<AlarmCenterEventItem>();
        foreach (var g in allGroups.OrderByDescending(x => x.Newest))
        {
            var latest = g.Items[0];
            var triggerCount = g.Items.Count(r => r.EventType == AlarmEventType.Triggered);
            var recoverCount = g.Items.Count(r => r.EventType == AlarmEventType.Recovered);
            result.Add(new AlarmCenterEventItem(
                latest,
                AlarmCenterDisplayHelper.ResolveEventDisplayName(deviceRepository, latest),
                repeatCount: g.Items.Count,
                triggerCount: triggerCount,
                recoverCount: recoverCount,
                firstEventTime: g.Items[^1].EventTime));
        }

        return result;
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
