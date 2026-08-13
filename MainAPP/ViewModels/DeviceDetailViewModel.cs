using System.Collections.ObjectModel;
using MainAPP.Resources;
using System.ComponentModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging;
using OxyPlot;

namespace MainAPP.ViewModels;
/// 设备详情页视图模型：单设备深度监控视图。
///  - DeviceRepository.Devices/Runtimes：实时运行时数据与设备配置
///  - IHistoryService.QueryAlarmEvents：历史报警事件
///  - IDeviceSelectionService.SelectedDeviceId：当前选中设备（跨页同步）
/// 不引入定时器：实时数据通过 DeviceRuntime.PropertyChanged 推送，历史数据通过手动刷新命令拉取。
/// </summary>
public partial class DeviceDetailViewModel : ObservableObject, IDisposable
{
    private readonly DeviceRepository _deviceRepository;
    private readonly IHistoryService _historyService;
    private readonly IDeviceSelectionService _selection;
    private readonly IDialogService _dialog;
    private readonly ILogger<DeviceDetailViewModel> _logger;
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly IWorkOrderService _workOrderService;

    private Device? _currentDevice;
    private DeviceRuntime? _currentRuntime;

    /// <summary>
    /// 最近报警查询的取消令牌：每次新查询前取消旧令牌，避免快速切换设备时
    /// 旧查询的后台线程覆盖新数据（竞态导致显示过期数据）。
    /// </summary>
    private CancellationTokenSource? _recentAlarmsCts;

    /// <summary>产量趋势/工单产量聚合查询的取消令牌（与报警查询独立，避免互相取消）。</summary>
    private CancellationTokenSource? _productionCts;

    /// <summary>工单产量聚合查询的取消令牌：设备切换/Dispose 时取消，防止旧设备结果覆盖（审查修复 2026-08-13）。</summary>
    private CancellationTokenSource? _workOrderSummaryCts;

    /// <summary>工单产量聚合节流：上次查询时刻与工单 Id（采集周期每秒触发，Remote 模式每次查询是一次 SignalR 往返）。</summary>
    private DateTime _lastSummaryQueryUtc = DateTime.MinValue;
    private int? _lastSummaryOrderId;

    public DeviceDetailViewModel(
        DeviceRepository deviceRepository,
        IHistoryService historyService,
        IDeviceSelectionService selection,
        IDialogService dialog,
        ILogger<DeviceDetailViewModel> logger,
        WorkOrderRepository workOrderRepo,
        IWorkOrderService workOrderService)
    {
        _deviceRepository = deviceRepository;
        _historyService = historyService;
        _selection = selection;
        _dialog = dialog;
        _logger = logger;
        _workOrderRepo = workOrderRepo;
        _workOrderService = workOrderService;

        _selection.PropertyChanged += OnSelectionServiceChanged;
        // 首次加载：尝试用共享选中设备初始化
        ApplySelectedDevice(_selection.SelectedDeviceId);
    }

    // ──────────── 当前设备 ────────────

    /// <summary>当前选中设备（配置数据）。</summary>
    public Device? CurrentDevice
    {
        get => _currentDevice;
        private set
        {
            if (_currentDevice != null)
            {
                _currentDevice.PropertyChanged -= OnDevicePropertyChanged;
                _currentDevice.Alarms.CollectionChanged -= OnAlarmsCollectionChanged;
                _currentDevice.Defects.CollectionChanged -= OnDefectsCollectionChanged;
                _currentDevice.CountAlarms.CollectionChanged -= OnCountAlarmsCollectionChanged;
                foreach (var alarm in _currentDevice.Alarms)
                    alarm.PropertyChanged -= OnAlarmPropertyChanged;
                foreach (var defect in _currentDevice.Defects)
                    defect.PropertyChanged -= OnDefectPropertyChanged;
                foreach (var alarm in _currentDevice.CountAlarms)
                    alarm.PropertyChanged -= OnCountAlarmPropertyChanged;
            }
            SetProperty(ref _currentDevice, value);
            if (value != null)
            {
                value.PropertyChanged += OnDevicePropertyChanged;
                value.Alarms.CollectionChanged += OnAlarmsCollectionChanged;
                value.Defects.CollectionChanged += OnDefectsCollectionChanged;
                value.CountAlarms.CollectionChanged += OnCountAlarmsCollectionChanged;
                foreach (var alarm in value.Alarms)
                    alarm.PropertyChanged += OnAlarmPropertyChanged;
                foreach (var defect in value.Defects)
                    defect.PropertyChanged += OnDefectPropertyChanged;
                foreach (var alarm in value.CountAlarms)
                    alarm.PropertyChanged += OnCountAlarmPropertyChanged;
            }
        }
    }

    /// <summary>当前设备运行时（实时数据）。</summary>
    public DeviceRuntime? CurrentRuntime
    {
        get => _currentRuntime;
        private set
        {
            if (_currentRuntime != null)
                _currentRuntime.PropertyChanged -= OnRuntimePropertyChanged;
            SetProperty(ref _currentRuntime, value);
            if (value != null)
                value.PropertyChanged += OnRuntimePropertyChanged;
        }
    }

