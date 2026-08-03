using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using MainAPP.Helpers;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
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
    public double TotalDurationMinutes { get; set; }
    public string TotalDurationText => TotalDurationMinutes >= 60
        ? $"{TotalDurationMinutes / 60.0:F1}h"
        : $"{TotalDurationMinutes:F0}min";
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
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, DateTime> _countAlarmTriggerTimes = new();
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

    public string ActiveEmptyStateMessage => ActiveCount > 0
        ? "当前筛选无匹配报警"
        : "暂无活跃故障";

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
        _activeTimer.Tick -= OnActiveTimerTick;
        _statsTimer.Tick -= OnStatsTimerTick;
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _countAlarmTriggerTimes.Clear();
    }

    partial void OnSelectedTimeRangeChanged(AlarmCenterTimeRange value) => RefreshStats();

    partial void OnShowHighAlarmsChanged(bool value) => RefreshActiveAlarms();
    partial void OnShowMediumAlarmsChanged(bool value) => RefreshActiveAlarms();
    partial void OnShowLowAlarmsChanged(bool value) => RefreshActiveAlarms();
    partial void OnAlarmSearchTextChanged(string value) => RefreshActiveAlarms();
    partial void OnSelectedDeviceIdChanged(string? value) => RefreshActiveAlarms();

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
    /// 刷新实时活跃报警列表：遍历所有设备的 Alarms 与 CountAlarms，
    /// 收集 StartTime!=default &amp;&amp; EndTime==default 的 PLC 报警 + IsTriggered 的计数报警。
    /// 与 HomeViewModel.RefreshActiveAlarms 逻辑一致但简化（无 CountAlarm 去抖）。
    /// </summary>
    private void RefreshActiveAlarms()
    {
        var now = DateTime.Now;
        var collected = new List<ActiveAlarmInfo>();
        var activeCountAlarmKeys = new HashSet<string>();

        foreach (var device in _deviceRepository.GetDevicesSnapshot())
        {
            // PLC 边沿报警：StartTime 已设置且未恢复
            foreach (var alarm in device.Alarms)
            {
                if (alarm.StartTime != default && alarm.EndTime == default
                    )
                {
                    collected.Add(new ActiveAlarmInfo(
                        alarm.StartTime, device.Name, alarm.Name, alarm.Level, AlarmKind.Plc));
                }
            }

            // 计数报警：已触发且启用
            foreach (var ca in device.CountAlarms)
            {
                if (ca.Enabled && ca.IsTriggered)
                {
                    var key = $"{device.Id}_{ca.Id}";
                    activeCountAlarmKeys.Add(key);
                    if (!_countAlarmTriggerTimes.TryGetValue(key, out var triggerTime))
                    {
                        triggerTime = now;
                        _countAlarmTriggerTimes[key] = triggerTime;
                    }
                    collected.Add(new ActiveAlarmInfo(
                        triggerTime, device.Name, ca.Name, AlarmLevel.Medium, AlarmKind.Count));
                }
            }
        }

        foreach (var key in _countAlarmTriggerTimes.Keys
                     .Where(key => !activeCountAlarmKeys.Contains(key))
                     .ToList())
            _countAlarmTriggerTimes.Remove(key);

        // 排序：级别降序 + 触发时间升序
        collected.Sort((a, b) =>
        {
            int c = b.Level.CompareTo(a.Level);
            return c != 0 ? c : a.EventTime.CompareTo(b.EventTime);
        });

        ActiveCount = collected.Count;
        AffectedDeviceCount = collected.Select(a => a.DeviceName).Distinct().Count();

        if (collected.Count > 0)
        {
            var longest = collected.OrderBy(a => a.EventTime).First();
            var ts = now - longest.EventTime;
            LongestDurationText = ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
                : $"{ts.Minutes}m {ts.Seconds}s";
            LongestAlarmText = $"{longest.DeviceName} · {longest.AlarmName}";
        }
        else
        {
            LongestDurationText = "—";
            LongestAlarmText = "—";
        }

        var visible = collected
            .Where(a => IsLevelVisible(a.Level) && IsAlarmVisible(a.DeviceName, a.AlarmName))
            .Take(MaxActiveAlarms)
            .ToList();

        // 复用已有实例：从 ActiveAlarms 中查找相等项（ActiveAlarmInfo.Equals 基于值），
        // 让 ObservableCollectionSyncHelper 的引用比较能识别未变化项，保留 DurationText 连续性，避免列表闪烁
        for (int i = 0; i < visible.Count; i++)
        {
            var existing = ActiveAlarms.FirstOrDefault(a => a.Equals(visible[i]));
            if (existing != null)
                visible[i] = existing;
        }

        // 差分更新（保留未变化项引用，避免列表闪烁）
        ObservableCollectionSyncHelper.Sync(ActiveAlarms, visible);

        // 刷新持续时间文本
        foreach (var item in ActiveAlarms)
            item.RefreshDuration(now);

        LastUpdateTime = now;
    }

    /// <summary>
    /// 刷新事件流与统计：从 HistoryService 查询最近 N 小时报警事件，
    /// 计算今日触发/恢复数、Top N 报警、最频繁报警名。
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

        // 后台线程查询避免阻塞 UI（HistoryService.QueryAlarmEvents 是同步 EF 调用）
        Task.Run(() =>
        {
            try
            {
                var events = _historyService.QueryAlarmEvents(from, now);

                // 今日 KPI（按日期过滤，不受时间范围影响）
                var today = now.Date;
                var todayEvents = _historyService.QueryAlarmEvents(today, now);
                var triggerCount = todayEvents.Count(e => e.EventType == AlarmEventType.Triggered);
                var recoverCount = todayEvents.Count(e => e.EventType == AlarmEventType.Recovered);

                // Top N 报警（按触发次数）
                var topItems = events
                    .Where(e => e.EventType == AlarmEventType.Triggered)
                    .GroupBy(e => new { e.AlarmName, e.DeviceName })
                    .Select(g => new AlarmTopItem
                    {
                        AlarmName = g.Key.AlarmName,
                        DeviceName = g.Key.DeviceName,
                        TriggerCount = g.Count(),
                        Level = LookupAlarmLevel(g.Key.AlarmName, g.Key.DeviceName),
                        TotalDurationMinutes = CalculateTotalDurationMinutes(g.ToList(), events),
                    })
                    .OrderByDescending(x => x.TotalDurationMinutes)
                    .Take(TopAlarmsCount)
                    .ToList();

                // 填充排名序号（1-based）
                for (int i = 0; i < topItems.Count; i++)
                    topItems[i].Rank = i + 1;

                // 最近事件流（时间倒序，截断上限）
                var recent = events
                    .OrderByDescending(e => e.EventTime)
                    .Take(MaxRecentEvents)
                    .ToList();

                // 最频繁报警
                var mostFrequent = topItems.FirstOrDefault();

                // 封送回 UI 线程更新集合
                _uiDispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed || requestVersion != _statsRefreshVersion) return;
                    ObservableCollectionSyncHelper.Sync(RecentEvents, recent);
                    ObservableCollectionSyncHelper.Sync(TopAlarms, topItems);

                    TodayTriggerCount = triggerCount;
                    TodayRecoverCount = recoverCount;
                    MostFrequentAlarm = mostFrequent != null
                        ? $"{mostFrequent.AlarmName}（{mostFrequent.TriggerCount}次）"
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
                    _dialog.NotifyError($"刷新报警统计失败: {ex.Message}");
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

    private bool IsAlarmVisible(string deviceName, string alarmName)
    {
        if (SelectedDeviceId != null && _deviceRepository.Devices.FirstOrDefault(d => d.Id == SelectedDeviceId)?.Name != deviceName)
            return false;
        return string.IsNullOrWhiteSpace(AlarmSearchText)
            || deviceName.Contains(AlarmSearchText, StringComparison.OrdinalIgnoreCase)
            || alarmName.Contains(AlarmSearchText, StringComparison.OrdinalIgnoreCase);
    }

    private static double CalculateTotalDurationMinutes(
        List<AlarmEventRecord> triggers, List<AlarmEventRecord> allEvents)
    {
        var total = 0.0;
        foreach (var trigger in triggers)
        {
            var recovery = allEvents
                .Where(e => e.DeviceId == trigger.DeviceId && e.AlarmId == trigger.AlarmId
                    && e.EventType == AlarmEventType.Recovered && e.EventTime > trigger.EventTime)
                .OrderBy(e => e.EventTime)
                .FirstOrDefault();
            var endTime = recovery?.EventTime ?? DateTime.Now;
            total += (endTime - trigger.EventTime).TotalMinutes;
        }
        return Math.Max(0, total);
    }

    [RelayCommand]
    private void CopyAlarm(ActiveAlarmInfo? alarm)
    {
        if (alarm == null) return;
        Clipboard.SetText($"{alarm.DeviceName} · {alarm.AlarmName}\n级别：{alarm.Level}\n触发时间：{alarm.EventTime:yyyy-MM-dd HH:mm:ss}\n持续时间：{alarm.DurationText}");
        _dialog.NotifySuccess("报警信息已复制");
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
        AlarmEventType.Triggered => "触发",
        AlarmEventType.Recovered => "恢复",
        AlarmEventType.ShiftChange => "班次切换",
        _ => eventType.ToString(),
    };
}
