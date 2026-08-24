using System.Collections.ObjectModel;
using MainAPP.Resources;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using MainAPP.Helpers;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Kanban.Contracts.Metrics;
using OxyPlot;
using System.Windows.Media;

namespace MainAPP.ViewModels;

/// <summary>主页数据状态档位（无设备 / 断线 / 无数据 / 实时）。</summary>
public enum HomeDataStatus
{
    NoDevice,
    Disconnected,
    NoData,
    Live,
}

/// <summary>
/// 主页仪表板 ViewModel：2 行 3 列共 6 张卡片。
/// 全部数据来自设备内存 Runtime，零数据库读取。
/// 每 3 秒从 DeviceRuntime 同步一次本地属性。
/// </summary>
public partial class HomeViewModel : ObservableObject, IDisposable, INavigationPageLifecycle
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IPlcConnectionManager _connectionManager;
    private readonly AppSettings _appSettings;
    private readonly IRuntimeMode _runtimeMode;
    private readonly IDeviceSelectionService _selection;
    private readonly IPlcDataAcquisitionService? _plcService;
    private readonly RemoteRuntimeSink? _remoteRuntimeSink;
    private readonly IWorkOrderRepository? _workOrderRepo;
    private readonly IDialogService? _dialog;
    private readonly IWorkOrderService? _workOrderService;
    private readonly DispatcherTimer _liveTimer;

    /// <summary>
    /// 设备快照缓存：仅在 Devices.CollectionChanged 时重建，RefreshActiveAlarms 每 tick 直接遍历，
    /// 避免每 3 秒 Devices.ToList() 分配。
    /// </summary>
    private List<Device> _deviceSnapshot = [];

    private readonly HomeAlarmCollector _alarmCollector = new();
    private readonly ShiftProgressProvider _shiftProgress;
    private readonly LastShiftComparisonProvider _lastShiftProvider;

    /// <summary>
    /// 已提示产量达标的工单 Id 集合（去重，每个工单仅弹一次 Growl）。
    /// 工单切换/完成后残留条目无害（仅内存占用），应用重启后自动清空。
    /// </summary>
    private readonly HashSet<int> _notifiedWorkOrderIds = [];

    /// <summary>
    /// 当前工单的「工单内 OK 产量」缓存（按工单口径，非会话累计）。
    /// 由 RefreshWorkOrderSummary 经可取消的工单产量查询后台回填。
    /// </summary>
    private int _currentWorkOrderOk;

    /// <summary>工单产量聚合查询节流：上次查询的工单 Id 与时刻（同一 Running 工单 2s 内复用）。</summary>
    private int? _lastSummaryOrderId;
    private DateTime _lastSummaryQueryUtc = DateTime.MinValue;
    private int _workOrderSummaryRunning;
    private int _workOrderSummaryRefreshRequested;
    private readonly object _workOrderSummaryGate = new();
    private CancellationTokenSource? _workOrderSummaryCts;
    private readonly CancellationTokenSource _disposeCts = new();
    private static readonly TimeSpan WorkOrderSummaryThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DataFreshnessTimeout = TimeSpan.FromSeconds(5);
    /// <summary>
    /// 主页实时故障列表最大显示条数。主页为摘要视图，空间有限，故小于报警中心的上限
    /// （<see cref="AlarmCenterViewModel"/> 的 MaxActiveAlarms=200）。
    /// 排序（级别降序 + 时间升序）后截断，保留最关键/最新的报警。
    /// 2026-08-11：10 → 5（用户要求精简）。
    /// </summary>
    private const int MaxHomeActiveAlarms = 5;

    // ──────────── 图表 diff 缓存（避免每 3 秒无变化重建 PlotModel） ────────────
    private (double a, double p, double q) _lastOeeInput;
    private (int r, int a, int p) _lastStatusInput;
    private int _lastDefectSignature;
    private (int ok, int ng) _lastQualityInput;

    // ──────────── 设备选择 ────────────

    public ObservableCollection<DeviceFilterItem> DeviceFilterItems { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewDeviceDetailCommand))]
    private string? _selectedDeviceId;
    [ObservableProperty] private Device? _currentDevice;
    [ObservableProperty] private DeviceRuntime? _currentRuntime;

    // ──────────── 班次进度 ────────────

    [ObservableProperty] private string _shiftProgressName = "";
    [ObservableProperty] private string _shiftProgressText = "";
    [ObservableProperty] private double _shiftProgressRatio;
    [ObservableProperty] private string _shiftProgressPct = "";
    /// <summary>
    /// 是否显示班次进度区域：无班次配置或非班次时段时为 false，隐藏顶部班次 UI。
    /// </summary>
    [ObservableProperty] private bool _isShiftProgressVisible;

    // ──────────── 设备状态卡右上角：当前班次 + 日期时钟 ────────────

    /// <summary>设备状态卡右上角班次标签，如 "早班 08:00-20:00"。</summary>
    [ObservableProperty] private string _deviceStatusShiftTag = "";
    /// <summary>设备状态卡右上角日期时钟，格式 "MM-dd HH:mm"。</summary>
    [ObservableProperty] private string _deviceStatusClock = "";

    // ──────────── 当前工单（顶部栏工单条） ────────────

    /// <summary>当前选中设备的工单（Running 优先，无则取最新 Pending）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentWorkOrder))]
    [NotifyPropertyChangedFor(nameof(WorkOrderProgressText))]
    [NotifyPropertyChangedFor(nameof(WorkOrderProgressRatio))]
    [NotifyPropertyChangedFor(nameof(WorkOrderProgressPct))]
    [NotifyPropertyChangedFor(nameof(WorkOrderTargetQuantity))]
    private WorkOrder? _currentWorkOrder;

    /// <summary>是否当前有工单（控制顶部工单条可见性）。</summary>
    public bool HasCurrentWorkOrder => CurrentWorkOrder != null;

    /// <summary>工单进度文本：OK产量 / 计划产量（如 "1200 / 5000 件"）。</summary>
    public string WorkOrderProgressText => CurrentWorkOrder != null
        ? string.Format(Strings.F029, _currentWorkOrderOk, CurrentWorkOrder.TargetQuantity)
        : "";

    /// <summary>工单进度比例（0.0-1.0，超额时 Clamp 到 1.0 避免进度条溢出）。</summary>
    public double WorkOrderProgressRatio => CurrentWorkOrder != null && CurrentWorkOrder.TargetQuantity > 0
        ? Math.Clamp((double)_currentWorkOrderOk / CurrentWorkOrder.TargetQuantity, 0, 1)
        : 0;

    /// <summary>工单进度百分比文本（如 "24%"）。</summary>
    public string WorkOrderProgressPct => CurrentWorkOrder != null
        ? $"{WorkOrderProgressRatio * 100:F0}%"
        : "";

    /// <summary>主页工单卡片显示的工单内 OK 产量，与顶部工单进度使用同一口径。</summary>
    public string WorkOrderOkProductionDisplay => CanDisplayKpiData && CurrentWorkOrder != null
        ? $"{_currentWorkOrderOk:N0}"
        : "—";

    /// <summary>工单设置数量（计划产量）。无工单时返回 0。</summary>
    public int WorkOrderTargetQuantity => CurrentWorkOrder?.TargetQuantity ?? 0;

    // ──────────── 班次产量目标进度（已移除：UI 不再绑定，相关字段删除） ────────────

    // ──────────── 上班次对比 ────────────

    /// <summary>上班次名称（如 "白班"），无数据时为空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastShift))]
    [NotifyPropertyChangedFor(nameof(LastShiftLabel))]
    private string _lastShiftName = "";
    /// <summary>上班次 OK 产量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastShiftTotal))]
    [NotifyPropertyChangedFor(nameof(ShiftOutputDiff))]
    [NotifyPropertyChangedFor(nameof(ShiftOutputDiffText))]
    private int _lastShiftOk;
    /// <summary>上班次 NG 产量。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastShiftTotal))]
    [NotifyPropertyChangedFor(nameof(ShiftNgDiff))]
    [NotifyPropertyChangedFor(nameof(ShiftNgDiffText))]
    private int _lastShiftNg;
    /// <summary>上班次总产量 = OK + NG。</summary>
    public int LastShiftTotal => LastShiftOk + LastShiftNg;
    /// <summary>本班次 vs 上班次产量差异（正=增长，负=下降）。无上班次数据时为 0。</summary>
    public int ShiftOutputDiff => HasLastShift ? TotalOkProduction - LastShiftOk : 0;
    /// <summary>本班次 vs 上班次不良数差异（正=增加，负=减少）。</summary>
    public int ShiftNgDiff => HasLastShift ? TotalNgProduction - LastShiftNg : 0;
    /// <summary>是否存在上班次数据（用于 UI 控制对比区域可见性）。</summary>
    public bool HasLastShift => !string.IsNullOrEmpty(LastShiftName);
    /// <summary>
    /// 上班次行左侧标签：有数据 "上班次·夜班"；无数据仅 "上班次"（2026-08-11：
    /// 上班次行不再隐藏，无数据时 OK/NG 显示 0）。
    /// </summary>
    public string LastShiftLabel => string.IsNullOrEmpty(LastShiftName)
        ? Strings.K260
        : string.Format(Strings.F265, LastShiftName);
    /// <summary>
    /// 本班次 vs 上班次产量差异显示文本：正数前缀 "+"，负数带 "-"，0 返回空字符串。
    /// 用于 UI 在差异为 0 时隐藏徽章。
    /// </summary>
    public string ShiftOutputDiffText => FormatHelper.FormatDiff(ShiftOutputDiff);
    /// <summary>本班次 vs 上班次不良数差异显示文本。</summary>
    public string ShiftNgDiffText => FormatHelper.FormatDiff(ShiftNgDiff);

    // ──────────── 第 2 行 列 1：OEE 预览 ────────────

    [ObservableProperty] private double _oeeValue;
    /// <summary>良品率（0-1）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityGapText))]
    private double _qualityRate;
    [ObservableProperty] private double _performanceRate;
    [ObservableProperty] private double _availabilityRate;
    [ObservableProperty] private PlotModel? _oeeRingChart;
    [ObservableProperty] private PlotModel? _availabilityRingChart;
    [ObservableProperty] private PlotModel? _performanceRingChart;
    [ObservableProperty] private PlotModel? _qualityRingChart;

    /// <summary>
    /// OEE 4 环图计算所用的具体数字（显示在环图下方，便于核对计算过程）。
    /// OEE = 可用率 × 性能率 × 合格率；其余为分子/分母形式。
    /// </summary>
    [ObservableProperty] private string _oeeFormulaText = "";
    [ObservableProperty] private string _availabilityFormulaText = "";
    [ObservableProperty] private string _performanceFormulaText = "";
    [ObservableProperty] private string _qualityFormulaText = "";

    // ──────────── 第 1 行 列 2：当前生产状态 ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedAchievementRate))]
    [NotifyPropertyChangedFor(nameof(ActualCycleSec))]
    [NotifyPropertyChangedFor(nameof(CycleDiffText))]
    [NotifyPropertyChangedFor(nameof(IsCycleSlow))]
    private double _realtimeSpeed;
    [ObservableProperty] private double _speedAchievementRate;
    /// <summary>目标速度（件/小时），即设备 TargetCycle。用于计算速度达成率。</summary>
    [ObservableProperty] private int _targetSpeed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalOutput))]
    [NotifyPropertyChangedFor(nameof(NgRate))]
    [NotifyPropertyChangedFor(nameof(NgRateDisplay))]
    [NotifyPropertyChangedFor(nameof(ShiftOutputDiff))]
    [NotifyPropertyChangedFor(nameof(ShiftOutputDiffText))]
    private int _totalOkProduction;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalOutput))]
    [NotifyPropertyChangedFor(nameof(NgRate))]
    [NotifyPropertyChangedFor(nameof(NgRateDisplay))]
    [NotifyPropertyChangedFor(nameof(ShiftNgDiff))]
    [NotifyPropertyChangedFor(nameof(ShiftNgDiffText))]
    private int _totalNgProduction;

    // ──────────── 第 1 行 列 1：设备状态 ────────────

    /// <summary>配方名称（从当前设备同步，用于设备状态卡显示当前生产型号）。</summary>
    [ObservableProperty] private string _recipeName = "";
    /// <summary>配方值（从当前设备同步）。</summary>
    [ObservableProperty] private int _recipeValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTimeRatio))]
    private int _realtimeStatus;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTimeRatio))]
    [NotifyPropertyChangedFor(nameof(AlarmTimeRatio))]
    [NotifyPropertyChangedFor(nameof(PausedTimeRatio))]
    [NotifyPropertyChangedFor(nameof(TotalTimeFormatted))]
    private double _runTime;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTimeRatio))]
    [NotifyPropertyChangedFor(nameof(AlarmTimeRatio))]
    [NotifyPropertyChangedFor(nameof(PausedTimeRatio))]
    [NotifyPropertyChangedFor(nameof(TotalTimeFormatted))]
    private double _alarmTime;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTimeRatio))]
    [NotifyPropertyChangedFor(nameof(AlarmTimeRatio))]
    [NotifyPropertyChangedFor(nameof(PausedTimeRatio))]
    [NotifyPropertyChangedFor(nameof(TotalTimeFormatted))]
    private double _pausedTime;
    [ObservableProperty] private string _runTimeFormatted = "";
    [ObservableProperty] private string _alarmTimeFormatted = "";
    [ObservableProperty] private string _pausedTimeFormatted = "";
    [ObservableProperty] private string _runTimeFullFormatted = "";
    [ObservableProperty] private string _alarmTimeFullFormatted = "";
    [ObservableProperty] private string _pausedTimeFullFormatted = "";
    [ObservableProperty] private string _totalTimeFullFormatted = "";
    [ObservableProperty] private PlotModel? _statusPieChart;
    /// <summary>设备状态卡三根立体柱图（OxyPlot ColumnSeries）。</summary>
    [ObservableProperty] private PlotModel? _statusColumnChart;

    // ──────────── 设备健康分 ────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceHealthScoreText))]
    private double _deviceHealthScore;
    [ObservableProperty] private string _deviceHealthLevel = "—";
    [ObservableProperty] private Brush _deviceHealthBrush = Brushes.Gray;
    /// <summary>设备健康分展示文本（0-100），无有效数据时显示 "—"。</summary>
    public string DeviceHealthScoreText => DeviceHealthScore <= 0 ? "—" : $"{DeviceHealthScore:0}";

    /// <summary>运行时长占比 = RunTime / (Run+Alarm+Paused)。总时长为 0 时返回 0。口径见 SnapshotMetrics。</summary>
    public double RunTimeRatio => SnapshotMetrics.TimeRatio(RunTime, RunTime, AlarmTime, PausedTime);
    /// <summary>报警时长占比</summary>
    public double AlarmTimeRatio => SnapshotMetrics.TimeRatio(AlarmTime, RunTime, AlarmTime, PausedTime);
    /// <summary>待机时长占比</summary>
    public double PausedTimeRatio => SnapshotMetrics.TimeRatio(PausedTime, RunTime, AlarmTime, PausedTime);

    /// <summary>状态总时长（运行+报警+暂停）格式化文本，用于状态饼图中心叠加显示。</summary>
    public string TotalTimeFormatted => FormatHelper.FormatDuration(RunTime + AlarmTime + PausedTime);

    // ──────────── 第 2 行 列 3：设备缺陷图表 ────────────

    [ObservableProperty] private PlotModel? _defectBarChart;

    // ──────────── 第 1 行 列 3：实时故障 ────────────

    public ObservableCollection<ActiveAlarmInfo> ActiveAlarms { get; } = new();

    /// <summary>
    /// 是否存在 High 级别活跃报警（用于标题徽章红色提示）。
    /// </summary>
    [ObservableProperty] private bool _hasHighLevelAlarm;

    // ──────────── 第 2 行 列 2：合格率概览 ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CycleDiffText))]
    [NotifyPropertyChangedFor(nameof(IsCycleSlow))]
    private double _targetCycleSec;
    [ObservableProperty] private PlotModel? _qualityPieChart;

    [ObservableProperty] private HomeDataStatus _dataStatusKind = HomeDataStatus.NoDevice;

    public string DataStatusText => DataStatusKind switch
    {
        HomeDataStatus.Disconnected => _runtimeMode.IsRemote ? Strings.M070 : Strings.M071,
        HomeDataStatus.NoData => Strings.M072,
        HomeDataStatus.Live => Strings.K083,
        _ => Strings.K144,
    };

    public string DataStatusTooltip => DataStatusKind switch
    {
        HomeDataStatus.Disconnected => _runtimeMode.IsRemote
            ? Strings.M050
            : Strings.K341,
        HomeDataStatus.NoData => Strings.K051,
        HomeDataStatus.Live => Strings.K083,
        _ => Strings.K144,
    };

    private bool CanDisplayKpiData => DataStatusKind == HomeDataStatus.Live;
    public string OeeDisplay => CanDisplayKpiData ? $"{OeeValue:P0}" : "—";
    public string AvailabilityRateDisplay => CanDisplayKpiData ? $"{AvailabilityRate:P0}" : "—";
    public string PerformanceRateDisplay => CanDisplayKpiData ? $"{PerformanceRate:P0}" : "—";
    public string QualityRateDisplay => CanDisplayKpiData ? $"{QualityRate:P0}" : "—";

    /// <summary>
    /// 距目标差距文本（2026-08-11 用户选定）：纯数据格式 "+0.6%"（超目标）/ "-0.5%"（还差），
    /// 颜色由 QualityThresholdConverter 表达（≥95% 绿 / 未达红）；不新增 resx key。
    /// </summary>
    public string QualityGapText => CanDisplayKpiData ? FormatQualityGap(QualityRate) : "—";

    /// <summary>距目标差距格式化：正=超目标 "+x.x%"，负=差距 "-x.x%"，0="+0.0%"。</summary>
    internal static string FormatQualityGap(double qualityRate)
        => $"{qualityRate - KpiThresholds.QualityGood:+#0.0%;-#0.0%;+0.0%}";
    public string RealtimeSpeedDisplay => CanDisplayKpiData ? $"{RealtimeSpeed:N0}" : "—";
    public string TotalOutputDisplay => CanDisplayKpiData ? $"{TotalOutput:N0}" : "—";
    public string TotalOkProductionDisplay => CanDisplayKpiData ? string.Format(Strings.F030, TotalOkProduction) : "—";
    public string TotalNgProductionDisplay => CanDisplayKpiData ? string.Format(Strings.F028, TotalNgProduction) : "—";
    public string NgRateDisplay => CanDisplayKpiData ? $"{NgRate:P2}" : "—";
    public string ShiftOkProductionDisplay => CanDisplayKpiData ? $"{TotalOkProduction:N0}" : "—";
    public string ShiftNgProductionDisplay => CanDisplayKpiData ? $"{TotalNgProduction:N0}" : "—";
    public string TargetCycleDisplay => TargetCycleSec > 0 ? $"{TargetCycleSec:F2}s" : "—";
    public string ActualCycleDisplay => CanDisplayKpiData && ActualCycleSec > 0 ? $"{ActualCycleSec:F2}s" : "—";

    /// <summary>
    /// PLC 连接管理器：暴露给 UI 绑定连接状态指示器
    /// </summary>
    public IPlcConnectionManager ConnectionManager => _connectionManager;

    // ──────────── 实时故障：静音 + 级别筛选 ────────────

    /// <summary>
    /// 故障静音开关：true 时新报警不再触发闪烁高亮（仅抑制视觉动画，列表照常更新）。
    /// 用于"已查看"场景，避免持续闪烁干扰值班人员。
    /// </summary>
    [ObservableProperty] private bool _isAlarmMuted;

    /// <summary>是否显示 High 级别报警（默认 true，UI 筛选用）。</summary>
    [ObservableProperty] private bool _showHighAlarms = true;
    /// <summary>是否显示 Medium 级别报警。</summary>
    [ObservableProperty] private bool _showMediumAlarms = true;
    /// <summary>是否显示 Low 级别报警。</summary>
    [ObservableProperty] private bool _showLowAlarms = true;

    /// <summary>
    /// 故障列表过滤视图：基于 ShowHighAlarms/ShowMediumAlarms/ShowLowAlarms 过滤 ActiveAlarms。
    /// UI 绑定此视图而非 ActiveAlarms 本身，便于按级别筛选；计数徽章仍绑定 ActiveAlarms.Count 显示总数。
    /// </summary>
    public ICollectionView FilteredActiveAlarms { get; }

    /// <summary>
    /// 总产量 = OK + NG（会话累计，用于当前生产状态卡片）。口径见 SnapshotMetrics（与 WASM 共用）。
    /// </summary>
    public int TotalOutput => SnapshotMetrics.TotalOutput(TotalOkProduction, TotalNgProduction);

    /// <summary>
    /// 不良率 = NG / 总产量。无产量时返回 0，用于合格率卡片高密度展示。口径见 SnapshotMetrics。
    /// </summary>
    public double NgRate => SnapshotMetrics.NgRate(TotalOkProduction, TotalNgProduction);

    /// <summary>
    /// 实际节拍（秒/件）= 3600 / 当前速度。速度为 0 时返回 0，UI 显示 "—"。
    /// 与目标节拍对比，直观反映当前快慢。口径见 SnapshotMetrics。
    /// </summary>
    public double ActualCycleSec => SnapshotMetrics.CycleSeconds(RealtimeSpeed);

    /// <summary>
    /// 实际节拍与目标节拍的差值文本：无数据返回空，达标返回"● 达标"，
    /// 否则"▲ 快 Xs"（实际更快/每件耗时更短）/"▼ 慢 Xs"（实际更慢/每件耗时更长）。
    /// </summary>
    public string CycleDiffText
    {
        get
        {
            if (TargetCycleSec <= 0 || ActualCycleSec <= 0) return "";
            var diff = ActualCycleSec - TargetCycleSec;
            if (Math.Abs(diff) < 0.01) return Strings.M012;
            return diff > 0 ? string.Format(Strings.F047, diff) : string.Format(Strings.F046, Math.Abs(diff));
        }
    }

    /// <summary>实际节拍是否慢于目标节拍（用于 UI 红色警示，快或达标为绿色）。</summary>
    public bool IsCycleSlow => TargetCycleSec > 0 && ActualCycleSec > 0 && ActualCycleSec > TargetCycleSec;

    public HomeViewModel(IDeviceRepository deviceRepo, IPlcConnectionManager connectionManager, AppSettings appSettings, IPlcDataAcquisitionService plcService, IDeviceSelectionService selection, IWorkOrderRepository? workOrderRepo = null, IDialogService? dialog = null, IWorkOrderService? workOrderService = null, IRuntimeMode? runtimeMode = null, Kanban.Collector.Core.Services.ProductionHistoryStore? historyStore = null, RemoteRuntimeSink? remoteRuntimeSink = null)
    {
        _deviceRepository = deviceRepo;
        _connectionManager = connectionManager;
        _appSettings = appSettings;
        _selection = selection;
        _plcService = plcService;
        _remoteRuntimeSink = remoteRuntimeSink;
        _workOrderRepo = workOrderRepo;
        _dialog = dialog;
        _workOrderService = workOrderService;
        _runtimeMode = runtimeMode ?? new RuntimeMode(appSettings);
        _shiftProgress = new ShiftProgressProvider(appSettings);
        _lastShiftProvider = new LastShiftComparisonProvider(plcService, _runtimeMode, historyStore, appSettings);

        RefreshDeviceFilterItems();
        // 使用命名方法而非 lambda，确保 Dispose 时能正确取消订阅（lambda 每次创建新委托实例，-= 不生效）
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
        _deviceSnapshot = _deviceRepository.Devices.ToList();
        _selection.PropertyChanged += OnSelectionServiceChanged;
        _connectionManager.PropertyChanged += OnConnectionManagerChanged;

        if (_deviceRepository.Devices.Count > 0)
            _selection.SelectedDeviceId = _deviceRepository.Devices[0].Id;

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_appSettings.DashboardRefreshIntervalMs) };
        _liveTimer.Tick += OnLiveTimerTick;

        // 故障列表过滤视图：基于 ShowHighAlarms/Medium/Low 三态筛选
        FilteredActiveAlarms = CollectionViewSource.GetDefaultView(ActiveAlarms);
        FilteredActiveAlarms.Filter = item => item is ActiveAlarmInfo a && IsLevelVisible(a.Level);
    }

    public void OnPageEnter()
    {
        if (_liveTimer.IsEnabled) return;
        // 兜底回退选中设备：主页进入时若设备已加载但未选中（Remote 模式设备异步到达的窗口期、
        // 设备集合重建等场景），自动选中第一台，避免主页停留在"未选择设备"空状态。
        // 设备未加载（Remote 拉取尚未完成）时不动作——由 OnDevicesCollectionChanged 在拉取完成后回退。
        if (string.IsNullOrEmpty(SelectedDeviceId) && _deviceRepository.Devices.Count > 0)
            SelectedDeviceId = _deviceRepository.Devices[0].Id;
        SyncRuntime();
        _liveTimer.Start();
    }

    public void OnPageExit() => _liveTimer.Stop();

    /// <summary>
    /// 判断指定级别是否在当前筛选范围内（用于 FilteredActiveAlarms 过滤谓词）。
    /// </summary>
    private bool IsLevelVisible(AlarmLevel level) => level switch
    {
        AlarmLevel.High => ShowHighAlarms,
        AlarmLevel.Medium => ShowMediumAlarms,
        AlarmLevel.Low => ShowLowAlarms,
        _ => true
    };

    // 级别筛选切换时刷新过滤视图
    partial void OnShowHighAlarmsChanged(bool value) => FilteredActiveAlarms.Refresh();
    partial void OnShowMediumAlarmsChanged(bool value) => FilteredActiveAlarms.Refresh();
    partial void OnShowLowAlarmsChanged(bool value) => FilteredActiveAlarms.Refresh();

    /// <summary>切换故障静音开关（UI 按钮命令）。</summary>
    [RelayCommand]
    private void ToggleAlarmMute() => IsAlarmMuted = !IsAlarmMuted;

    /// <summary>
    /// 查看当前选中设备的详情：写入选中服务并触发跳转请求。
    /// MainWindowViewModel 订阅 ViewDeviceDetailRequested 后将 SelectedIndex 切到 7（设备详情页）。
    /// 复用 ProductionLineViewModel/OverviewViewModel 的 FocusDeviceRequested 同款模式。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanViewDeviceDetail))]
    private void ViewDeviceDetail()
    {
        if (string.IsNullOrEmpty(SelectedDeviceId)) return;
        // 写回共享服务（值相等时不触发 PropertyChanged，避免回环）
        if (_selection.SelectedDeviceId != SelectedDeviceId)
            _selection.SelectedDeviceId = SelectedDeviceId;
        ViewDeviceDetailRequested?.Invoke(SelectedDeviceId);
    }

    private bool CanViewDeviceDetail() => !string.IsNullOrEmpty(SelectedDeviceId);

    /// <summary>跳转设备详情页请求事件（MainWindowViewModel 订阅）。</summary>
    public event Action<string>? ViewDeviceDetailRequested;

    /// <summary>跳转工单管理页请求事件（MainWindowViewModel 订阅）。</summary>
    public event Action? ViewWorkOrderManagerRequested;

    /// <summary>
    /// 编辑当前工单：弹出工单编辑对话框。无当前工单时弹出新增对话框。
    /// 对话框确认后通过 Repository 落库并刷新 CurrentWorkOrder。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkOrder))]
    private async Task EditWorkOrderAsync()
    {
        if (_workOrderRepo == null || _dialog == null || _workOrderService == null) return;
        var saved = CurrentWorkOrder == null
            ? await _workOrderService.AddWorkOrderAsync()
            : await _workOrderService.EditWorkOrderAsync(CurrentWorkOrder);
        if (saved == null) return;
        RefreshCurrentWorkOrder();
    }

    private bool CanEditWorkOrder() => _workOrderRepo != null && _dialog != null;

    /// <summary>跳转到工单管理页（顶部栏按钮命令）。</summary>
    [RelayCommand]
    private void ViewWorkOrderManager()
    {
        ViewWorkOrderManagerRequested?.Invoke();
    }

    /// <summary>
    /// 刷新当前工单：按选中设备查询 Running 工单，无则取最新 Pending。
    /// 由 SyncRuntime 每 tick 调用（轻量：内存集合 FirstOrDefault）。
    /// </summary>
    private void RefreshCurrentWorkOrder()
    {
        if (_workOrderRepo == null || string.IsNullOrEmpty(SelectedDeviceId))
        {
            CurrentWorkOrder = null;
            return;
        }
        var running = _workOrderRepo.GetRunningByDevice(SelectedDeviceId);
        if (running != null)
        {
            CurrentWorkOrder = running;
            return;
        }
        // 无 Running 时回退显示最新 Pending（仅在工单条显示，不计入进度）
        var pending = _workOrderRepo.GetLatestPendingByDevice(SelectedDeviceId);
        CurrentWorkOrder = pending;
    }

    partial void OnCurrentWorkOrderChanged(WorkOrder? value)
    {
        // 工单变化时取消旧查询，避免 Remote 模式继续占用 SignalR 请求；
        // 旧测试替身没有异步能力时仍由单飞标志保证不会并发执行。
        if (Volatile.Read(ref _workOrderSummaryRunning) != 0)
            Volatile.Write(ref _workOrderSummaryRefreshRequested, 1);
        CancelWorkOrderSummaryQuery();
        _lastSummaryOrderId = null;
        _lastSummaryQueryUtc = DateTime.MinValue;
        _currentWorkOrderOk = 0;
        // 达标提示记录修剪：只保留当前工单（若有），避免长期运行集合无限增长
        _notifiedWorkOrderIds.RemoveWhere(id => value == null || id != value.Id);
        NotifyWorkOrderProgress();
        RefreshWorkOrderSummary();
    }

    /// <summary>
    /// 查询当前 Running 工单的「工单内 OK 产量」并回填进度。
    /// 走 IWorkOrderService.GetProductionSummary（按工单时间窗口差分），而非会话累计 TotalOkProduction，
    /// 避免连续多个工单时第二个工单一开工进度即满/误弹达标。
    /// 后台查询 + 2s 节流（Remote 模式通过可取消异步接口执行 SignalR 请求）。
    /// </summary>
    private void RefreshWorkOrderSummary()
    {
        var order = CurrentWorkOrder;
        if (order == null || order.Status != WorkOrderStatus.Running)
        {
            _currentWorkOrderOk = 0;
            NotifyWorkOrderProgress();
            return;
        }

        if (_workOrderService == null) return;

        // 节流：同一 Running 工单 2s 内复用上次结果
        if (_lastSummaryOrderId == order.Id && DateTime.UtcNow - _lastSummaryQueryUtc < WorkOrderSummaryThrottle)
            return;

        if (Interlocked.CompareExchange(ref _workOrderSummaryRunning, 1, 0) != 0)
        {
            // 单飞：同步/Remote 查询本身暂不支持取消，禁止同一主页并发堆积多个查询。
            Volatile.Write(ref _workOrderSummaryRefreshRequested, 1);
            return;
        }

        _lastSummaryOrderId = order.Id;
        _lastSummaryQueryUtc = DateTime.UtcNow;
        var queryCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        lock (_workOrderSummaryGate)
            _workOrderSummaryCts = queryCts;
        var cancellationToken = queryCts.Token;

        Task.Run(async () =>
        {
            try
            {
                var ok = _workOrderService is IAsyncWorkOrderProductionSummary asyncService
                    ? (await asyncService.GetProductionSummaryAsync(order, cancellationToken).ConfigureAwait(false)).OkCount
                    : _workOrderService.GetProductionSummary(order).OkCount;
                if (!cancellationToken.IsCancellationRequested)
                    UiDispatcher.Dispatch(() => ApplyWorkOrderSummary(order, ok));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 工单切换或页面销毁属于正常取消，不记录为查询故障。
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "查询工单 {OrderNo} 产量聚合失败", order.OrderNo);
            }
            finally
            {
                lock (_workOrderSummaryGate)
                {
                    if (ReferenceEquals(_workOrderSummaryCts, queryCts))
                        _workOrderSummaryCts = null;
                    queryCts.Dispose();
                }
                Interlocked.Exchange(ref _workOrderSummaryRunning, 0);
                if (Interlocked.Exchange(ref _workOrderSummaryRefreshRequested, 0) != 0
                    && !_disposeCts.IsCancellationRequested)
                    UiDispatcher.Dispatch(RefreshWorkOrderSummary);
            }
        }).Forget();
    }

    private void CancelWorkOrderSummaryQuery()
    {
        lock (_workOrderSummaryGate)
            _workOrderSummaryCts?.Cancel();
    }

    private void ApplyWorkOrderSummary(WorkOrder order, int okCount)
    {
        // 工单已切换时丢弃过期结果
        if (CurrentWorkOrder?.Id != order.Id) return;
        _currentWorkOrderOk = okCount;
        NotifyWorkOrderProgress();
    }

    internal void ApplyWorkOrderSummaryForTest(WorkOrder order, int okCount)
        => ApplyWorkOrderSummary(order, okCount);

    internal void RefreshWorkOrderSummaryForTest()
        => RefreshWorkOrderSummary();

    private void NotifyWorkOrderProgress()
    {
        OnPropertyChanged(nameof(WorkOrderProgressText));
        OnPropertyChanged(nameof(WorkOrderProgressRatio));
        OnPropertyChanged(nameof(WorkOrderProgressPct));
        OnPropertyChanged(nameof(WorkOrderOkProductionDisplay));
    }

    partial void OnSelectedDeviceIdChanged(string? value)
    {
        // 切换设备：取消在途上班次回填查询，RefreshSelected 会重设速度与各项数据
        _lastShiftProvider.Cancel();
        RefreshSelected();
        // 本地选中变更写回共享服务（值相等时不触发 PropertyChanged，避免与 OnSelectionServiceChanged 互调形成回环）
        if (_selection.SelectedDeviceId != value)
            _selection.SelectedDeviceId = value;
    }

    /// <summary>
    /// 共享选中服务变更 → 同步本 VM 的 SelectedDeviceId（驱动主页刷新当前设备）。
    /// </summary>
    private void OnSelectionServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IDeviceSelectionService.SelectedDeviceId))
            SelectedDeviceId = _selection.SelectedDeviceId;
    }

    private void OnConnectionManagerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlcConnectionManager.IsConnected))
            UiDispatcher.Dispatch(RefreshDataStatus);
    }

    private void RefreshDataStatus()
    {
        var hasFreshData = HasFreshData();
        var newKind = string.IsNullOrEmpty(SelectedDeviceId)
            ? HomeDataStatus.NoDevice
            : !_connectionManager.IsConnected
                ? HomeDataStatus.Disconnected
                : CurrentRuntime == null || !hasFreshData ? HomeDataStatus.NoData : HomeDataStatus.Live;

        // 状态档位变化：通知档位文本 + 全部显示（CanDisplayKpiData 随档位翻转）
        if (newKind != _lastDataStatusKind)
        {
            _lastDataStatusKind = newKind;
            DataStatusKind = newKind;
            if (newKind != HomeDataStatus.Live)
            {
                CurrentWorkOrder = null;
                ClearLiveData();
            }
            NotifyAllDisplays();
            _lastDisplaySignature = null; // 强制下次签名重算
            return;
        }

        // 档位未变：仅当显示值实际变化时通知（审查修复 2026-08-13：
        // 原实现每 3s 无条件抛 12+ PropertyChanged——设备待机/无数据时值根本没变，全量空刷新）
        var hash = new HashCode();
        hash.Add(OeeDisplay);
        hash.Add(AvailabilityRateDisplay);
        hash.Add(PerformanceRateDisplay);
        hash.Add(QualityRateDisplay);
        hash.Add(RealtimeSpeedDisplay);
        hash.Add(TotalOutputDisplay);
        hash.Add(TotalOkProductionDisplay);
        hash.Add(TotalNgProductionDisplay);
        hash.Add(ShiftOkProductionDisplay);
        hash.Add(ShiftNgProductionDisplay);
        hash.Add(TargetCycleDisplay);
        hash.Add(ActualCycleDisplay);
        hash.Add(WorkOrderOkProductionDisplay);
        var signature = hash.ToHashCode();
        if (signature == _lastDisplaySignature) return;
        _lastDisplaySignature = signature;
        NotifyValueDisplays();
    }

    private bool HasFreshData()
    {
        if (_runtimeMode.IsRemote)
        {
            var receivedAt = _remoteRuntimeSink?.GetLastSnapshotReceivedAt(SelectedDeviceId ?? string.Empty) ?? default;
            return receivedAt != default && DateTime.Now - receivedAt <= DataFreshnessTimeout;
        }

        // 纯单元测试可不注入采集服务，此时用已存在的 Runtime 保持预览语义；
        // 生产路径由采集诊断的最近成功时间判定，避免连接存活但数据已停滞时继续显示 Live。
        if (_plcService is null) return CurrentRuntime is not null;
        var diagnostics = _plcService.GetDiagnosticsSnapshot();
        return diagnostics.LastSuccessfulAt is { } lastSuccessful
            && DateTime.Now - lastSuccessful <= DataFreshnessTimeout
            && (diagnostics.ConfiguredDevices == 0
                || (SelectedDeviceId is { } selectedId
                    && diagnostics.LastSuccessfulDeviceIds.Contains(selectedId)));
    }

    private HomeDataStatus? _lastDataStatusKind;
    private int? _lastDisplaySignature;

    private void NotifyAllDisplays()
    {
        OnPropertyChanged(nameof(DataStatusText));
        OnPropertyChanged(nameof(DataStatusTooltip));
        NotifyValueDisplays();
    }

    private void NotifyValueDisplays()
    {
        OnPropertyChanged(nameof(OeeDisplay));
        OnPropertyChanged(nameof(AvailabilityRateDisplay));
        OnPropertyChanged(nameof(PerformanceRateDisplay));
        OnPropertyChanged(nameof(QualityRateDisplay));
        OnPropertyChanged(nameof(RealtimeSpeedDisplay));
        OnPropertyChanged(nameof(TotalOutputDisplay));
        OnPropertyChanged(nameof(TotalOkProductionDisplay));
        OnPropertyChanged(nameof(TotalNgProductionDisplay));
        OnPropertyChanged(nameof(NgRateDisplay));
        OnPropertyChanged(nameof(ShiftOkProductionDisplay));
        OnPropertyChanged(nameof(ShiftNgProductionDisplay));
        OnPropertyChanged(nameof(TargetCycleDisplay));
        OnPropertyChanged(nameof(ActualCycleDisplay));
        OnPropertyChanged(nameof(WorkOrderOkProductionDisplay));
    }

    private void SyncRuntime()
    {
        if (string.IsNullOrEmpty(SelectedDeviceId))
        {
            RefreshDataStatus();
            return;
        }
        var deviceId = SelectedDeviceId!;
        if (!_deviceRepository.RuntimeMap.TryGetValue(deviceId, out var rt))
        {
            CurrentRuntime = null;
            ClearLiveData();
            RefreshCurrentWorkOrder();
            RefreshActiveAlarms();
            RefreshDataStatus();
            return;
        }
        if (!HasFreshData())
        {
            RefreshDataStatus();
            return;
        }

        // 当前速度（平均速度）= 实际总产量(OK+NG) / 运行时长（小时）
        // - 与 OEE 性能率口径一致：性能率 = 实际产量 / (目标节拍 × RunTime)
        // - 运行时长 RunTime 只在设备运行状态时累计，报警/待机期间不增加
        // - 这样报警/待机时速度保持不变（产量和运行时长都不变），避免归零
        // - RunTime 下限保护见 SnapshotMetrics（<5s 返回 0，避免启动失真）——与 WASM 端共用同一实现
        RealtimeSpeed = SnapshotMetrics.RealtimeSpeed(rt.RunTime, rt.TotalOkProduction, rt.TotalNgProduction);

        var now = DateTime.Now;
        ApplyRuntime(rt, CurrentDevice);
        // 仅数据变化时重建 OxyPlot（避免每 3 秒无意义 new PlotModel）
        var oeeInput = (Math.Round(AvailabilityRate, 3), Math.Round(PerformanceRate, 3), Math.Round(QualityRate, 3));
        if (oeeInput != _lastOeeInput) { BuildOeeRingCharts(); _lastOeeInput = oeeInput; }
        // 状态时长：5s 粒度桶 diff（审查修复 2026-08-13：原秒级 diff 使运行时每跨整秒重建一次饼图，
        // 即"几乎每 tick 重建"；5s 桶将重建频率降 5 倍且显示口径不变）
        var statusInput = ((int)(RunTime / 5), (int)(AlarmTime / 5), (int)(PausedTime / 5));
        if (statusInput != _lastStatusInput) { BuildStatusPieChart(); _lastStatusInput = statusInput; }
        var defectSig = DefectSignature(CurrentDevice);
        if (defectSig != _lastDefectSignature) { BuildDefectBarChart(); _lastDefectSignature = defectSig; }
        var qualityInput = (TotalOkProduction, TotalNgProduction);
        if (qualityInput != _lastQualityInput) { BuildQualityPieChart(); _lastQualityInput = qualityInput; }
        RefreshActiveAlarms();
        // 刷新所有活跃报警的持续时间文本（基于当前时间）
        foreach (var a in ActiveAlarms)
        {
            a.RefreshDuration(now);
            // 加入超过 30 秒后清除新报警高亮标志：3 秒过短（工人未及注意即停闪），
            // 永不停止会视觉疲劳；30 秒兼顾提醒效果与疲劳控制。
            if (a.IsNew && (now - a.AddedAt).TotalSeconds >= 30)
                a.IsNew = false;
        }
        UpdateShiftProgress();
        // 刷新上班次对比数据：PlcDataAcquisitionService 在班次切换时会更新 _lastShiftSummaries 缓存，
        // 此处每 tick 拾取（开销仅一次锁+字典查找），确保班次切换后 UI 立即反映新数据
        RefreshLastShiftComparison();
        // 刷新当前工单（轻量：内存集合 FirstOrDefault；工单切换/编辑后立即反映）
        RefreshCurrentWorkOrder();
        // 工单内 OK 产量：后台查询 + 2s 节流，回填进度（不可每 tick 同步查）
        RefreshWorkOrderSummary();
        // 产量达标提示：检查当前工单产量是否达到目标，达标时弹 Growl（每工单仅一次）
        CheckWorkOrderCompletionTarget();
        RefreshDataStatus();
    }

    /// <summary>
    /// 检查当前 Running 工单的累计产量是否达到目标产量。
    /// 达标时通过 Growl 弹出成功提示，并将工单 Id 加入 _notifiedWorkOrderIds 避免重复提示。
    /// 产量口径与 WorkOrderProgressText 一致：_currentWorkOrderOk（工单内 OK，非会话累计）。
    /// </summary>
    private void CheckWorkOrderCompletionTarget()
    {
        if (_dialog == null || CurrentWorkOrder == null) return;
        if (CurrentWorkOrder.Status != Kanban.Collector.Core.Entities.WorkOrderStatus.Running) return;
        if (_notifiedWorkOrderIds.Contains(CurrentWorkOrder.Id)) return;
        var produced = _currentWorkOrderOk;
        if (CurrentWorkOrder.TargetQuantity > 0 && produced >= CurrentWorkOrder.TargetQuantity)
        {
            _notifiedWorkOrderIds.Add(CurrentWorkOrder.Id);
            // 业务事件 INF 级日志（符合 project_memory 中"产量达标"属于业务事件的约定）
            Serilog.Log.Information("工单 {OrderNo} 产量已达标（{Produced} / {Target} 件）",
                CurrentWorkOrder.OrderNo, produced, CurrentWorkOrder.TargetQuantity);
            _dialog.NotifySuccess(string.Format(Strings.F093, CurrentWorkOrder.OrderNo, produced, CurrentWorkOrder.TargetQuantity));
        }
    }

    public void RefreshDeviceFilterItems()
        => DeviceFilterHelper.Refresh(DeviceFilterItems, _deviceRepository);

    private void RefreshSelected()
    {
        var deviceId = SelectedDeviceId;
        CurrentDevice = _deviceRepository.Devices.FirstOrDefault(d => d.Id == deviceId);
        CurrentRuntime = deviceId != null && _deviceRepository.RuntimeMap.TryGetValue(deviceId, out var runtime)
            ? runtime
            : null;

        if (CurrentRuntime != null && HasFreshData())
            ApplyRuntime(CurrentRuntime, CurrentDevice);
        else
            ClearLiveData();

        // 刷新上班次对比数据（切换设备时重新加载）
        RefreshLastShiftComparison();

        // 刷新当前工单（切换设备时立即拾取新设备工单）
        RefreshCurrentWorkOrder();

        // 重置 diff 缓存，强制下次 SyncRuntime 重建图表。
        // 不重置会导致新设备数据恰好等于旧设备缓存值时跳过重建，显示陈旧图表。
        _lastOeeInput = default;
        _lastStatusInput = default;
        _lastDefectSignature = 0;
        _lastQualityInput = default;

        if (CurrentRuntime != null && HasFreshData())
        {
            BuildOeeRingCharts();
            BuildStatusPieChart();
            BuildDefectBarChart();
            BuildQualityPieChart();
        }
        RefreshActiveAlarms();
        RefreshDataStatus();
    }

    /// <summary>
    /// 从采集服务读取当前设备的上班次产量汇总；无内存缓存时由 LastShiftComparisonProvider
    /// 在后台查询历史库回填（60 秒退避）。结果通过回调应用到可绑定属性。
    /// </summary>
    private void RefreshLastShiftComparison()
        => _lastShiftProvider.Refresh(SelectedDeviceId, snap =>
        {
            LastShiftName = snap.Name;
            LastShiftOk = snap.Ok;
            LastShiftNg = snap.Ng;
        });

    private void ApplyRuntime(DeviceRuntime rt, Device? dev)
    {
        AvailabilityRate = rt.AvailabilityRate;
        QualityRate = rt.QualityRate;
        PerformanceRate = rt.PerformanceRate;
        OeeValue = rt.Oee;
        RunTime = rt.RunTime;
        AlarmTime = rt.AlarmTime;
        PausedTime = rt.PausedTime;
        TotalOkProduction = rt.TotalOkProduction;
        TotalNgProduction = rt.TotalNgProduction;
        RealtimeStatus = rt.StatusWord;
        TargetCycleSec = SnapshotMetrics.CycleSeconds(dev?.TargetCycle ?? 0);
        if (dev != null) TargetSpeed = dev.TargetCycle;
        // 配方信息同步（设备状态卡显示当前生产型号）
        RecipeName = dev?.RecipeName ?? "";
        RecipeValue = dev?.RecipeValue ?? 0;
        // 限制在 [0,1] 避免超速或节拍过保守时显示 > 100%（ProgressBar Maximum=1）
        SpeedAchievementRate = SnapshotMetrics.AchievementRate(RealtimeSpeed, TargetSpeed);
        RunTimeFormatted = FormatHelper.FormatDuration(rt.RunTime);
        AlarmTimeFormatted = FormatHelper.FormatDuration(rt.AlarmTime);
        PausedTimeFormatted = FormatHelper.FormatDuration(rt.PausedTime);
        RunTimeFullFormatted = FormatHelper.FormatDurationFull(rt.RunTime);
        AlarmTimeFullFormatted = FormatHelper.FormatDurationFull(rt.AlarmTime);
        PausedTimeFullFormatted = FormatHelper.FormatDurationFull(rt.PausedTime);
        TotalTimeFullFormatted = FormatHelper.FormatDurationFull(rt.RunTime + rt.AlarmTime + rt.PausedTime);
        UpdateDeviceHealth();
        UpdateOeeFormulas(rt, dev);
    }

    /// <summary>
    /// 计算设备健康分：A/P/Q 三率 + 报警稳定性加权，0-100。
    /// 无有效运行数据时显示 "—"。
    /// </summary>
    private void UpdateDeviceHealth()
    {
        if (RunTime + AlarmTime + PausedTime <= 0)
        {
            DeviceHealthScore = 0;
            DeviceHealthLevel = "—";
            DeviceHealthBrush = Brushes.Gray;
            return;
        }

        var stability = Math.Clamp(1 - AlarmTimeRatio, 0, 1);
        var score = 100 * (0.30 * AvailabilityRate
                          + 0.20 * PerformanceRate
                          + 0.25 * QualityRate
                          + 0.25 * stability);
        DeviceHealthScore = Math.Clamp(score, 0, 100);

        if (DeviceHealthScore >= 85)
        {
            DeviceHealthLevel = "健康";
            DeviceHealthBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        }
        else if (DeviceHealthScore >= 70)
        {
            DeviceHealthLevel = "良好";
            DeviceHealthBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
        }
        else if (DeviceHealthScore >= 60)
        {
            DeviceHealthLevel = "关注";
            DeviceHealthBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        }
        else
        {
            DeviceHealthLevel = "异常";
            DeviceHealthBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        }
    }

    /// <summary>
    /// 更新 OEE 4 环图下方的计算式文本（显示计算所用的具体数字）。
    /// OEE = 可用率 × 性能率 × 合格率（显示三率乘积式）
    /// 可用率 = 运行时长 / (运行+报警)
    /// 性能率 = 实际产量 / 理论产量（理论 = 目标节拍 × 运行时长）
    /// 合格率 = OK / (OK+NG)
    /// </summary>
    private void UpdateOeeFormulas(DeviceRuntime rt, Device? dev)
    {
        OeeFormulaText = $"{AvailabilityRate:P0} × {PerformanceRate:P0} × {QualityRate:P0}";

        var totalAvailSecs = rt.RunTime + rt.AlarmTime;
        AvailabilityFormulaText = totalAvailSecs > 0
            ? $"{FormatHelper.FormatDuration(rt.RunTime)} / {FormatHelper.FormatDuration(totalAvailSecs)}"
            : "— / —";

        if (dev != null && dev.TargetCycle > 0 && rt.RunTime > 0)
        {
            var idealOutput = dev.TargetCycle * (rt.RunTime / 3600.0);
            // 拆分显示 OK/NG，让用户直观看到 NG 也计入性能率分子（总产量口径）
            PerformanceFormulaText = string.Format(Strings.F037, rt.TotalOkProduction, rt.TotalNgProduction, idealOutput);
        }
        else
        {
            PerformanceFormulaText = "— / —";
        }

        var totalOutput = rt.TotalOkProduction + rt.TotalNgProduction;
        QualityFormulaText = totalOutput > 0
            ? string.Format(Strings.F036, rt.TotalOkProduction, totalOutput)
            : "— / —";
    }

    private void ClearLiveData()
    {
        OeeValue = 0; QualityRate = 0; PerformanceRate = 0; AvailabilityRate = 0;
        RunTime = 0; AlarmTime = 0; PausedTime = 0;
        TotalOkProduction = 0; TotalNgProduction = 0;
        TargetSpeed = 0; RealtimeStatus = (int)DeviceStatus.Unknown;
        RealtimeSpeed = 0; SpeedAchievementRate = 0; TargetCycleSec = 0;
        RecipeName = ""; RecipeValue = 0;
        RunTimeFormatted = ""; AlarmTimeFormatted = ""; PausedTimeFormatted = "";
        RunTimeFullFormatted = ""; AlarmTimeFullFormatted = ""; PausedTimeFullFormatted = ""; TotalTimeFullFormatted = "";
        DeviceHealthScore = 0; DeviceHealthLevel = "—"; DeviceHealthBrush = Brushes.Gray;
        OeeFormulaText = ""; AvailabilityFormulaText = "";
        PerformanceFormulaText = ""; QualityFormulaText = "";
        ActiveAlarms.Clear();
        HasHighLevelAlarm = false;
        // 断线/无数据时不保留旧图表，交给各卡片的空状态显示，避免旧数据继续误导。
        _lastOeeInput = default; _lastStatusInput = default; _lastDefectSignature = 0; _lastQualityInput = default;
        OeeRingChart = null;
        AvailabilityRingChart = null;
        PerformanceRingChart = null;
        QualityRingChart = null;
        StatusPieChart = null;
        StatusColumnChart = null;
        QualityPieChart = null;
        DefectBarChart = null;
    }


    private void BuildStatusPieChart()
    {
        StatusPieChart = ChartService.BuildStatusPieChart(RunTime, AlarmTime, PausedTime);
        StatusColumnChart = ChartService.BuildStatusColumnChart(RunTime, AlarmTime, PausedTime);
    }

    private void BuildOeeRingCharts()
    {
        OeeRingChart = ChartService.BuildOeeRing(OeeValue);
        AvailabilityRingChart = ChartService.BuildAvailabilityRing(AvailabilityRate);
        PerformanceRingChart = ChartService.BuildPerformanceRing(PerformanceRate);
        QualityRingChart = ChartService.BuildQualityRing(QualityRate);
    }

    private void BuildQualityPieChart()
        => QualityPieChart = ChartService.BuildQualityPieChart(TotalOkProduction, TotalNgProduction);

    /// <summary>计算设备缺陷签名的哈希（仅名称 + 计数），避免每 tick 拼字符串。</summary>
    private static int DefectSignature(Device? device)
    {
        if (device == null) return 0;
        var hash = new HashCode();
        foreach (var d in device.Defects)
        {
            hash.Add(d.Name);
            hash.Add(d.Count);
        }
        return hash.ToHashCode();
    }

    private void BuildDefectBarChart()
    {
        if (CurrentDevice == null)
        {
            DefectBarChart = null;
            return;
        }
        // 快照 Defects：避免 ChartService 内部枚举时其他线程修改集合抛 InvalidOperationException
        var defectsSnapshot = CurrentDevice.Defects.ToList();
        DefectBarChart = ChartService.BuildDefectBarChart(
            defectsSnapshot.Select(d => (d.Name, d.Count)));
    }

    /// <summary>
    /// 刷新实时故障列表：收集所有设备的活跃报警。
    /// 使用差分更新避免全量 Clear+Add 导致列表闪烁。
    /// 计数报警无触发/恢复时间戳，用首次发现触发时刻作为 EventTime（缓存避免每 3 秒跳动）。
    /// 排序：级别降序（High→Medium→Low）+ 触发时间升序（近的在前）。
    /// 关键：复用 ActiveAlarms 中已有的实例（基于 Equals 匹配），避免每次 new 新实例
    ///       导致 ReferenceEquals 永远 false、排序 Move 失效、IsNew 标志错乱。
    /// </summary>
    private void RefreshActiveAlarms()
    {
        if (string.IsNullOrEmpty(SelectedDeviceId))
        {
            ActiveAlarms.Clear();
            HasHighLevelAlarm = false;
            return;
        }

        HasHighLevelAlarm = _alarmCollector.Refresh(
            ActiveAlarms, _deviceSnapshot, DateTime.Now, IsAlarmMuted, MaxHomeActiveAlarms, SelectedDeviceId);
    }

    private void OnDevicesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Devices 可能被后台线程（DeviceManagerViewModel.RemoveDevice 等）修改，
        // CollectionChanged 会在修改方所在线程触发。此处操作绑定的 ObservableCollection
        // 和 SelectedDeviceId（触发 OnSelectedDeviceIdChanged → BuildXxxCharts 等 UI 操作），
        // 必须切回 UI 线程，否则会抛跨线程 InvalidOperationException；无 Dispatcher 时同步执行。
        UiDispatcher.Dispatch(() =>
        {
            _deviceSnapshot = _deviceRepository.Devices.ToList();
            RefreshDeviceFilterItems();
            // 主页必须有选中设备：设备集合重建（Remote 配置拉取 ReplaceAll / 删除设备）时，
            // ComboBox 的 SelectedValue 双向绑定会因 DeviceFilterItems 清空而把 SelectedDeviceId
            // 置为 null；FallbackSelected 对 null 原样返回（保留"全部设备"语义给历史查询页共用），
            // 故此处先回退第一台，避免主页停留在"未选中设备"空状态。
            if (string.IsNullOrEmpty(SelectedDeviceId))
                SelectedDeviceId = _deviceRepository.Devices.Count > 0 ? _deviceRepository.Devices[0].Id : null;
            else
                SelectedDeviceId = DeviceFilterHelper.FallbackSelected(_deviceRepository, SelectedDeviceId);
            RefreshActiveAlarms();
            RefreshDataStatus();
        });
    }

    private void OnLiveTimerTick(object? sender, EventArgs e)
        => SyncRuntime();

    private void UpdateShiftProgress()
    {
        var now = DateTime.Now;
        var snap = _shiftProgress.Compute(now);
        IsShiftProgressVisible = snap.IsVisible;
        ShiftProgressName = snap.Name;
        ShiftProgressText = snap.Text;
        ShiftProgressRatio = snap.Ratio;
        ShiftProgressPct = snap.Pct;

        // 设备状态卡右上角：当前班次 + 日期时钟
        var shift = ShiftConfigResolver.ResolveCurrentShift(_appSettings.Shifts, now);
        DeviceStatusShiftTag = shift.Shift == null
            ? string.Empty
            : $"{shift.Shift.Name} {shift.Start:hh\\:mm}-{shift.End:hh\\:mm}";
        DeviceStatusClock = now.ToString("MM-dd HH:mm");
    }

    public void Dispose()
    {
        _liveTimer?.Stop();
        CancelWorkOrderSummaryQuery();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _lastShiftProvider.Dispose();
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _selection.PropertyChanged -= OnSelectionServiceChanged;
        _connectionManager.PropertyChanged -= OnConnectionManagerChanged;
    }
}