    /// <summary>是否有选中的设备（无设备时显示空状态）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDevice))]
    private bool _hasDevice;

    public bool HasNoDevice => !HasDevice;

    // ──────────── KPI ────────────

    [ObservableProperty] private int _totalOk;
    [ObservableProperty] private int _totalNg;
    [ObservableProperty] private double _qualityRate;
    [ObservableProperty] private double _oee;
    [ObservableProperty] private double _availabilityRate;
    [ObservableProperty] private double _performanceRate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDurationHours))]
    [NotifyPropertyChangedFor(nameof(RunTimeRatio))]
    [NotifyPropertyChangedFor(nameof(AlarmTimeRatio))]
    [NotifyPropertyChangedFor(nameof(PausedTimeRatio))]
    private double _runTimeHours;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDurationHours))]
    [NotifyPropertyChangedFor(nameof(RunTimeRatio))]
    [NotifyPropertyChangedFor(nameof(AlarmTimeRatio))]
    [NotifyPropertyChangedFor(nameof(PausedTimeRatio))]
    private double _alarmTimeHours;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalDurationHours))]
    [NotifyPropertyChangedFor(nameof(RunTimeRatio))]
    [NotifyPropertyChangedFor(nameof(AlarmTimeRatio))]
    [NotifyPropertyChangedFor(nameof(PausedTimeRatio))]
    private double _pausedTimeHours;
    [ObservableProperty] private int _todayAlarmCount;
    [ObservableProperty] private int _activeAlarmCount;
    [ObservableProperty] private int _actualCycle;
    [ObservableProperty] private string _statusText = Strings.Status_Initial;
    [ObservableProperty] private string _statusBrushKey = "StatusIdleBrush";
    [ObservableProperty] private string _dataScopeText = Strings.M063;
    [ObservableProperty] private string _refreshStatusText = Strings.M062;
    [ObservableProperty] private bool _isRefreshing;

    // ──────────── 设备配置（从 Device 读取） ────────────

    /// <summary>目标节拍（个/小时）。</summary>
    [ObservableProperty] private int _targetCycle;
    /// <summary>配方名称。</summary>
    [ObservableProperty] private string _recipeName = string.Empty;
    /// <summary>配方值。</summary>
    [ObservableProperty] private int _recipeValue;
    /// <summary>OK 计数 PLC 地址。</summary>
    [ObservableProperty] private string _okCountAddress = string.Empty;
    /// <summary>NG 计数 PLC 地址。</summary>
    [ObservableProperty] private string _ngCountAddress = string.Empty;
    /// <summary>状态字 PLC 地址。</summary>
    [ObservableProperty] private string _statusAddress = string.Empty;
    /// <summary>复位 PLC 地址。</summary>
    [ObservableProperty] private string _resetAddress = string.Empty;

    // ──────────── PLC 原始值（从 DeviceRuntime 读取） ────────────

    /// <summary>当前 OK 计数（PLC 原始值）。</summary>
    [ObservableProperty] private int _plcOkCount;
    /// <summary>当前 NG 计数（PLC 原始值）。</summary>
    [ObservableProperty] private int _plcNgCount;
    /// <summary>当前状态字（PLC 原始值）。</summary>
    [ObservableProperty] private int _plcStatusWord;

    // ──────────── 工单信息 ────────────

    /// <summary>当前是否有关联的活跃工单。</summary>
    [ObservableProperty] private bool _hasWorkOrder;
    /// <summary>工单号。</summary>
    [ObservableProperty] private string _workOrderNo = string.Empty;
    /// <summary>产品名称。</summary>
    [ObservableProperty] private string _productName = string.Empty;
    /// <summary>计划数量。</summary>
    [ObservableProperty] private int _plannedQuantity;
    /// <summary>已完成数量（OK 产量）。</summary>
    [ObservableProperty] private int _completedQuantity;
    /// <summary>工单状态文本（进行中/已完成/已中止）。</summary>
    [ObservableProperty] private string _workOrderStatusText = string.Empty;
    /// <summary>工单完成进度（0~1）。</summary>
    [ObservableProperty] private double _workOrderProgress;

    // ──────────── 节拍与理论产量对比 ────────────

    /// <summary>实际节拍（产量/运行时长，个/小时）。运行时长为 0 时为 0。</summary>
    [ObservableProperty] private double _actualCycleRate;
    /// <summary>节拍差距百分比（实际 vs 目标，负值表示未达标）。</summary>
    [ObservableProperty] private double _cycleGapPercent;
    /// <summary>理论产量（目标节拍 × 运行时长）。</summary>
    [ObservableProperty] private int _theoreticalOutput;
    /// <summary>实际总产量（OK+NG）。</summary>
    [ObservableProperty] private int _actualTotalOutput;

    // ──────────── 图表 ────────────

    /// <summary>按小时产量柱状图（最近 24 小时）。</summary>
    [ObservableProperty] private PlotModel? _hourlyProductionChart;
    /// <summary>缺陷占比饼图。</summary>
    [ObservableProperty] private PlotModel? _defectPieChart;
    [ObservableProperty] private int _hourlyRangeHours = 24;
    [ObservableProperty] private string _hourlyRangeText = Strings.K025;
    [ObservableProperty] private int _defectTotal;
    [ObservableProperty] private int _defectTypeCount;
    [ObservableProperty] private string _topDefectText = "—";

    // ──────────── 状态时长比例（用于堆叠条形图） ────────────

    /// <summary>总时长（运行+报警+待机），用于计算状态时长占比。无数据时为 0。</summary>
    public double TotalDurationHours => RunTimeHours + AlarmTimeHours + PausedTimeHours;
    /// <summary>运行时长占比（0~1）。无数据时为 0。</summary>
    public double RunTimeRatio => TotalDurationHours > 0 ? RunTimeHours / TotalDurationHours : 0;
    /// <summary>报警时长占比（0~1）。无数据时为 0。</summary>
    public double AlarmTimeRatio => TotalDurationHours > 0 ? AlarmTimeHours / TotalDurationHours : 0;
    /// <summary>待机时长占比（0~1）。无数据时为 0。</summary>
    public double PausedTimeRatio => TotalDurationHours > 0 ? PausedTimeHours / TotalDurationHours : 0;

    // ──────────── 列表数据 ────────────

    /// <summary>最近报警事件（从历史库查询）。</summary>
    public ObservableCollection<AlarmEventRecord> RecentAlarms { get; } = new();

    /// <summary>当前活跃报警（从设备配置筛选 IsActive=true）。</summary>
    public ObservableCollection<AlarmConfigRow> ActiveAlarms { get; } = new();

    /// <summary>最后刷新时间。</summary>
    [ObservableProperty] private DateTime _lastUpdateTime = DateTime.Now;

    // ──────────── 选中设备变更 ────────────

    private void OnSelectionServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IDeviceSelectionService.SelectedDeviceId))
            ApplySelectedDevice(_selection.SelectedDeviceId);
    }

    private void ApplySelectedDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            CurrentDevice = null;
            CurrentRuntime = null;
            HasDevice = false;
            ClearLists();
            ClearKpis();
            return;
        }

        var device = _deviceRepository.Devices.FirstOrDefault(d => d.Id == deviceId);
        var runtime = _deviceRepository.Runtimes.FirstOrDefault(r => r.DeviceId == deviceId);

        // 取消旧设备的在途工单聚合查询并重置节流，防旧结果覆盖新设备（审查修复 2026-08-13）
        _workOrderSummaryCts?.Cancel();
        _workOrderSummaryCts?.Dispose();
        _workOrderSummaryCts = null;
        _lastSummaryOrderId = null;
        _lastSummaryQueryUtc = DateTime.MinValue;

        CurrentDevice = device;
        CurrentRuntime = runtime;
        HasDevice = device != null;

        RefreshKpis();
        RefreshRecentAlarms();
    }

    /// <summary>清空所有集合数据（活跃报警 + 最近报警事件）。</summary>
    private void ClearLists()
    {
        ActiveAlarms.Clear();
        RecentAlarms.Clear();
        ActiveAlarmCount = 0;
        TodayAlarmCount = 0;
    }

    /// <summary>清空 KPI 数值（产量/率/时长/状态）。</summary>
    private void ClearKpis()
    {
        TotalOk = 0;
        TotalNg = 0;
        QualityRate = 0;
        Oee = 0;
        AvailabilityRate = 0;
        PerformanceRate = 0;
        RunTimeHours = 0;
        AlarmTimeHours = 0;
        PausedTimeHours = 0;
        ActualCycle = 0;
        StatusText = Strings.Status_Initial;
        StatusBrushKey = "StatusIdleBrush";

        // 设备配置
        TargetCycle = 0;
        RecipeName = string.Empty;
        RecipeValue = 0;
        OkCountAddress = string.Empty;
        NgCountAddress = string.Empty;
        StatusAddress = string.Empty;
        ResetAddress = string.Empty;

        // PLC 原始值
        PlcOkCount = 0;
        PlcNgCount = 0;
        PlcStatusWord = 0;

        // 工单
        HasWorkOrder = false;
        WorkOrderNo = string.Empty;
        ProductName = string.Empty;
        PlannedQuantity = 0;
        CompletedQuantity = 0;
        WorkOrderStatusText = string.Empty;
        WorkOrderProgress = 0;

        // 节拍与理论产量
        ActualCycleRate = 0;
        CycleGapPercent = 0;
        TheoreticalOutput = 0;
        ActualTotalOutput = 0;

        // 图表
        HourlyProductionChart = null;
        DefectPieChart = null;
    }

    // ──────────── 实时数据刷新 ────────────

    /// <summary>KPI 刷新已排程（脏标记合并：同一轮询周期内多次 Runtime 属性变更只排程一次全量刷新）。</summary>
    private bool _kpiRefreshScheduled;

    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Runtime 属性在 PLC 采集后台线程被修改，必须切回 UI 线程才能更新绑定集合
        // （ObservableCollection 不允许跨线程修改，否则抛 NotSupportedException）。
        // 每个采集周期每台设备触发 3~7 次属性变更：脏标记合并后只排程一次 RefreshKpis，
        // 避免 BeginInvoke 排队淹没 UI 线程（每次全刷含仓储查询 + 报警/缺陷集合重建）。
        if (_kpiRefreshScheduled) return;
        _kpiRefreshScheduled = true;
        DispatchOnUi(() =>
        {
            _kpiRefreshScheduled = false;
            RefreshKpis();
        });
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Device 配置变更（如修改报警级别、PLC地址）时刷新活跃报警列表
        DispatchOnUi(() =>
        {
            RefreshActiveAlarms();
            RefreshDefectChart();
        });
    }

    private void OnDefectsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (Defect defect in e.OldItems)
                defect.PropertyChanged -= OnDefectPropertyChanged;
        if (e.NewItems != null)
            foreach (Defect defect in e.NewItems)
                defect.PropertyChanged += OnDefectPropertyChanged;
        DispatchOnUi(RefreshDefectChart);
    }

    private void OnAlarmsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (Alarm alarm in e.OldItems)
                alarm.PropertyChanged -= OnAlarmPropertyChanged;
        if (e.NewItems != null)
            foreach (Alarm alarm in e.NewItems)
                alarm.PropertyChanged += OnAlarmPropertyChanged;
        DispatchOnUi(RefreshActiveAlarms);
    }

    private void OnAlarmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Alarm.StartTime) or nameof(Alarm.EndTime))
            DispatchOnUi(RefreshActiveAlarms);
    }

    private void OnDefectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Defect.Count) or nameof(Defect.Name))
            DispatchOnUi(RefreshDefectChart);
    }

    private void OnCountAlarmsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (CountAlarm alarm in e.OldItems)
                alarm.PropertyChanged -= OnCountAlarmPropertyChanged;
        if (e.NewItems != null)
            foreach (CountAlarm alarm in e.NewItems)
                alarm.PropertyChanged += OnCountAlarmPropertyChanged;
        DispatchOnUi(RefreshActiveAlarms);
    }

    private void OnCountAlarmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CountAlarm.CurrentValue) or nameof(CountAlarm.MaxValue)
            or nameof(CountAlarm.Enabled))
            DispatchOnUi(RefreshActiveAlarms);
    }

    /// <summary>
    /// 将操作切换到 UI 线程执行：runtime/device 属性变更来自 PLC 采集后台线程，
    /// 而 ObservableCollection 和 ObservableProperty 绑定的 UI 元素必须在调度线程访问。
    /// 应用关闭时 Dispatcher 可能已终止，故做空守卫。
    /// </summary>
    private static void DispatchOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// 释放事件订阅并取消挂起的后台查询。
    /// 构造时订阅了 IDeviceSelectionService.PropertyChanged，并在 CurrentDevice/CurrentRuntime
    /// setter 中订阅了 Device/DeviceRuntime 及其子集合的事件；释放时统一解绑，避免事件泄漏。
    /// 同时取消挂起的报警/产量查询，防止 Dispose 后回调写入已释放的资源。
    /// </summary>
    public void Dispose()
    {
        _selection.PropertyChanged -= OnSelectionServiceChanged;

        // 解绑当前设备及其子集合/子项的事件（与 CurrentDevice setter 的清理逻辑保持一致）
        if (_currentDevice != null)
        {
            _currentDevice.PropertyChanged -= OnDevicePropertyChanged;
            _currentDevice.Alarms.CollectionChanged -= OnAlarmsCollectionChanged;
            _currentDevice.Defects.CollectionChanged -= OnDefectsCollectionChanged;
            _currentDevice.CountAlarms.CollectionChanged -= OnCountAlarmsCollectionChanged;
            foreach (var alarm in _currentDevice.Alarms)
                alarm.PropertyChanged -= OnAlarmPropertyChanged;
            foreach (var defect in _currentDevice.Defects)
                defect.PropertyChanged -= OnDefectPropertyChanged;
            foreach (var alarm in _currentDevice.CountAlarms)
                alarm.PropertyChanged -= OnCountAlarmPropertyChanged;
        }

        if (_currentRuntime != null)
            _currentRuntime.PropertyChanged -= OnRuntimePropertyChanged;

        // 取消挂起的后台查询，避免回调访问已释放资源
        _recentAlarmsCts?.Cancel();
        _recentAlarmsCts?.Dispose();
        _productionCts?.Cancel();
        _productionCts?.Dispose();
        _workOrderSummaryCts?.Cancel();
        _workOrderSummaryCts?.Dispose();
    }

    /// <summary>刷新 KPI 与状态（从 Runtime 实时读取）。</summary>
    private void RefreshKpis()
    {
        var rt = CurrentRuntime;
        if (rt == null)
        {
            ClearKpis();
            ClearLists();
            return;
        }

        TotalOk = rt.TotalOkProduction;
        TotalNg = rt.TotalNgProduction;
        QualityRate = rt.QualityRate;
        Oee = rt.Oee;
        AvailabilityRate = rt.AvailabilityRate;
        PerformanceRate = rt.PerformanceRate;
        RunTimeHours = rt.RunTime;
        AlarmTimeHours = rt.AlarmTime;
        PausedTimeHours = rt.PausedTime;

        // PLC 原始值
        PlcOkCount = rt.OkProduction;
        PlcNgCount = rt.NgProduction;
        PlcStatusWord = rt.StatusWord;

        // 节拍与理论产量对比
        ActualTotalOutput = rt.TotalOkProduction + rt.TotalNgProduction;
        var runHours = rt.RunTime;
        ActualCycleRate = runHours > 0 ? ActualTotalOutput / runHours : 0;
        TheoreticalOutput = (int)Math.Round(rt.TargetCycle * runHours);
        CycleGapPercent = rt.TargetCycle > 0
            ? (ActualCycleRate - rt.TargetCycle) / rt.TargetCycle * 100
            : 0;

        // 状态文本与画刷
        (StatusText, StatusBrushKey) = MapStatus(rt.StatusWord);

        // 设备配置（从 Device 读取）
        RefreshDeviceConfig();

        // 活跃报警从设备配置读取
        RefreshActiveAlarms();

        // 工单信息
        RefreshWorkOrder();

        // 缺陷饼图（从内存快照）
        RefreshDefectChart();

        LastUpdateTime = DateTime.Now;
    }

    /// <summary>刷新设备配置属性（从 CurrentDevice 读取）。</summary>
    private void RefreshDeviceConfig()
    {
        var dev = CurrentDevice;
        if (dev == null)
        {
            TargetCycle = 0;
            RecipeName = string.Empty;
            RecipeValue = 0;
            OkCountAddress = string.Empty;
            NgCountAddress = string.Empty;
            StatusAddress = string.Empty;
            ResetAddress = string.Empty;
            return;
        }
        TargetCycle = dev.TargetCycle;
        RecipeName = dev.RecipeName;
        RecipeValue = dev.RecipeValue;
        OkCountAddress = dev.OkCountAddress;
        NgCountAddress = dev.NgCountAddress;
        StatusAddress = dev.StatusCountAddress;
        ResetAddress = dev.ProductionResetAddress;
    }

    /// <summary>
    /// 刷新当前设备关联的活跃工单。
    /// 查询 Running 状态工单，若无 Running 则查询最新 Pending 作为预览。
    /// 产量聚合通过 IWorkOrderService.GetProductionSummary 获取。
    /// </summary>
    private void RefreshWorkOrder()
    {
        var dev = CurrentDevice;
        if (dev == null)
        {
            HasWorkOrder = false;
            WorkOrderNo = string.Empty;
            ProductName = string.Empty;
            PlannedQuantity = 0;
            CompletedQuantity = 0;
            WorkOrderStatusText = string.Empty;
            WorkOrderProgress = 0;
            return;
        }

        var order = _workOrderRepo.GetRunningByDevice(dev.Id)
                    ?? _workOrderRepo.GetLatestPendingByDevice(dev.Id);
        if (order == null)
        {
            HasWorkOrder = false;
            WorkOrderNo = string.Empty;
            ProductName = string.Empty;
            PlannedQuantity = 0;
            CompletedQuantity = 0;
            WorkOrderStatusText = string.Empty;
            WorkOrderProgress = 0;
            return;
        }

        HasWorkOrder = true;
        WorkOrderNo = order.OrderNo;
        ProductName = order.ProductName;
        PlannedQuantity = order.TargetQuantity;
        WorkOrderStatusText = order.Status switch
        {
            WorkOrderStatus.Pending => Strings.M041,
            WorkOrderStatus.Running => Strings.M042,
            WorkOrderStatus.Completed => Strings.M043,
            WorkOrderStatus.Aborted => Strings.M031,
            _ => order.Status.ToString(),
        };

        // 产量聚合：Running 工单查询实际产量，Pending 工单无产量
        if (order.Status == WorkOrderStatus.Running)
        {
            // 节流：同一工单 2s 内（采集周期 1 次/秒）复用上次结果——底层数据按
            // HistoryWriteIntervalScans 才变化，无需每秒重复查询（Remote 模式每次是一次 SignalR 往返）。
            if (_lastSummaryOrderId == order.Id && (DateTime.UtcNow - _lastSummaryQueryUtc).TotalSeconds < 2)
                return;

            // 查询移出 UI 线程：Local 模式为 SQLite 查询，Remote 模式 GetProductionSummary
            // 内部经 Task.Run+GetResult 同步阻塞，最长 RemoteCallTimeout（30s）——原来在 UI 线程执行会整窗卡死。
            _workOrderSummaryCts?.Cancel();
            _workOrderSummaryCts?.Dispose();
            var cts = _workOrderSummaryCts = new CancellationTokenSource();
            var token = cts.Token;
            _lastSummaryOrderId = order.Id;
            _lastSummaryQueryUtc = DateTime.UtcNow;

            Task.Run(() =>
            {
                int okCount;
                try
                {
                    okCount = _workOrderService.GetProductionSummary(order).OkCount;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "查询工单 {OrderNo} 产量聚合失败", order.OrderNo);
                    okCount = 0; // 降级显示 0（与原同步路径行为一致）
                }
                if (token.IsCancellationRequested) return;
                DispatchWorkOrderSummary(order, okCount);
            }, token).Forget(_logger);
        }
        else
        {
            CompletedQuantity = 0;
            WorkOrderProgress = 0;
        }
    }

    /// <summary>
    /// 把工单产量聚合结果封送回 UI 线程。Dispatcher 不可用（单元测试环境）或应用关闭中时直接执行，
    /// 生产环境经 BeginInvoke 封送（审查修复 2026-08-13）。
    /// </summary>
    private void DispatchWorkOrderSummary(WorkOrder order, int okCount)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess())
        {
            ApplyWorkOrderSummary(order, okCount);
            return;
        }
        dispatcher.BeginInvoke(() => ApplyWorkOrderSummary(order, okCount));
    }

    private void ApplyWorkOrderSummary(WorkOrder order, int okCount)
    {
        // 设备已切换或工单已变化时丢弃过期结果（防旧设备数据覆盖新设备）
        if (CurrentDevice?.Id != order.DeviceId) return;
        CompletedQuantity = okCount;
        WorkOrderProgress = order.TargetQuantity > 0
            ? Math.Clamp((double)okCount / order.TargetQuantity, 0, 1)
            : 0;
    }

    /// <summary>
    /// 刷新缺陷占比饼图（从 CurrentDevice.Defects 内存快照）。
    /// 无缺陷数据时设为 null（由 UI 显示空状态）。
    /// </summary>
    private void RefreshDefectChart()
    {
        var dev = CurrentDevice;
        if (dev == null || dev.Defects.Count == 0)
        {
            DefectPieChart = null;
            DefectTotal = 0;
            DefectTypeCount = 0;
            TopDefectText = "—";
            return;
        }
        var snapshot = dev.Defects.ToList();
        var positive = snapshot.Where(d => d.Count > 0).OrderByDescending(d => d.Count).ToList();
        DefectTotal = positive.Sum(d => d.Count);
        DefectTypeCount = positive.Count;
        TopDefectText = positive.Count > 0 ? $"{positive[0].Name} ({positive[0].Count})" : "—";
        DefectPieChart = ChartService.BuildDefectPieChart(
            snapshot.Select(d => (d.Name, d.Count)));
    }

    private static (string text, string brushKey) MapStatus(int statusWord)
    {
        // 状态文本复用 HistoryQueryHelper.GetStateText（全应用统一映射），此处只保留画刷映射。
        var text = HistoryQueryHelper.GetStateText(statusWord);
        var brushKey = statusWord switch
        {
            (int)DeviceStatus.Running => "StatusRunBrush",
            (int)DeviceStatus.Alarm => "StatusAlarmBrush",
            (int)DeviceStatus.Paused => "StatusPauseBrush",
            _ => "StatusIdleBrush",
        };
        return (text, brushKey);
    }

    /// <summary>
    /// 刷新活跃报警列表：从设备配置筛选未恢复的报警
    /// （已触发 StartTime &gt; MinValue 且未恢复 EndTime &lt; StartTime）。
    /// 活跃报警的 Duration 用 Now - StartTime 动态计算（Alarm.Duration 在活跃态返回 Zero）。
    /// </summary>
    private void RefreshActiveAlarms()
    {
        ActiveAlarms.Clear();
        if (CurrentDevice == null) return;

        var now = DateTime.Now;
        foreach (var a in CurrentDevice.Alarms)
        {
            // 活跃判断：已触发（StartTime > MinValue）且未恢复（EndTime < StartTime）
            if (a.StartTime > DateTime.MinValue && a.EndTime < a.StartTime)
            {
                ActiveAlarms.Add(new AlarmConfigRow
                {
                    Name = a.Name,
                    PlcAddress = a.PlcAddress,
                    Level = a.Level,
                    Description = a.Description,
                    StartTime = a.StartTime,
                    Duration = now - a.StartTime,
                });
            }
        }
        foreach (var alarm in CurrentDevice.CountAlarms)
        {
            if (!alarm.Enabled || !alarm.IsTriggered) continue;
            ActiveAlarms.Add(new AlarmConfigRow
            {
                Name = alarm.Name,
                PlcAddress = alarm.PlcAddress,
                Level = AlarmLevel.Medium,
                Description = alarm.Description,
                IsCountAlarm = true,
                CurrentValue = alarm.CurrentValue,
                Threshold = alarm.MaxValue,
                StartTime = now,
                Duration = TimeSpan.Zero,
            });
        }
        ActiveAlarmCount = ActiveAlarms.Count;
    }

    [RelayCommand]
    private void ViewAlarmHistory(AlarmConfigRow? alarm)
    {
        if (alarm == null || CurrentDevice == null) return;
        ViewAlarmHistoryRequested?.Invoke(CurrentDevice.Id, alarm.Name);
    }

    [RelayCommand]
    private void ShowOeeExplanation(string? metric)
    {
        var explanation = metric switch
        {
            "Availability" => Strings.M150,
            "Performance" => Strings.M151,
            "Quality" => Strings.M152,
            _ => Strings.M153,
        };
        _dialog.NotifyInfo(explanation);
    }

    /// <summary>
    /// 查询最近报警事件（从历史库，异步）。
    /// 每次查询前取消上一次查询，避免快速切换设备时旧查询覆盖新数据。
    /// </summary>
    private void RefreshRecentAlarms()
    {
        // 取消上一次未完成的查询
        _recentAlarmsCts?.Cancel();
        _recentAlarmsCts?.Dispose();
        _recentAlarmsCts = new CancellationTokenSource();
        var token = _recentAlarmsCts.Token;

        if (CurrentDevice == null)
        {
            RecentAlarms.Clear();
            TodayAlarmCount = 0;
            IsRefreshing = false;
            RefreshStatusText = Strings.M160;
            return;
        }

        var deviceId = CurrentDevice.Id;
        Task.Run(() =>
        {
            try
            {
                var end = DateTime.Now;
                var start = DateTime.Today;
                var records = _historyService.QueryAlarmEvents(start, end, deviceId);
                token.ThrowIfCancellationRequested();

                var list = records.OrderByDescending(r => r.EventTime).Take(50).ToList();
                token.ThrowIfCancellationRequested();

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    RecentAlarms.Clear();
                    foreach (var r in list)
                        RecentAlarms.Add(r);
                    TodayAlarmCount = records.Count(r => r.EventType == AlarmEventType.Triggered);
                    IsRefreshing = false;
                    RefreshStatusText = Strings.M155;
                });
            }
            catch (OperationCanceledException)
            {
                // 切换设备导致的取消，非错误，不记录
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询设备 {DeviceId} 报警事件失败", deviceId);
                DispatchOnUi(() =>
                {
                    IsRefreshing = false;
                    RefreshStatusText = Strings.M157;
                });
                DispatchOnUi(() => _dialog.NotifyError(string.Format(Strings.F155, ex.Message)));
            }
        }, token).Forget(_logger);
    }

    // ──────────── 命令 ────────────

    /// <summary>手动刷新（重新查询历史数据并刷新 KPI）。</summary>
    [RelayCommand]
    private void Refresh()
    {
        IsRefreshing = true;
        RefreshStatusText = Strings.M156;
        RefreshKpis();
        RefreshRecentAlarms();
    }

    /// <summary>
    /// 设备详情页进入时加载一次最近 24 小时产量图。
    /// ViewModel 为单例，不能放在构造函数中，否则会在应用启动而非页面进入时查询。
    /// </summary>
    public void RefreshOnEnter()
    {
        RefreshStatusText = Strings.M159;
        RefreshHourlyProduction();
    }

    [RelayCommand]
    private void SetHourlyRange(string? hoursText)
    {
        if (!int.TryParse(hoursText, out var hours) || hours is not (8 or 24))
            return;
        HourlyRangeHours = hours;
        HourlyRangeText = hours == 8 ? Strings.M109 : Strings.K025;
        RefreshHourlyProduction();
    }

    /// <summary>
    /// 刷新按小时产量柱状图（最近选定时长）。
    /// 在后台线程查询 ProductionLog，按小时桶聚合后差分得到增量。
    /// OK/NG 的累计值差分独立计算（OverviewViewModel 中 NG 未实现差分，此处补全）。
    /// </summary>
    private void RefreshHourlyProduction()
    {
        // 取消上次未完成的产量查询
        _productionCts?.Cancel();
        _productionCts?.Dispose();
        _productionCts = new CancellationTokenSource();
        var token = _productionCts.Token;

        if (CurrentDevice == null)
        {
            HourlyProductionChart = null;
            return;
        }

        var deviceId = CurrentDevice.Id;
        var targetCycle = CurrentDevice.TargetCycle;
        var rangeHours = HourlyRangeHours;
        Task.Run(() =>
        {
            try
            {
                var to = DateTime.Now;
                var from = to.AddHours(-rangeHours);
                var logs = _historyService.QueryProductionLogs(from, to, deviceId);
                token.ThrowIfCancellationRequested();

                // 构建按小时桶
                var buckets = BuildHourlyBuckets(from, to);
                if (buckets.Length == 0)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (!token.IsCancellationRequested) HourlyProductionChart = null;
                    });
                    return;
                }

                var okCumulative = new int[buckets.Length];
                var ngCumulative = new int[buckets.Length];
                var hasSample = new bool[buckets.Length];
                foreach (var log in logs.OrderBy(log => log.Timestamp))
                {
                    var idx = GetBucketIndex(buckets, log.Timestamp);
                    if (idx >= 0 && idx < buckets.Length)
                    {
                        // 累计值：桶内保留末条（覆盖写入）
                        okCumulative[idx] = log.OkProduction;
                        ngCumulative[idx] = log.NgProduction;
                        hasSample[idx] = true;
                    }
                }

                // 缺少采集记录的小时沿用上一条累计值，避免后续差分把空桶当成归零。
                var lastOk = 0;
                var lastNg = 0;
                for (int i = 0; i < buckets.Length; i++)
                {
                    if (hasSample[i])
                    {
                        lastOk = okCumulative[i];
                        lastNg = ngCumulative[i];
                    }
                    else
                    {
                        okCumulative[i] = lastOk;
                        ngCumulative[i] = lastNg;
                    }
                }

                // 累计转增量（负差分置零，班次切换重置场景）。
                // 首桶基线（审查修复 2026-08-13）：首桶直接取窗口内首条累计值会把窗口开始前的历史产量
                // 计入第一个小时（设备长期未上传时首柱虚高几千件）——查窗口前最后一条快照作基线
                var baseline = _historyService.QueryProductionLogs(from.AddHours(-24), from, deviceId)
                    .LastOrDefault();
                var okDiff = DiffCumulative(okCumulative, baseline?.OkProduction ?? 0);
                var ngDiff = DiffCumulative(ngCumulative, baseline?.NgProduction ?? 0);

                var chart = ChartService.BuildHourlyProductionBarChart(buckets, okDiff, ngDiff, targetCycle);
                token.ThrowIfCancellationRequested();

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        HourlyProductionChart = chart;
                        RefreshStatusText = Strings.M154;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // 切换设备导致的取消，非错误
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询设备 {DeviceId} 按小时产量失败", deviceId);
                DispatchOnUi(() =>
                {
                    RefreshStatusText = Strings.M158;
                    _dialog.NotifyError(string.Format(Strings.F153, ex.Message));
                });
            }
        }, token).Forget(_logger);
    }

    // ──────────── 按小时桶聚合辅助（委托 HistoryQueryHelper 单源，与 OverviewViewModel 桶逻辑同源） ────────────

    private static DateTime[] BuildHourlyBuckets(DateTime from, DateTime to)
        => ViewModels.HistoryQueryHelper.BuildHourlyBuckets(from, to);

    private static int GetBucketIndex(DateTime[] buckets, DateTime time)
        => ViewModels.HistoryQueryHelper.GetBucketIndex(buckets, time);

    /// <summary>累计值转增量：后一桶减前一桶，负数置零（班次切换重置场景）；首桶扣 <paramref name="baseline"/>。</summary>
    private static int[] DiffCumulative(int[] cumulative, int baseline = 0)
    {
        if (cumulative.Length == 0) return cumulative;
        var result = new int[cumulative.Length];
        result[0] = Math.Max(0, cumulative[0] - baseline);
        for (int i = 1; i < cumulative.Length; i++)
        {
            result[i] = Math.Max(0, cumulative[i] - cumulative[i - 1]);
        }
        return result;
    }

    /// <summary>返回主页：触发 GoBackRequested 事件，由 MainWindowViewModel 订阅后置 SelectedIndex=0。</summary>
    [RelayCommand]
    private void GoBack()
    {
        GoBackRequested?.Invoke();
    }

    /// <summary>返回主页请求事件（MainWindowViewModel 订阅）。</summary>
    public event Action? GoBackRequested;
    public event Action<string, string>? ViewAlarmHistoryRequested;
}

// ──────────── 配置行数据模型 ────────────

public class AlarmConfigRow
{
    public string Name { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    public AlarmLevel Level { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsCountAlarm { get; set; }
    public int CurrentValue { get; set; }
    public int Threshold { get; set; }
    public DateTime StartTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string LevelText => Level switch
    {
        AlarmLevel.High => Strings.Level_High,
        AlarmLevel.Medium => Strings.Level_Medium,
        _ => Strings.Level_Low,
    };
    /// <summary>持续时间文本（活跃报警显示累计时长）。</summary>
    public string DurationText => Duration.TotalSeconds > 0
        ? $"{(int)Duration.TotalHours}h {Duration.Minutes}m"
        : "—";
    public string ValueText => IsCountAlarm
        ? string.Format(Strings.F120, CurrentValue, Threshold)
        : string.Format(Strings.F196, StartTime, PlcAddress);
}
