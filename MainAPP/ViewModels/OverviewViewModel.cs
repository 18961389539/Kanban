using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Client;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using Serilog;
using MainAPP.Helpers;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 最近 N 小时生产概览 ViewModel。
/// 汇总全厂产量/报警，按小时聚合趋势，列出 Top 报警和设备明细（每台设备独立 OEE）。
/// 数据来源：HistoryService（历史快照）+ DeviceRepository.Runtimes（实时状态色条）。
/// </summary>
public partial class OverviewViewModel : ObservableObject, IDisposable, INavigationPageLifecycle
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly AppSettings _appSettings;
    private readonly IDialogService _dialog;
    private readonly IDeviceSelectionService _selection;
    private readonly IProductionReviewPdfService? _pdfService;
    private readonly IWorkOrderRepository? _workOrderRepository;
    private readonly IProductionReviewCsvExportService _csvExportService;
    private readonly IProductionReviewChartService _chartService;
    // 2026-09-02 拆分（P1-9）：数据聚合全部委托给概览页服务，
    // ViewModel 只保留 UI 映射（KPI 属性/集合/图表重建）与命令。
    private readonly IOverviewDashboardService _dashboardService;
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private bool _isInitialized;
    private int _cachedChartSignature;
    private int _refreshVersion;
    private bool _refreshPending;

    // ──────────── 页面生命周期（审查修复 2026-08-30：N-3 图表定时器泄漏） ────────────
    // 概览页 2Hz 图表重绘定时器与 VM.PropertyChanged 订阅原本由 OverviewView 的 Loaded/Unloaded
    // 管理，但 NavigationPageHost 常驻导致 Unloaded 永不触发 → 切走后定时器持续运行、逐页叠加
    // （"页面切换越来越卡"的主因之一）。现由 MainWindow.ActivatePage 经本接口驱动，
    // 通过 Entered/Exited 事件通知视图绑定/解绑，视图的定时器仅在页面激活期间运行。

    /// <summary>页面当前是否激活（视图据此守卫图表重绘定时器）。</summary>
    public bool IsPageActive { get; private set; }

    /// <summary>页面进入（MainWindow 切换驱动后触发；视图在此启动定时器并强制刷新图表）。</summary>
    public event EventHandler? Entered;

    /// <summary>页面退出（视图在此停止定时器、清空脏标记）。</summary>
    public event EventHandler? Exited;

    /// <inheritdoc />
    public void OnPageEnter()
    {
        if (IsPageActive) return;
        IsPageActive = true;
        Entered?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void OnPageExit()
    {
        if (!IsPageActive) return;
        IsPageActive = false;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    // ──────────── KPI 属性 ────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalOutput))]
    private int _totalOk;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalOutput))]
    private int _totalNg;
    [ObservableProperty] private double _qualityRate;
    [ObservableProperty] private double _oee;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecoveredAlarmCount))]
    private int _alarmCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecoveredAlarmCount))]
    private int _pendingAlarmCount;

    public int RecoveredAlarmCount => Math.Max(0, AlarmCount - PendingAlarmCount);
    [ObservableProperty] private string _peakHour = string.Empty;
    [ObservableProperty] private int _peakHourOk;
    [ObservableProperty] private string _valleyHour = string.Empty;
    [ObservableProperty] private int _valleyHourOk;
    [ObservableProperty] private string _longestDowntimeDevice = string.Empty;
    [ObservableProperty] private string _longestDowntimeAlarm = string.Empty;
    [ObservableProperty] private double _longestDowntimeHours;
    [ObservableProperty] private DateTime _lastUpdateTime;
    [ObservableProperty] private string _currentShiftName = string.Empty;
    [ObservableProperty] private string _currentShiftDateRange = string.Empty;

    public ObservableCollection<DeviceFilterItem> DeviceFilterItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDeviceName))]
    private string? _selectedDeviceId;

    public string SelectedDeviceName => DeviceFilterItems.FirstOrDefault(d => d.Id == SelectedDeviceId)?.Name ?? Strings.M_NoDeviceSelected;

    [ObservableProperty] private int _targetOutput;
    [ObservableProperty] private double _outputAchievementRate;
    [ObservableProperty] private string _comparisonLabel = Strings.M058;
    [ObservableProperty] private int _baselineTotalOutput;
    [ObservableProperty] private double _baselineQualityRate;
    [ObservableProperty] private double _baselineOee;
    [ObservableProperty] private int _outputDelta;
    [ObservableProperty] private double _qualityRateDelta;
    [ObservableProperty] private double _oeeDelta;
    [ObservableProperty] private double _totalDowntimeHours;
    [ObservableProperty] private double _averageAlarmDurationMinutes;
    [ObservableProperty] private double _mtbfHours;
    [ObservableProperty] private string _availabilityLossText = string.Empty;
    [ObservableProperty] private string _performanceLossText = string.Empty;
    [ObservableProperty] private string _qualityLossText = string.Empty;
    [ObservableProperty] private int _healthScore = 100;
    [ObservableProperty] private string _currentWorkOrderText = Strings.M055;
    [ObservableProperty] private string _currentProductText = Strings.M056;
    [ObservableProperty] private string _currentRecipeText = Strings.M057;

    public string HealthScoreText => $"{HealthScore} / 100";

    /// <summary>峰值/谷值时段 OK 产量占全窗口 OK 比例（方案 A，2026-08-11；纯数据文本）。</summary>
    public string PeakShareText => TotalOk > 0 ? $"{PeakHourOk * 100.0 / TotalOk:F0}%" : "—";
    public string ValleyShareText => TotalOk > 0 ? $"{ValleyHourOk * 100.0 / TotalOk:F0}%" : "—";

    private static OverviewChartPalette CreateChartPalette() => new(
        ChartPalette.Text,
        ChartPalette.Grid,
        ChartPalette.Ok,
        ChartPalette.Ng,
        ChartPalette.ShiftBg,
        ChartPalette.Base,
        ChartPalette.Pause);

    public int TotalOutput => TotalOk + TotalNg;
    public string ComparisonSummaryText =>
        string.Format(Strings.F021, ComparisonLabel, FormatSigned(OutputDelta), FormatPercentageDelta(QualityRateDelta), FormatPercentageDelta(OeeDelta));

    // 阈值唯一源（2026-08-16）：引用 KpiThresholds，与 Converter/ChartService 着色共用，改一处全局生效。
    public const double QualityTarget = KpiThresholds.QualityGood;
    public const double OeeTarget = KpiThresholds.OeeGood;

    public bool HasProductionData => TotalOk > 0 || TotalNg > 0;
    public bool HasAlarmData => AlarmCount > 0;
    public bool HasStatusData => RunTimeHours > 0 || AlarmDurationHours > 0 || PausedTimeHours > 0;
    public bool HasAnyHistoryData => HasProductionData || HasAlarmData || HasStatusData;
    public string DataCoverageText => !HasAnyHistoryData
        ? Strings.M102
        : !HasProductionData
            ? Strings.M103
            : !HasAlarmData
                ? Strings.M104
                : Strings.M105;
    public string TargetStatusText => string.Format(Strings.F192, QualityRate, QualityTarget, Oee, OeeTarget);

    private static string FormatSigned(int value) => Services.ProductionReviewCalculations.FormatSigned(value);
    private static string FormatPercentageDelta(double value) => Services.ProductionReviewCalculations.FormatSignedPercentage(value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHour1))]
    [NotifyPropertyChangedFor(nameof(IsHours8))]
    [NotifyPropertyChangedFor(nameof(IsHours24))]
    [NotifyPropertyChangedFor(nameof(IsDays7))]
    private OverviewTimeRange _selectedTimeRange = OverviewTimeRange.Hours24;

    public bool IsHour1 => SelectedTimeRange == OverviewTimeRange.Hour1;
    public bool IsHours8 => SelectedTimeRange == OverviewTimeRange.Hours8;
    public bool IsHours24 => SelectedTimeRange == OverviewTimeRange.Hours24;
    public bool IsDays7 => SelectedTimeRange == OverviewTimeRange.Days7;

    public IReadOnlyList<OverviewTimeRangeOption> TimeRangeOptions { get; } = new[]
    {
        new OverviewTimeRangeOption(OverviewTimeRange.CurrentShift, Strings.M106),
        new OverviewTimeRangeOption(OverviewTimeRange.PreviousShift, Strings.M107),
        new OverviewTimeRangeOption(OverviewTimeRange.Today, Strings.M108),
        new OverviewTimeRangeOption(OverviewTimeRange.Hour1, Strings.K042),
        new OverviewTimeRangeOption(OverviewTimeRange.Hours8, Strings.M109),
        new OverviewTimeRangeOption(OverviewTimeRange.Hours24, Strings.K025),
        new OverviewTimeRangeOption(OverviewTimeRange.Days7, Strings.K258),
    };

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExportingReport;

    /// <summary>趋势图窗口内是否有产量数据（false 时 XAML 显示空状态提示，P2-9）。</summary>
    [ObservableProperty] private bool _hasTrendData;

    /// <summary>最近一次刷新失败的错误消息（页内提示条 + 重试按钮），null = 无错误。</summary>
    [ObservableProperty] private string? _queryErrorMessage;

    /// <summary>设备明细表展开状态（默认展开，复盘页核心信息首屏可见；可手动折叠）。</summary>
    [ObservableProperty] private bool _isDeviceTableExpanded = true;

    public bool HasData => HasAnyHistoryData;

    [ObservableProperty] private double _runTimeHours;
    [ObservableProperty] private double _pausedTimeHours;
    [ObservableProperty] private double _alarmDurationHours;

    partial void OnTotalOkChanged(int value)
    {
        NotifyDataCoverageChanged();
        ExportReportPdfCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ComparisonSummaryText));
        OnPropertyChanged(nameof(PeakShareText));
        OnPropertyChanged(nameof(ValleyShareText));
    }

    partial void OnTotalNgChanged(int value)
    {
        NotifyDataCoverageChanged();
        OnPropertyChanged(nameof(ComparisonSummaryText));
    }

    partial void OnAlarmCountChanged(int value)
    {
        NotifyDataCoverageChanged();
        OnPropertyChanged(nameof(TargetStatusText));
    }

    partial void OnQualityRateChanged(double value) => OnPropertyChanged(nameof(TargetStatusText));
    partial void OnOeeChanged(double value) => OnPropertyChanged(nameof(TargetStatusText));
    partial void OnRunTimeHoursChanged(double value) => NotifyDataCoverageChanged();
    partial void OnPausedTimeHoursChanged(double value) => NotifyDataCoverageChanged();
    partial void OnAlarmDurationHoursChanged(double value) => NotifyDataCoverageChanged();
    partial void OnOutputDeltaChanged(int value) => OnPropertyChanged(nameof(ComparisonSummaryText));
    partial void OnQualityRateDeltaChanged(double value) => OnPropertyChanged(nameof(ComparisonSummaryText));
    partial void OnOeeDeltaChanged(double value) => OnPropertyChanged(nameof(ComparisonSummaryText));

    partial void OnSelectedDeviceIdChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedDeviceName));
        if (_selection.SelectedDeviceId != value)
            _selection.SelectedDeviceId = value;
        if (_isInitialized)
            _ = RefreshAsync();
    }

    partial void OnHealthScoreChanged(int value) => OnPropertyChanged(nameof(HealthScoreText));

    partial void OnIsLoadingChanged(bool value)
    {
        ExportReportCommand.NotifyCanExecuteChanged();
        ExportReportPdfCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsExportingReportChanged(bool value)
    {
        ExportReportCommand.NotifyCanExecuteChanged();
        ExportReportPdfCommand.NotifyCanExecuteChanged();
    }

    private void NotifyDataCoverageChanged()
    {
        OnPropertyChanged(nameof(HasAlarmData));
        OnPropertyChanged(nameof(HasProductionData));
        OnPropertyChanged(nameof(HasStatusData));
        OnPropertyChanged(nameof(HasAnyHistoryData));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(DataCoverageText));
    }

    private bool CanExportReport() => HasAnyHistoryData && !IsLoading && !IsExportingReport;
    private bool CanExportReportPdf() => CanExportReport() && _pdfService != null;

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportAsync()
    {
        var path = _dialog.ShowSaveFileDialog(
            Strings.M116,
            string.Format(Strings.Export_ReviewCsvFileName, DateTime.Now),
            Strings.Export_CsvFilter);
        if (string.IsNullOrWhiteSpace(path)) return;

        var (from, to) = _dashboardService.ResolveTimeRange(SelectedTimeRange, _appSettings.GetShiftsSnapshot(), DateTime.Now); // 2026-09-02 拆分：时间范围解析下沉服务
        // IsExporting 前置 + CSV 构建移后台（审查修复 2026-08-13）：原 Build 在 UI 线程
        // 同步执行、IsExportingReport 置位在构建之后——双击窗口可并发构建两次 + 大窗口卡 UI
        IsExportingReport = true;
        try
        {
            var csv = await Task.Run(() => _csvExportService.Build(new ProductionReviewCsvData(
                from,
                to,
                CurrentShiftName,
                TotalOk,
                TotalNg,
                QualityRate,
                Oee,
                RunTimeHours,
                PausedTimeHours,
                AlarmDurationHours,
                AlarmCount,
                DeviceSummaries.ToList(),
                ShiftComparisons.ToList(),
                TopAlarms.ToList())));
            await Task.Run(() => File.WriteAllText(path, csv, new UTF8Encoding(true)));
            AuditLog.Record("Export.Csv", "Export", Path.GetFileName(path), detail: Strings.Audit_Detail_ReviewReport);
            _dialog.NotifySuccess(string.Format(Strings.F168, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出生产复盘报表失败");
            _dialog.NotifyError(string.Format(Strings.F126, ex.Message));
        }
        finally
        {
            IsExportingReport = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportReportPdf))]
    private async Task ExportReportPdfAsync()
    {
        if (_pdfService == null) return;
        var path = _dialog.ShowSaveFileDialog(
            Strings.M117,
            string.Format(Strings.Export_ReviewPdfFileName, DateTime.Now),
            Strings.Export_PdfFilter);
        if (string.IsNullOrWhiteSpace(path)) return;

        var (rangeFrom, rangeTo) = _dashboardService.ResolveTimeRange(SelectedTimeRange, _appSettings.GetShiftsSnapshot(), DateTime.Now); // 2026-09-02 拆分：时间范围解析下沉服务
        var range = (From: rangeFrom, To: rangeTo);
        var data = new ProductionReviewPdfData(
            range.From,
            range.To,
            CurrentShiftName,
            TotalOk,
            TotalNg,
            QualityRate,
            Oee,
            RunTimeHours,
            PausedTimeHours,
            AlarmDurationHours,
            AlarmCount,
            DeviceSummaries.ToList(),
            ShiftComparisons.ToList(),
            TopAlarms.ToList(),
            TrendChart,
            OeeWaterfallChart,
            ProductionHeatmapChart,
            TargetOutput,
            OutputAchievementRate,
            DefectParetos.ToList(),
            ComparisonLabel,
            BaselineTotalOutput,
            BaselineQualityRate,
            BaselineOee,
            OutputDelta,
            QualityRateDelta,
            OeeDelta,
            TotalDowntimeHours,
            AverageAlarmDurationMinutes,
            MtbfHours,
            AvailabilityLossText,
            PerformanceLossText,
            QualityLossText);

        IsExportingReport = true;
        try
        {
            await Task.Run(() => _pdfService.Export(path, data));
            AuditLog.Record("Export.Pdf", "Export", Path.GetFileName(path), detail: Strings.Audit_Detail_ReviewPdf);
            _dialog.NotifySuccess(string.Format(Strings.F167, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出生产复盘 PDF 失败");
            _dialog.NotifyError(string.Format(Strings.F009, ex.Message));
        }
        finally
        {
            IsExportingReport = false;
        }
    }

    // ──────────── 图表与列表 ────────────

    public PlotModel? TrendChart { get; private set; }

    public ObservableCollection<DeviceOverviewSummary> DeviceSummaries { get; } = new();

    public ObservableCollection<AlarmOverviewSummary> TopAlarms { get; } = new();

    /// <summary>班次对比列表（每行一个班次的聚合指标），用于班次对比表格。</summary>
    public ObservableCollection<ShiftComparisonSummary> ShiftComparisons { get; } = new();

    /// <summary>缺陷帕累托列表（按数量降序），用于缺陷帕累托图。</summary>
    public ObservableCollection<DefectParetoSummary> DefectParetos { get; } = new();

    public ObservableCollection<ReviewStatusSegment> StatusTimeline { get; } = new();
    public ObservableCollection<HeatmapBucket> HeatmapBuckets { get; } = new();
    public ObservableCollection<string> HealthIssues { get; } = new();

    /// <summary>基于当前时间范围生成的事实型复盘结论，不包含无法追溯的主观判断。</summary>
    public ObservableCollection<ReviewConclusion> ReviewConclusions { get; } = new();

    /// <summary>OEE 瀑布图模型（P→A→Q→OEE 损失拆解）。</summary>
    public PlotModel? OeeWaterfallChart { get; private set; }

    /// <summary>时段产量热力图模型（设备 × 时段）。</summary>
    public PlotModel? ProductionHeatmapChart { get; private set; }

    /// <summary>缺陷帕累托双轴图模型：柱=新增数量（左轴），折线=累计占比（右轴）。</summary>
    public PlotModel? DefectParetoChart { get; private set; }

    /// <summary>
    /// 点击设备行：设置选中设备并发起跳转设备详情页请求。
    /// MainWindowViewModel 订阅 FocusDeviceRequested 完成导航。
    /// </summary>
    public event Action<string>? FocusDeviceRequested;

    public OverviewViewModel(
        IDeviceRepository deviceRepository,
        AppSettings appSettings,
        IDialogService dialog,
        IDeviceSelectionService selection,
        IProductionReviewPdfService? pdfService,
        IWorkOrderRepository? workOrderRepository,
        IProductionReviewCsvExportService csvExportService,
        IProductionReviewChartService chartService,
        IOverviewDashboardService dashboardService)
    {
        _deviceRepository = deviceRepository;
        _appSettings = appSettings;
        _dialog = dialog;
        _selection = selection;
        _pdfService = pdfService;
        _workOrderRepository = workOrderRepository;
        _csvExportService = csvExportService;
        _chartService = chartService;
        _dashboardService = dashboardService;

        RefreshDeviceFilterItems();
        var initialDeviceId = _selection.SelectedDeviceId
            ?? _deviceRepository.GetDevicesSnapshot().FirstOrDefault()?.Id;
        _selectedDeviceId = initialDeviceId;
        if (_selection.SelectedDeviceId == null && initialDeviceId != null)
            _selection.SelectedDeviceId = initialDeviceId;
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
        _selection.PropertyChanged += OnSelectionChanged;
        _isInitialized = true;

        // 当前班次名称
        UpdateCurrentShiftName();

    }

    /// <summary>
    /// 退订事件（DI 单例由容器 dispose 时调用，与同族 ViewModel 保持一致，避免事件链泄漏）。
    /// 命名方法订阅 + 退订，多次调用安全。
    /// </summary>
    public void Dispose()
    {
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _selection.PropertyChanged -= OnSelectionChanged;
    }

    /// <summary>
    /// 兼容旧测试的构造入口（仅测试/外部适配用，勿在新代码调用）。生产 DI 使用上面的分层构造，
    /// 此入口仅负责把旧历史门面适配为复盘应用服务。重构提示：拆分职责时优先迁移测试到分层构造，
    /// 随后删除本入口，消除"同一依赖两处创建"的测试/生产行为分叉。
    /// </summary>
    [Obsolete("仅供测试/旧调用适配；生产路径使用分层构造器")]
    public OverviewViewModel(
        IHistoryService historyService,
        IDeviceRepository deviceRepository,
        AppSettings appSettings,
        IDialogService dialog,
        IDeviceSelectionService selection,
        IProductionReviewPdfService? pdfService = null,
        WorkOrderRepository? workOrderRepository = null,
        IProductionReviewCsvExportService? csvExportService = null,
        IProductionReviewChartService? chartService = null)
        : this(
            deviceRepository,
            appSettings,
            dialog,
            selection,
            pdfService,
            workOrderRepository,
            csvExportService ?? new ProductionReviewCsvExportService(),
            chartService ?? new ProductionReviewChartService(),
            new OverviewDashboardService(
                new ProductionReviewDataService(historyService),
                new ProductionReviewMetricsService(new ProductionReviewDataService(historyService)),
                new ProductionReviewAnalysisService(historyService, null, workOrderRepository),
                defectHistoryStore: null))
    {
    }

    private void RefreshDeviceFilterItems()
    {
        DeviceFilterHelper.Refresh(DeviceFilterItems, _deviceRepository);
        OnPropertyChanged(nameof(SelectedDeviceName));
    }

    private void OnDevicesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _uiDispatcher.BeginInvoke(new Action(() =>
        {
            RefreshDeviceFilterItems();
            if (SelectedDeviceId == null || !_deviceRepository.Devices.Any(d => d.Id == SelectedDeviceId))
                SelectedDeviceId = _deviceRepository.Devices.FirstOrDefault()?.Id;
        }));
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IDeviceSelectionService.SelectedDeviceId)) return;
        var selected = _selection.SelectedDeviceId;
        if (selected == SelectedDeviceId) return;
        if (_uiDispatcher.CheckAccess())
            SelectedDeviceId = selected;
        else
            _uiDispatcher.BeginInvoke(new Action(() => SelectedDeviceId = selected));
    }

    /// <summary>
    /// 使正在执行的后台刷新结果失效。数据源被外部重置或替换时调用，
    /// 防止旧查询在新数据准备完成后回写过期 KPI。
    /// </summary>
    public void InvalidatePendingRefresh()
        => Interlocked.Increment(ref _refreshVersion);

    private void UpdateCurrentShiftName()
    {
        var now = DateTime.Now;
        // P0-1 修复 2026-09-02：经锁内快照读取班次，禁止直接枚举可变集合
        var shiftsSnapshot = _appSettings.GetShiftsSnapshot();
        var (shift, _) = HistoryQueryHelper.FindCurrentShift(shiftsSnapshot, now.TimeOfDay);
        if (shift == null)
        {
            CurrentShiftName = shiftsSnapshot.Count == 0
                ? Strings.M110
                : Strings.M111;
            CurrentShiftDateRange = string.Empty;
            return;
        }
        CurrentShiftName = $"{shift.Name} {FormatHelper.FormatClock(shift.StartTime)}-{FormatHelper.FormatClock(shift.EndTime)}";

        // 计算班次实际日期区间（跨天班次如夜班 20:00-08:00 需正确跨越午夜）
        DateTime shiftStart, shiftEnd;
        if (shift.StartTime < shift.EndTime)
        {
            // 同天班次（如白班 08:00-20:00）
            shiftStart = now.Date + shift.StartTime;
            shiftEnd = now.Date + shift.EndTime;
        }
        else
        {
            // 跨天班次（如夜班 20:00-08:00）
            if (now.TimeOfDay >= shift.StartTime)
            {
                // 当前在班次前半段（20:00 之后）
                shiftStart = now.Date + shift.StartTime;
                shiftEnd = now.Date.AddDays(1) + shift.EndTime;
            }
            else
            {
                // 当前在班次后半段（08:00 之前）
                shiftStart = now.Date.AddDays(-1) + shift.StartTime;
                shiftEnd = now.Date + shift.EndTime;
            }
        }
        CurrentShiftDateRange = $"{shiftStart:MM-dd HH:mm} ~ {shiftEnd:MM-dd HH:mm}";
    }

    partial void OnSelectedTimeRangeChanged(OverviewTimeRange value)
    {
        _ = RefreshAsync();
    }

    /// <summary>
    /// 手动刷新按钮命令。
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
        {
            Log.Information("概览页刷新被跳过（IsLoading=true，置 pending）");
            _refreshPending = true;
            return;
        }

        do
        {
            _refreshPending = false;
            await RefreshOnceAsync();
        }
        while (_refreshPending);
    }

    private async Task RefreshOnceAsync()
    {
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        IsLoading = true;
        try
        {
            var devices = _deviceRepository.GetDevicesSnapshot()
                .Where(device => device.Id == SelectedDeviceId)
                .ToList();
            if (devices.Count == 0)
            {
                ClearAll(refreshVersion);
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // P0-1 修复 2026-09-02：UI 线程取班次快照，交给后台服务线程消费
            var shiftsSnapshot = _appSettings.GetShiftsSnapshot();
            // P1-9 拆分（2026-09-02）：聚合逻辑在 OverviewDashboardService 后台线程执行，
            // ViewModel 只做 UI 映射（ApplyResult）。
            var result = await Task.Run(() => _dashboardService.Build(
                devices,
                _deviceRepository.RuntimeMap,
                shiftsSnapshot,
                SelectedTimeRange,
                DateTime.Now));
            sw.Stop();
            Log.Information("概览页刷新完成：设备 {Count} 台，耗时 {Elapsed}ms，SelectedDevice={DeviceId}",
                devices.Count, sw.ElapsedMilliseconds, SelectedDeviceId);
            if (!_uiDispatcher.HasShutdownStarted)
                await _uiDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (refreshVersion != Volatile.Read(ref _refreshVersion)) return;
            ApplyResult(result);
            LastUpdateTime = DateTime.Now;
            UpdateCurrentShiftName();
            OnPropertyChanged(nameof(HasData));
            QueryErrorMessage = null;
            _remoteRefreshRetries = 0;
        }
        catch (Exception ex)
        {
            // Remote 模式启动竞态：MainAPP 启动早期（连接 Collector 之前）会自动触发一次刷新，
            // 属瞬态而非数据故障——不弹错误打扰用户，延迟数秒后自动重试（连接通常 2~5s 内建立）。
            // 重试上限 2 次：连接持续失败时交给外层重连循环，避免每 3s 一次的刷新风暴。
            if (ex is InvalidOperationException ioe
                && ioe.Message.Contains(KanbanDataClient.NotConnectedMessage, StringComparison.Ordinal)
                && _remoteRefreshRetries < 2)
            {
                _remoteRefreshRetries++;
                Log.Warning("概览页刷新早于采集服务连接（第 {Retry} 次），2s 后自动重试", _remoteRefreshRetries);
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    Log.Information("竞态重试回调触发 Shutdown={Shutdown}", _uiDispatcher.HasShutdownStarted);
                    if (_uiDispatcher.HasShutdownStarted) return;
                    _uiDispatcher.BeginInvoke(() =>
                    {
                        Log.Information("竞态重试 UI 回调执行 IsLoading={IsLoading}", IsLoading);
                        if (!IsLoading) _ = RefreshAsync();
                    });
                });
                return;
            }
            Log.Warning(ex, "概览页数据查询失败");
            QueryErrorMessage = ex.Message;
            _dialog.NotifyError(string.Format(Strings.F085, ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>启动竞态自动重试计数（成功后清零；上限 2 次防刷新风暴）。</summary>
    private int _remoteRefreshRetries;

    /// <summary>
    /// 把聚合结果映射为 UI 状态（KPI 属性 + 集合差分同步 + 图表按签名重建）。
    /// 必须在 UI 线程调用；refreshVersion 已在调用前校验。
    /// </summary>
    private void ApplyResult(OverviewDashboardResult result)
    {
        TotalOk = result.TotalOk;
        TotalNg = result.TotalNg;
        AlarmCount = result.AlarmCount;
        PendingAlarmCount = result.PendingAlarmCount;
        PeakHour = result.PeakHour;
        PeakHourOk = result.PeakHourOk;
        ValleyHour = result.ValleyHour;
        ValleyHourOk = result.ValleyHourOk;
        LongestDowntimeDevice = result.LongestDowntimeDevice;
        LongestDowntimeAlarm = result.LongestDowntimeAlarm;
        LongestDowntimeHours = result.LongestDowntimeHours;
        QualityRate = result.QualityRate;
        Oee = result.Oee;
        RunTimeHours = result.RunTimeHours;
        PausedTimeHours = result.PausedTimeHours;
        AlarmDurationHours = result.AlarmDurationHours;
        TargetOutput = result.TargetOutput;
        OutputAchievementRate = result.OutputAchievementRate;
        ComparisonLabel = result.ComparisonLabel;
        BaselineTotalOutput = result.BaselineTotalOutput;
        BaselineQualityRate = result.BaselineQualityRate;
        BaselineOee = result.BaselineOee;
        OutputDelta = result.OutputDelta;
        QualityRateDelta = result.QualityRateDelta;
        OeeDelta = result.OeeDelta;
        TotalDowntimeHours = result.TotalDowntimeHours;
        AverageAlarmDurationMinutes = result.AverageAlarmDurationMinutes;
        MtbfHours = result.MtbfHours;
        AvailabilityLossText = result.AvailabilityLossText;
        PerformanceLossText = result.PerformanceLossText;
        QualityLossText = result.QualityLossText;
        HealthScore = result.HealthScore;
        CurrentWorkOrderText = result.CurrentWorkOrderText;
        CurrentProductText = result.CurrentProductText;
        CurrentRecipeText = result.CurrentRecipeText;

        var sortedSummaries = result.DeviceSummaries.OrderByDescending(d => d.Oee).ToList();
        // 差分同步（审查修复 2026-08-13）：原 7 个集合全量 Clear+逐项 Add，
        // 60s 定时刷新 + 切设备/时间范围都会触发一次，列表闪烁、滚动位置丢失、GC 压力大；
        // 改用 ObservableCollectionSyncHelper.Sync 按引用差分（与 HomeViewModel 同源）
        ObservableCollectionSyncHelper.Sync(DeviceSummaries, sortedSummaries);
        ObservableCollectionSyncHelper.Sync(TopAlarms, result.TopAlarms.ToList());
        ObservableCollectionSyncHelper.Sync(ShiftComparisons, result.ShiftComparisons.ToList());
        ObservableCollectionSyncHelper.Sync(DefectParetos, result.DefectParetos.ToList());
        ObservableCollectionSyncHelper.Sync(StatusTimeline, result.StatusTimeline.ToList());
        ObservableCollectionSyncHelper.Sync(HeatmapBuckets, HeatmapBucketBuilder.Build(result.StatusTimeline));
        ObservableCollectionSyncHelper.Sync(HealthIssues, result.HealthIssues.ToList());
        ObservableCollectionSyncHelper.Sync(ReviewConclusions, result.ReviewConclusions.ToList());

        // 图表签名比对：数据未变时跳过 PlotModel 重建（避免 60s 定时刷新的无谓 CPU 开销与 UI 闪烁）
        // 时间戳不纳入签名（允许时间标签最多滞后一个刷新周期），仅比较产量/率值/设备明细
        var signature = ComputeChartSignature(result.BucketOk, result.BucketNg, result.Oee, result.Performance,
            result.Availability, result.QualityRate, result.AvgTargetCycle, SelectedTimeRange, sortedSummaries, result.DefectParetos);
        if (signature == _cachedChartSignature) return;
        Log.Information("图表重建：签名变化 {Sig}（旧 {Old}），构建趋势/瀑布/热力", signature, _cachedChartSignature);
        TrendChart = _chartService.BuildTrend(
            result.Buckets,
            result.BucketOk,
            result.BucketNg,
            (ProductionReviewBucketSize)result.BucketSize,
            result.AvgTargetCycle,
            result.DeviceSummaries.Count,
            _appSettings.GetShiftsSnapshot(), // P0-1 修复 2026-09-02：统一快照读取
            CreateChartPalette());
        OnPropertyChanged(nameof(TrendChart));
        HasTrendData = result.BucketOk.Any(v => v > 0) || result.BucketNg.Any(v => v > 0);

        OeeWaterfallChart = _chartService.BuildOeeWaterfall(
            result.Performance,
            result.Availability,
            result.QualityRate,
            result.Oee,
            CreateChartPalette());
        Log.Information("OeeWaterfallChart 已构建：perf={P:P1} avail={A:P1} qual={Q:P1} oee={O:P1}",
            result.Performance, result.Availability, result.QualityRate, result.Oee);
        OnPropertyChanged(nameof(OeeWaterfallChart));

        ProductionHeatmapChart = _chartService.BuildHeatmap(
            result.Buckets,
            sortedSummaries
                .Select(summary => new ProductionReviewHeatmapRow(summary.DeviceName, summary.HourlyOk))
                .ToList(),
            (ProductionReviewBucketSize)result.BucketSize,
            CreateChartPalette());
        OnPropertyChanged(nameof(ProductionHeatmapChart));

        DefectParetoChart = _chartService.BuildDefectParetoChart(
            result.DefectParetos,
            CreateChartPalette());
        OnPropertyChanged(nameof(DefectParetoChart));

        _cachedChartSignature = signature;
    }

    private void ClearAll(int refreshVersion)
    {
        if (refreshVersion != Volatile.Read(ref _refreshVersion)) return;
        if (!_uiDispatcher.HasShutdownStarted)
        {
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                if (refreshVersion != Volatile.Read(ref _refreshVersion)) return;
                TotalOk = 0; TotalNg = 0;
                QualityRate = 0; Oee = 0;
                AlarmCount = 0; PendingAlarmCount = 0;
                RunTimeHours = 0; PausedTimeHours = 0; AlarmDurationHours = 0;
                PeakHour = string.Empty; ValleyHour = string.Empty;
                LongestDowntimeDevice = string.Empty; LongestDowntimeAlarm = string.Empty;
                LongestDowntimeHours = 0;
                DeviceSummaries.Clear();
                TopAlarms.Clear();
                ShiftComparisons.Clear();
                DefectParetos.Clear();
                StatusTimeline.Clear();
                HealthIssues.Clear();
                ReviewConclusions.Clear();
                ReviewConclusions.Add(new ReviewConclusion
                {
                    Text = Strings.M_NoDeviceForReview,
                    Kind = ReviewConclusionKind.Info,
                    Met = ReviewConclusionMetState.Neutral,
                });
                HealthScore = 100;
                CurrentWorkOrderText = Strings.M055;
                CurrentProductText = Strings.M056;
                CurrentRecipeText = Strings.M057;
                TrendChart = null;
                OnPropertyChanged(nameof(TrendChart));
                OeeWaterfallChart = null;
                OnPropertyChanged(nameof(OeeWaterfallChart));
                ProductionHeatmapChart = null;
                OnPropertyChanged(nameof(ProductionHeatmapChart));
                DefectParetoChart = null;
                OnPropertyChanged(nameof(DefectParetoChart));
                // P2-12 补全重置：设备为空时清空全部展示属性，避免残留旧值造成矛盾显示
                HasTrendData = false;
                TargetOutput = 0;
                OutputAchievementRate = 0;
                ComparisonLabel = string.Empty;
                BaselineTotalOutput = 0;
                BaselineQualityRate = 0;
                BaselineOee = 0;
                OutputDelta = 0;
                QualityRateDelta = 0;
                OeeDelta = 0;
                TotalDowntimeHours = 0;
                AverageAlarmDurationMinutes = 0;
                MtbfHours = 0;
                AvailabilityLossText = string.Empty;
                PerformanceLossText = string.Empty;
                QualityLossText = string.Empty;
                _cachedChartSignature = 0;
            }));
        }
    }
    /// <summary>
    /// 计算图表数据签名：用于判断刷新时数据是否变化，决定是否需要重建 PlotModel。
    /// 包含产量桶值、OEE 三率、设备产量明细等关键输入；时间戳不纳入（允许时间标签轻微滞后）。
    /// </summary>
    private static int ComputeChartSignature(
        int[] bucketOk, int[] bucketNg,
        double oee, double performance, double availability, double quality,
        double avgTargetCycle, OverviewTimeRange timeRange,
        IReadOnlyList<DeviceOverviewSummary> deviceSummaries,
        IReadOnlyList<DefectParetoSummary> defectParetos)
    {
        var hash = new HashCode();
        hash.Add(timeRange);
        hash.Add(avgTargetCycle.GetHashCode());
        hash.Add(oee.GetHashCode());
        hash.Add(performance.GetHashCode());
        hash.Add(availability.GetHashCode());
        hash.Add(quality.GetHashCode());
        foreach (var v in bucketOk) hash.Add(v);
        foreach (var v in bucketNg) hash.Add(v);
        foreach (var d in deviceSummaries)
        {
            hash.Add(d.DeviceId);
            hash.Add(d.OkCount);
            hash.Add(d.NgCount);
            hash.Add(d.StatusWord);
            if (d.HourlyOk != null)
                foreach (var v in d.HourlyOk) hash.Add(v);
        }
        // 审查修复 2026-08-13：签名此前不含缺陷帕累托数据——产量不变、缺陷计数变化时
        // DefectParetoChart 不重建，与每次重建的缺陷列表不一致
        foreach (var p in defectParetos)
        {
            hash.Add(p.DefectName);
            hash.Add(p.Count);
        }
        return hash.ToHashCode();
    }

    [RelayCommand]
    private void FocusDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        _selection.SelectedDeviceId = deviceId;
        FocusDeviceRequested?.Invoke(deviceId);
    }
}

/// <summary>时间范围下拉选项（值 + 中文标签）。</summary>
public class OverviewTimeRangeOption
{
    public OverviewTimeRange Value { get; set; }
    public string Label { get; set; } = string.Empty;
    public OverviewTimeRangeOption(OverviewTimeRange value, string label)
    {
        Value = value;
        Label = label;
    }
}
