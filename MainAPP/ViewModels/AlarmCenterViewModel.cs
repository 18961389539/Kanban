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
    public string DeviceName { get; set; } = string.Empty;
    public int TriggerCount { get; set; }
    public AlarmLevel Level { get; set; }
    /// <summary>排名序号（1-based，由 ViewModel 填充）</summary>
    public int Rank { get; set; }
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
    private readonly Dictionary<string, DateTime> _counterAlarmTriggerTimes = new();
    private int _statsRefreshVersion;
    private bool _disposed;

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
    [ObservableProperty] private int _todayTriggerCount;
    [ObservableProperty] private int _todayRecoverCount;
    [ObservableProperty] private string _longestDurationText = "—";
    [ObservableProperty] private string _longestAlarmText = "—";
    [ObservableProperty] private string _mostFrequentAlarm = "—";
    [ObservableProperty] private int _affectedDeviceCount;
    [ObservableProperty] private DateTime _lastUpdateTime;

    /// <summary>过滤后活跃报警条数（截断提示判断依据）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTruncation))]
    [NotifyPropertyChangedFor(nameof(ActiveTruncationHint))]
    private int _visibleAlarmCount;

    /// <summary>事件流/统计加载失败提示（null=正常；非 null 显示红色横幅，DB 故障不再静默）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatsError))]
    private string? _statsErrorText;

    public bool HasStatsError => !string.IsNullOrWhiteSpace(StatsErrorText);

    public bool HasTruncation => VisibleAlarmCount > MaxActiveAlarms;

    public string ActiveTruncationHint => HasTruncation
        ? string.Format(Strings.K708, MaxActiveAlarms)
        : string.Empty;

    /// <summary>最频繁报警 KPI 标签：带时间范围标注，避免与"今日"口径混淆。</summary>
    public string MostFrequentLabel => string.Format(Strings.K707, SelectedTimeRange switch
    {
        AlarmCenterTimeRange.Hour1 => Strings.K042,
        AlarmCenterTimeRange.Hours4 => Strings.K043,
        _ => Strings.K025,
    });

    public string ActiveEmptyStateMessage => ActiveCount > 0
        ? Strings.M060
        : Strings.M061;

    // ──────────── 列表数据 ────────────

    /// <summary>实时活跃报警列表（按级别降序 + 触发时间升序）。</summary>
    public ObservableCollection<ActiveAlarmInfo> ActiveAlarms { get; } = new();

    /// <summary>最近事件流（按时间倒序，最新在最前）。</summary>
    public ObservableCollection<AlarmEventRecord> RecentEvents { get; } = new();

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
        RefreshActiveAlarms();
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
        RefreshActiveAlarms();
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
        _activeTimer.Stop();
        _statsTimer.Stop();
        _searchDebounceTimer.Stop();
        _activeTimer.Tick -= OnActiveTimerTick;
        _statsTimer.Tick -= OnStatsTimerTick;
        _searchDebounceTimer.Tick -= OnSearchDebounceTick;
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _counterAlarmTriggerTimes.Clear();
    }

    partial void OnSelectedTimeRangeChanged(AlarmCenterTimeRange value) => RefreshStats();

    partial void OnShowHighAlarmsChanged(bool value) => RefreshActiveAlarms();
    partial void OnShowMediumAlarmsChanged(bool value) => RefreshActiveAlarms();
    partial void OnShowLowAlarmsChanged(bool value) => RefreshActiveAlarms();
    partial void OnAlarmSearchTextChanged(string value)
    {
        // 防抖：停止已有计时，输入停顿 350ms 后才重扫，避免 IME 每个按键触发完整遍历
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }
    partial void OnSelectedDeviceIdChanged(string? value) => RefreshActiveAlarms();

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        RefreshActiveAlarms();
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
            RefreshActiveAlarms();
        }));
    }

    [RelayCommand]
    private void RefreshAll()
    {
        RefreshActiveAlarms();
        RefreshStats();
    }

    /// <summary>
    /// 刷新实时活跃报警列表：遍历所有设备的 Alarms 与 CounterAlarms，
    /// 收集 StartTime!=default &amp;&amp; EndTime==default 的 PLC 报警 + IsTriggered 的计数报警。
    /// 与 HomeViewModel.RefreshActiveAlarms 逻辑一致但简化（无 CounterAlarm 去抖）。
    /// </summary>
    private void RefreshActiveAlarms()
    {
        var now = DateTime.Now;
        var collected = new List<ActiveAlarmInfo>();
        var activeCounterAlarmKeys = new HashSet<string>();

        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            // PLC 边沿报警：StartTime 已设置且未恢复
            foreach (var alarm in device.Alarms)
            {
                if (alarm.StartTime != default && alarm.EndTime == default
                    )
                {
                    collected.Add(new ActiveAlarmInfo(
                        alarm.StartTime, device.Name, alarm.Name, alarm.Level, AlarmKind.Plc,
                        alarm.NameEn, alarm.NameJa, alarm.NamePt));
                }
            }

            // 计数报警：已触发且启用
            foreach (var ca in device.CounterAlarms)
            {
                if (ca.Enabled && ca.IsTriggered)
                {
                    var key = $"{device.Id}_{ca.Id}";
                    activeCounterAlarmKeys.Add(key);
                    if (!_counterAlarmTriggerTimes.TryGetValue(key, out var triggerTime))
                    {
                        triggerTime = now;
                        _counterAlarmTriggerTimes[key] = triggerTime;
                    }
                    collected.Add(new ActiveAlarmInfo(
                        triggerTime, device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count,
                        ca.NameEn, ca.NameJa, ca.NamePt));
                }
            }
        }

        foreach (var key in _counterAlarmTriggerTimes.Keys
                     .Where(key => !activeCounterAlarmKeys.Contains(key))
                     .ToList())
            _counterAlarmTriggerTimes.Remove(key);

        // 排序：级别降序 + 触发时间升序
        collected.Sort((a, b) =>
        {
            int c = b.Level.CompareTo(a.Level);
            return c != 0 ? c : a.EventTime.CompareTo(b.EventTime);
        });

        ActiveCount = collected.Count;

        // 级别/设备/搜索过滤后的集合（KPI 与列表共用同一口径；审查修复 2026-08-13：
        // 原 KPI 基于过滤前全集——用户按级别筛选时，KPI 卡显示的最长报警可能不在下方列表中）
        var selectedDeviceName = SelectedDeviceId is null
            ? null
            : _deviceRepository.Devices.FirstOrDefault(d => d.Id == SelectedDeviceId)?.Name;
        var filtered = collected
            .Where(a => IsLevelVisible(a.Level)
                        && (selectedDeviceName is null || a.DeviceName == selectedDeviceName)
                        && (string.IsNullOrWhiteSpace(AlarmSearchText)
                            || a.DeviceName.Contains(AlarmSearchText, StringComparison.OrdinalIgnoreCase)
                            || a.AlarmName.Contains(AlarmSearchText, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 影响设备数：与列表共用同一过滤口径（审查修复 2026-08-14）
        AffectedDeviceCount = filtered.Select(a => a.DeviceName).Distinct().Count();

        // 截断提示依据：过滤后条数超过展示上限时提示"仅显示前 N 条"
        VisibleAlarmCount = filtered.Count;

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

        // 差分更新：每 tick 用最新 NameEn 构造新实例，避免复用旧快照导致多语言名称不刷新
        ObservableCollectionSyncHelper.Sync(ActiveAlarms, visible);

        // 刷新持续时间文本
        foreach (var item in ActiveAlarms)
            item.RefreshDuration(now);

        LastUpdateTime = now;
    }

    /// <summary>
    /// 刷新事件流与统计：单次查询 [min(窗口起点, 今日0点), now]，内存切分窗口事件与今日事件，
    /// 计算今日触发/恢复数、Top N 报警（按触发次数降序）、最频繁报警名。
    /// 使用严格查询：DB 故障时抛出并展示内联错误横幅，不再静默显示为"无报警"。
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
        // 单次查询覆盖两个窗口的并集，避免每轮刷新两次全量扫描（审查修复 2026-08-14）
        var queryFrom = from < todayStart ? from : todayStart;

        // 后台线程查询避免阻塞 UI（HistoryService 查询是同步 EF 调用）
        Task.Run(() =>
        {
            try
            {
                var allEvents = _historyService.QueryAlarmEventsStrict(queryFrom, now);
                var windowEvents = allEvents.Where(e => e.EventTime >= from).ToList();
                var todayEvents = allEvents.Where(e => e.EventTime >= todayStart).ToList();

                // 今日 KPI（按日期过滤，不受时间范围影响）
                var triggerCount = todayEvents.Count(e => e.EventType == AlarmEventType.Triggered);
                var recoverCount = todayEvents.Count(e => e.EventType == AlarmEventType.Recovered);

                // Top N 报警（按触发次数降序；审查修复 2026-08-13：原按 TotalDurationMinutes 排序，
                // 却以"最频繁报警"标签+次数文案展示——时长最长与触发最频繁口径混淆，工业看板会误报根因）
                var topItems = windowEvents
                    .Where(e => e.EventType == AlarmEventType.Triggered)
                    .GroupBy(e => new { e.AlarmName, e.DeviceName })
                    .Select(g => new AlarmTopItem
                    {
                        AlarmName = g.Key.AlarmName,
                        DeviceName = g.Key.DeviceName,
                        TriggerCount = g.Count(),
                        Level = LookupAlarmLevel(g.Key.AlarmName, g.Key.DeviceName),
                    })
                    .OrderByDescending(x => x.TriggerCount)
                    .Take(TopAlarmsCount)
                    .ToList();

                // 填充排名序号（1-based）
                for (int i = 0; i < topItems.Count; i++)
                    topItems[i].Rank = i + 1;

                // 最近事件流（时间倒序，截断上限）
                var recent = windowEvents
                    .OrderByDescending(e => e.EventTime)
                    .Take(MaxRecentEvents)
                    .ToList();

                // 最频繁报警
                var mostFrequent = topItems.FirstOrDefault();

                // 封送回 UI 线程更新集合
                _uiDispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed || requestVersion != _statsRefreshVersion) return;
                    StatsErrorText = null;
                    ObservableCollectionSyncHelper.Sync(RecentEvents, recent);
                    ObservableCollectionSyncHelper.Sync(TopAlarms, topItems);

                    TodayTriggerCount = triggerCount;
                    TodayRecoverCount = recoverCount;
                    MostFrequentAlarm = mostFrequent != null
                        ? string.Format(Strings.F033, mostFrequent.AlarmName, mostFrequent.TriggerCount)
                        : "—";
                    LastUpdateTime = now;
                }));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "报警中心刷新统计失败");
                _uiDispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed || requestVersion != _statsRefreshVersion) return;
                    // 内联横幅提示故障，并清空上一轮残留数据——避免"看起来正常但数据是旧的"
                    StatsErrorText = string.Format(Strings.F073, ex.Message);
                    RecentEvents.Clear();
                    TopAlarms.Clear();
                    TodayTriggerCount = 0;
                    TodayRecoverCount = 0;
                    MostFrequentAlarm = "—";
                    LastUpdateTime = now;
                }));
            }
        }).Forget();
    }

    /// <summary>
    /// 根据报警名与设备名查找配置中的报警级别（Top 排行展示用）。
    /// 未找到返回 Low（保守显示）。
    /// </summary>
    private AlarmLevel LookupAlarmLevel(string alarmName, string deviceName)
    {
        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            if (device.Name != deviceName) continue;
            var alarm = device.Alarms.FirstOrDefault(a => a.Name == alarmName);
            if (alarm != null) return alarm.Level;
        }
        return AlarmLevel.Low;
    }

    private bool IsLevelVisible(AlarmLevel level) => level switch
    {
        AlarmLevel.High => ShowHighAlarms,
        AlarmLevel.Medium => ShowMediumAlarms,
        AlarmLevel.Low => ShowLowAlarms,
        _ => true,
    };

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
        var device = _deviceRepository.Devices.FirstOrDefault(d => d.Name == alarm.DeviceName);
        if (device == null) return;
        ViewAlarmHistoryRequested?.Invoke(device.Id, alarm.AlarmName);
    }

    public static string EventTypeText(AlarmEventType eventType) => eventType switch
    {
        AlarmEventType.Triggered => Strings.EventType_Triggered,
        AlarmEventType.Recovered => Strings.EventType_Recovered,
        AlarmEventType.ShiftChange => Strings.EventType_ShiftChange,
        _ => eventType.ToString(),
    };
}
