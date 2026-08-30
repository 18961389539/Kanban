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
    private readonly IProductionReviewDataService _reviewDataService;
    private readonly IDeviceRepository _deviceRepository;
    private readonly AppSettings _appSettings;
    private readonly IDialogService _dialog;
    private readonly IDeviceSelectionService _selection;
    private readonly IProductionReviewPdfService? _pdfService;
    private readonly IDefectHistoryReader? _defectHistoryStore;
    private readonly IWorkOrderRepository? _workOrderRepository;
    private readonly IProductionReviewAnalysisService _analysisService;
    private readonly IProductionReviewCsvExportService _csvExportService;
    private readonly IProductionReviewChartService _chartService;
    private readonly IProductionReviewMetricsService _metricsService;
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

        var (from, to) = GetTimeRange();
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
            AuditLog.Record("Export.Csv", "Export", Path.GetFileName(path), detail: "生产复盘报表");
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

        var range = GetTimeRange();
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
            AuditLog.Record("Export.Pdf", "Export", Path.GetFileName(path), detail: "生产复盘 PDF");
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
        IDefectHistoryReader? defectHistoryStore,
        IWorkOrderRepository? workOrderRepository,
        IProductionReviewAnalysisService analysisService,
        IProductionReviewDataService reviewDataService,
        IProductionReviewCsvExportService csvExportService,
        IProductionReviewChartService chartService,
        IProductionReviewMetricsService metricsService)
    {
        _deviceRepository = deviceRepository;
        _appSettings = appSettings;
        _dialog = dialog;
        _selection = selection;
        _pdfService = pdfService;
        _defectHistoryStore = defectHistoryStore;
        _workOrderRepository = workOrderRepository;
        _analysisService = analysisService;
        _reviewDataService = reviewDataService;
        _csvExportService = csvExportService;
        _chartService = chartService;
        _metricsService = metricsService;

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
        IDefectHistoryReader? defectHistoryStore = null,
        WorkOrderRepository? workOrderRepository = null,
        IProductionReviewAnalysisService? analysisService = null,
        IProductionReviewDataService? reviewDataService = null,
        IProductionReviewCsvExportService? csvExportService = null,
        IProductionReviewChartService? chartService = null,
        IProductionReviewMetricsService? metricsService = null)
        : this(
            deviceRepository,
            appSettings,
            dialog,
            selection,
            pdfService,
            defectHistoryStore,
            workOrderRepository,
            analysisService ?? new ProductionReviewAnalysisService(historyService, defectHistoryStore, workOrderRepository),
            reviewDataService ?? new ProductionReviewDataService(historyService),
            csvExportService ?? new ProductionReviewCsvExportService(),
            chartService ?? new ProductionReviewChartService(),
            metricsService ?? new ProductionReviewMetricsService(
                reviewDataService ?? new ProductionReviewDataService(historyService)))
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
        var (shift, _) = HistoryQueryHelper.FindCurrentShift(_appSettings.Shifts, now.TimeOfDay);
        if (shift == null)
        {
            CurrentShiftName = (_appSettings.Shifts == null || _appSettings.Shifts.Count == 0)
                ? Strings.M110
                : Strings.M111;
            CurrentShiftDateRange = string.Empty;
            return;
        }
        CurrentShiftName = $"{shift.Name} {shift.StartTime:hh\\:mm}-{shift.EndTime:hh\\:mm}";

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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => QueryData(devices, refreshVersion));
            sw.Stop();
            Log.Information("概览页刷新完成：设备 {Count} 台，耗时 {Elapsed}ms，SelectedDevice={DeviceId}",
                devices.Count, sw.ElapsedMilliseconds, SelectedDeviceId);
            if (!_uiDispatcher.HasShutdownStarted)
                await _uiDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
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

    // ──────────── 数据查询 ────────────

    /// <summary>
    /// 在后台线程执行全部查询：每台设备的生产/状态/报警数据，聚合后回写 UI 属性。
    /// </summary>
    private void QueryData(IReadOnlyList<Device> devices, int refreshVersion)
    {
        var (from, to) = GetTimeRange();
        if (devices.Count == 0)
        {
            ClearAll(refreshVersion);
            return;
        }

        // 按小时桶聚合全厂产量趋势
        var bucketSize = GetBucketSize();
        var buckets = _metricsService.BuildBuckets(from, to, (ProductionReviewBucketSize)bucketSize);
        var bucketOk = new int[buckets.Length];
        var bucketNg = new int[buckets.Length];

        List<DeviceOverviewSummary> deviceSummaries = [];

        int totalOk = 0, totalNg = 0;
        double totalRunSec = 0, totalAlarmSec = 0, totalPauseSec = 0;
        int totalAlarmCount = 0;
        int totalPendingAlarmCount = 0;
        string longestDowntimeDevice = string.Empty;
        string longestDowntimeAlarm = string.Empty;
        double maxDowntimeSec = 0;
        var selectedDevice = devices[0];

        // ── 批量查询：一次拉全量后内存分组，消除 foreach 内的 N+1 ──
        var deviceIds = devices.Select(d => d.Id).ToList();
        // 生产快照向窗口前扩展一天，用于跨班次窗口差分基线；状态/报警仍只查询窗口内数据。
        var swQuery = System.Diagnostics.Stopwatch.StartNew();
        var reviewData = _reviewDataService.QueryWindow(from, to, deviceIds);
        swQuery.Stop();
        var prodLogsByDevice = reviewData.ProductionLogsByDevice;
        var statusByDevice = reviewData.StatusTransitionsByDevice;
        var alarmByDevice = reviewData.AlarmEventsByDevice;
        Log.Information("[耗时] QueryWindow {Elapsed}ms 生产={Prod} 状态={Status} 报警={Alarm}",
            swQuery.ElapsedMilliseconds,
            prodLogsByDevice.Values.Sum(v => v.Count),
            statusByDevice.Values.Sum(v => v.Count),
            alarmByDevice.Values.Sum(v => v.Count));

        var swDevice = System.Diagnostics.Stopwatch.StartNew();
        var swDelta = System.Diagnostics.Stopwatch.StartNew();
        var deltaMs = 0L;

        foreach (var device in devices)
        {
            // ── 生产快照 ──
            prodLogsByDevice.TryGetValue(device.Id, out var allProdLogs);
            allProdLogs ??= [];
            var prodLogs = allProdLogs
                .Where(p => p.Timestamp >= from && p.Timestamp <= to)
                .OrderBy(p => p.Timestamp)
                .ToList();
            var baselineCandidates = allProdLogs
                .Where(p => p.Timestamp < from)
                .ToList();
            var (devOk, devNg) = HistoryQueryHelper.SumWindowProduction(
                prodLogs, baselineCandidates, from);
            // 保留首页既有语义：单条快照且无可用同班次基线时，
            // 将该快照视为当前累计产量；多条快照和跨班次数据仍按差分计算。
            if (prodLogs.Count == 1
                && HistoryQueryHelper.FindBaselineBeforeWindow(baselineCandidates, prodLogs[0].ShiftName) == null)
            {
                devOk = Math.Max(0, prodLogs[0].OkProduction);
                devNg = Math.Max(0, prodLogs[0].NgProduction);
            }
            var (devHourlyOk, devHourlyNg) = _metricsService.BuildProductionDeltas(
                allProdLogs,
                from,
                buckets,
                (ProductionReviewBucketSize)bucketSize);
            swDelta.Stop();
            deltaMs += swDelta.ElapsedMilliseconds;
            swDelta.Restart();

            // 汇总到全厂桶
            for (int i = 0; i < buckets.Length; i++)
            {
                bucketOk[i] += devHourlyOk[i];
                bucketNg[i] += devHourlyNg[i];
            }

            totalOk += devOk;
            totalNg += devNg;

            // ── 状态时长 ──
            statusByDevice.TryGetValue(device.Id, out var statusTransitions);
            statusTransitions ??= [];
            // GetLatestStatusBefore 仍是逐设备查询（窗口前最后一条），保留不变（查询量小且难以批量化）
            var lastBefore = _reviewDataService.GetLatestStatusBefore(device.Id, from);
            int initialState = lastBefore?.CurrentState ?? (int)DeviceStatus.Offline;
            var (runSec, alarmSec, pauseSec, _) = OeeCalculator.CalculateStateDurations(
                statusTransitions, from, to, initialState);
            totalRunSec += runSec;
            totalAlarmSec += alarmSec;
            totalPauseSec += pauseSec;

            // ── 报警事件 ──
            alarmByDevice.TryGetValue(device.Id, out var alarmEvents);
            alarmEvents ??= [];
            int devAlarmCount = alarmEvents.Count(e => e.EventType == AlarmEventType.Triggered);
            totalAlarmCount += devAlarmCount;

            // 待处理报警：最后一条是 Triggered 且无对应 Recovered
            var pendingCount = CountPendingAlarms(alarmEvents);
            totalPendingAlarmCount += pendingCount;

            // 最长停机报警（按报警 Id 分组，计算 Triggered 到 Recovered 的时长；未恢复按窗口终点截断）
            // 口径说明（2026-08-16）：此处刻意用「事件配对」而非状态段时长——本指标要回答"哪一条报警
            // 拖得最久"，需要把时长归因到具体报警名/设备，状态段时长只能给出聚合值无法归因。
            // 与下方 TotalDowntimeHours 等聚合指标（状态段口径，重叠报警不重复计时）口径不同：
            // 重叠报警下两者数值可能不一致，属预期而非 bug。
            var (topAlarmName, topDurationSec) = FindLongestAlarm(alarmEvents, to);
            if (topDurationSec > maxDowntimeSec)
            {
                maxDowntimeSec = topDurationSec;
                longestDowntimeDevice = device.Name;
                longestDowntimeAlarm = topAlarmName;
            }

            // 实时状态（从 RuntimeMap）
            int statusWord = (int)DeviceStatus.Offline;
            if (_deviceRepository.RuntimeMap.TryGetValue(device.Id, out var rt))
                statusWord = rt.StatusWord;

            // OEE 计算
            double devQuality = OeeCalculator.CalculateQualityRate(devOk, devNg);
            int targetCycle = device.TargetCycle;
            double devPerformance = OeeCalculator.CalculatePerformanceRate(devOk, devNg, targetCycle, runSec);
            double devAvailability = OeeCalculator.CalculateAvailabilityRate(runSec, alarmSec);
            double devOee = OeeCalculator.CalculateOee(devQuality, devPerformance, devAvailability);

            deviceSummaries.Add(new DeviceOverviewSummary
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                StatusWord = statusWord,
                OkCount = devOk,
                NgCount = devNg,
                QualityRate = devQuality,
                Oee = devOee,
                RunTimeHours = runSec / 3600.0,
                PausedTimeHours = pauseSec / 3600.0,
                AlarmDurationHours = alarmSec / 3600.0,
                StatusDistributionChart = ChartService.BuildStatusDistributionBarChart(runSec, alarmSec, pauseSec),
                AlarmCount = devAlarmCount,
                TopAlarmName = topAlarmName,
                HourlyOk = devHourlyOk,
            });
        }

        // ── 全厂 OEE 加权（用总产量和总时长计算，非各设备 OEE 平均）──
        double quality = OeeCalculator.CalculateQualityRate(totalOk, totalNg);
        // 性能率需要全厂 TargetCycle 加权，简化用所有设备 TargetCycle 的均值
        double avgTargetCycle = devices.Count > 0
            ? devices.Average(d => d.TargetCycle)
            : 0;
        double performance = OeeCalculator.CalculatePerformanceRate(totalOk, totalNg, (int)avgTargetCycle, totalRunSec);
        double availability = OeeCalculator.CalculateAvailabilityRate(totalRunSec, totalAlarmSec);
        double oee = OeeCalculator.CalculateOee(quality, performance, availability);
        var effectiveTo = to > DateTime.Now ? DateTime.Now : to;
        var targetOutput = (int)Math.Max(0, Math.Round(avgTargetCycle * Math.Max(0, (effectiveTo - from).TotalHours)));
        var outputAchievementRate = targetOutput > 0
            ? Math.Clamp((double)(totalOk + totalNg) / targetOutput, 0, 1)
            : 0;

        var comparisonRange = GetComparisonRange(from, to);
        var baselineMetrics = _metricsService.CalculateRangeMetrics(
            devices[0],
            comparisonRange.From,
            comparisonRange.To);
        var baselineTotalOutput = baselineMetrics.Ok + baselineMetrics.Ng;

        // ── 峰值/谷值小时 ──
        FindPeakValleyHour(buckets, bucketOk, bucketSize == BucketSize.Day,
            out var peakHour, out int peakOk, out var valleyHour, out int valleyOk);

        // ── 单设备复盘分析：由应用服务计算，ViewModel 只映射为绑定模型 ──
        // 只传所选设备报警（服务层另有 DeviceId 强制过滤，双层防护防止全厂报警串入）
        prodLogsByDevice.TryGetValue(selectedDevice.Id, out var selectedProductionLogs);
        selectedProductionLogs ??= [];
        var analysis = _analysisService.Analyze(
            selectedDevice,
            statusByDevice.GetValueOrDefault(selectedDevice.Id) ?? [],
            alarmByDevice.GetValueOrDefault(selectedDevice.Id) ?? [],
            selectedProductionLogs,
            from,
            to,
            comparisonRange.From,
            comparisonRange.To);
        var topAlarms = analysis.Alarms.Select((item, index) => new AlarmOverviewSummary
        {
            Rank = index + 1, // 方案 A 排名徽章（2026-08-11）
            AlarmName = item.AlarmName,
            DeviceName = item.DeviceName,
            PlcAddress = item.PlcAddress,
            TriggerCount = item.TriggerCount,
            AverageIntervalMinutes = item.AverageIntervalMinutes,
            IsHighFrequency = item.IsHighFrequency,
            OutputBefore = item.OutputBefore,
            OutputAfter = item.OutputAfter,
            ShiftName = item.ShiftName,
            TotalDurationHours = item.TotalDurationHours,
        }).ToList();
        var statusTimeline = analysis.StatusTimeline.Select(item => new ReviewStatusSegment
        {
            Start = item.Start,
            End = item.End,
            StatusText = item.StatusText,
            StatusWord = item.StatusWord,
            OutputDelta = item.OutputDelta,
            AlarmCount = item.AlarmCount,
            HasNoOutput = item.HasNoOutput,
        }).ToList();
        var healthIssues = analysis.HealthIssues.ToList();
        var healthScore = analysis.HealthScore;
        var workOrderText = analysis.WorkOrderText;
        var productText = analysis.ProductText;
        var recipeText = analysis.RecipeText;

        // ── 回写 UI 属性：查询在线程池执行，所有绑定属性必须回到 UI 线程 ──
        void ApplyKpis()
        {
            if (refreshVersion != Volatile.Read(ref _refreshVersion)) return;
            TotalOk = totalOk;
            TotalNg = totalNg;
            AlarmCount = totalAlarmCount;
            PendingAlarmCount = totalPendingAlarmCount;
            PeakHour = peakHour;
            PeakHourOk = peakOk;
            ValleyHour = valleyHour;
            ValleyHourOk = valleyOk;
            LongestDowntimeDevice = longestDowntimeDevice;
            LongestDowntimeAlarm = longestDowntimeAlarm;
            LongestDowntimeHours = maxDowntimeSec / 3600.0;
            QualityRate = quality;
            Oee = oee;
            RunTimeHours = totalRunSec / 3600.0;
            PausedTimeHours = totalPauseSec / 3600.0;
            AlarmDurationHours = totalAlarmSec / 3600.0;
            TargetOutput = targetOutput;
            OutputAchievementRate = outputAchievementRate;
            ComparisonLabel = comparisonRange.Label;
            BaselineTotalOutput = baselineTotalOutput;
            BaselineQualityRate = baselineMetrics.QualityRate;
            BaselineOee = baselineMetrics.Oee;
            OutputDelta = totalOk + totalNg - baselineTotalOutput;
            QualityRateDelta = quality - baselineMetrics.QualityRate;
            OeeDelta = oee - baselineMetrics.Oee;
            // 聚合停机/报警指标统一用状态段口径（CalculateStateDurations：重叠报警不重复计时）。
            // 注意与 LongestDowntimeHours（事件配对口径，需归因到具体报警名）不同，见设备循环内注释。
            TotalDowntimeHours = (totalAlarmSec + totalPauseSec) / 3600.0;
            AverageAlarmDurationMinutes = totalAlarmCount > 0
                ? totalAlarmSec / totalAlarmCount / 60.0
                : 0;
            MtbfHours = totalAlarmCount > 0
                ? totalRunSec / totalAlarmCount / 3600.0
                : totalRunSec / 3600.0;
            AvailabilityLossText = string.Format(Strings.F079, availability, (1 - availability));
            PerformanceLossText = string.Format(Strings.F124, performance, (1 - performance));
            QualityLossText = string.Format(Strings.F193, quality, (1 - quality));
            HealthScore = healthScore;
            CurrentWorkOrderText = workOrderText;
            CurrentProductText = productText;
            CurrentRecipeText = recipeText;
        }

        if (!_uiDispatcher.HasShutdownStarted)
            // BeginInvoke 而非 Invoke：RefreshAsync 在后台线程调用，Invoke 会同步阻塞后台线程
            // 直到 UI 执行完 ApplyKpis（可能拖慢后续班次对比/图表构建，与下方更新集合的注释一致）。
            _uiDispatcher.BeginInvoke(ApplyKpis);

        swDevice.Stop();
        Log.Information("[耗时] 设备循环 {Elapsed}ms（其中 BuildProductionDeltas 累计 {Delta}ms）",
            swDevice.ElapsedMilliseconds, deltaMs);

        // ── 班次对比：复用 QueryData 已查的批量数据，按 ShiftName 内存分组，不再重新查询 ──
        var swShifts = System.Diagnostics.Stopwatch.StartNew();
        var shiftComparisons = _metricsService.BuildShiftComparisons(
                prodLogsByDevice,
                statusByDevice,
                alarmByDevice,
                devices,
                _appSettings.Shifts,
                from,
                to)
            .Select(item => new ShiftComparisonSummary
            {
                ShiftName = item.ShiftName,
                OkCount = item.OkCount,
                NgCount = item.NgCount,
                AlarmCount = item.AlarmCount,
                Oee = item.Oee,
                RunTimeHours = item.RunTimeHours,
                AlarmDurationHours = item.AlarmDurationHours,
                TargetAchievementRate = item.TargetAchievementRate,
            })
            .ToList();
        swShifts.Stop();
        Log.Information("[耗时] 班次对比 {Elapsed}ms", swShifts.ElapsedMilliseconds);

        // ── 缺陷帕累托：从内存 Defect.Count 快照聚合（无历史持久化，取当前累计值） ──
        var swDefect = System.Diagnostics.Stopwatch.StartNew();
        var defectParetos = BuildDefectParetos(devices[0], from, to);
        var reviewConclusions = BuildReviewConclusions(
            totalOk, totalNg, totalAlarmCount, maxDowntimeSec,
            longestDowntimeDevice, longestDowntimeAlarm,
            shiftComparisons, defectParetos, quality, oee);
        swDefect.Stop();
        Log.Information("[耗时] 缺陷帕累托+结论 {Elapsed}ms", swDefect.ElapsedMilliseconds);

        // ── 更新集合与图表（ObservableCollection 修改必须在 UI 线程）──
        var sortedSummaries = deviceSummaries.OrderByDescending(d => d.Oee).ToList();
        // 用 BeginInvoke 替代 Invoke：RefreshAsync 由后台线程调用，Invoke 会阻塞后台线程直到 UI 排队任务执行，
        // 长时间运行会导致采集线程被卡住；BeginInvoke 异步派发到 UI 线程，不阻塞调用方。
        // 应用关闭时 Dispatcher 可能已终止，HasShutdownStarted 守卫避免 DispatcherOperation 创建异常。
        if (!_uiDispatcher.HasShutdownStarted)
        {
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (refreshVersion != Volatile.Read(ref _refreshVersion)) return;
                    // 差分同步（审查修复 2026-08-13）：原 7 个集合全量 Clear+逐项 Add，
                    // 60s 定时刷新 + 切设备/时间范围都会触发一次，列表闪烁、滚动位置丢失、GC 压力大；
                    // 改用 ObservableCollectionSyncHelper.Sync 按引用差分（与 HomeViewModel 同源）
                    ObservableCollectionSyncHelper.Sync(DeviceSummaries, sortedSummaries);
                    ObservableCollectionSyncHelper.Sync(TopAlarms, topAlarms);
                    ObservableCollectionSyncHelper.Sync(ShiftComparisons, shiftComparisons);
                    ObservableCollectionSyncHelper.Sync(DefectParetos, defectParetos);
                    ObservableCollectionSyncHelper.Sync(StatusTimeline, statusTimeline);
                    ObservableCollectionSyncHelper.Sync(HeatmapBuckets, HeatmapBucketBuilder.Build(statusTimeline));
                    ObservableCollectionSyncHelper.Sync(HealthIssues, healthIssues);
                    ObservableCollectionSyncHelper.Sync(ReviewConclusions, reviewConclusions);

                // 图表签名比对：数据未变时跳过 PlotModel 重建（避免 60s 定时刷新的无谓 CPU 开销与 UI 闪烁）
                // 时间戳不纳入签名（允许时间标签最多滞后一个刷新周期），仅比较产量/率值/设备明细
                var signature = ComputeChartSignature(bucketOk, bucketNg, oee, performance,
                    availability, quality, avgTargetCycle, SelectedTimeRange, sortedSummaries, defectParetos);
                if (signature != _cachedChartSignature)
                {
                    Log.Information("图表重建：签名变化 {Sig}（旧 {Old}），构建趋势/瀑布/热力", signature, _cachedChartSignature);
                    TrendChart = _chartService.BuildTrend(
                        buckets,
                        bucketOk,
                        bucketNg,
                        (ProductionReviewBucketSize)bucketSize,
                        avgTargetCycle,
                        devices.Count,
                        _appSettings.Shifts,
                        CreateChartPalette());
                    OnPropertyChanged(nameof(TrendChart));
                    HasTrendData = bucketOk.Any(v => v > 0) || bucketNg.Any(v => v > 0);

                    OeeWaterfallChart = _chartService.BuildOeeWaterfall(
                        performance,
                        availability,
                        quality,
                        oee,
                        CreateChartPalette());
                    Log.Information("OeeWaterfallChart 已构建：perf={P:P1} avail={A:P1} qual={Q:P1} oee={O:P1}",
                        performance, availability, quality, oee);
                    OnPropertyChanged(nameof(OeeWaterfallChart));

                    ProductionHeatmapChart = _chartService.BuildHeatmap(
                        buckets,
                        sortedSummaries
                            .Select(summary => new ProductionReviewHeatmapRow(summary.DeviceName, summary.HourlyOk))
                            .ToList(),
                        (ProductionReviewBucketSize)bucketSize,
                        CreateChartPalette());
                    OnPropertyChanged(nameof(ProductionHeatmapChart));

                    DefectParetoChart = _chartService.BuildDefectParetoChart(
                        defectParetos,
                        CreateChartPalette());
                    OnPropertyChanged(nameof(DefectParetoChart));

                    _cachedChartSignature = signature;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "概览页 UI 集合/图表更新失败（refreshVersion={Version}）", refreshVersion);
            }
            }));
        }
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

    // ──────────── 时间范围与桶 ────────────

    private (DateTime From, DateTime To) GetTimeRange()
    {
        var now = DateTime.Now;
        return SelectedTimeRange switch
        {
            OverviewTimeRange.CurrentShift => ResolveCurrentShiftRange(now),
            OverviewTimeRange.PreviousShift => ResolvePreviousShiftRange(now),
            OverviewTimeRange.Today => (now.Date, now),
            OverviewTimeRange.Hour1 => (now.AddHours(-1), now),
            OverviewTimeRange.Hours8 => (now.AddHours(-8), now),
            OverviewTimeRange.Hours24 => (now.AddHours(-24), now),
            OverviewTimeRange.Days7 => (now.AddDays(-7), now),
            _ => (now.AddHours(-24), now),
        };
    }

    private (DateTime From, DateTime To, string Label) GetComparisonRange(DateTime from, DateTime to)
    {
        var duration = to - from;
        var now = DateTime.Now;
        return SelectedTimeRange switch
        {
            OverviewTimeRange.CurrentShift => GetPreviousShiftComparison(now),
            OverviewTimeRange.PreviousShift => (from - duration, from, Strings.M_EarlierShift),
            OverviewTimeRange.Today => (from.AddDays(-1), from, Strings.K257),
            OverviewTimeRange.Hour1 => (from.AddHours(-1), from, Strings.M301),
            OverviewTimeRange.Hours8 => (from.AddHours(-8), from, Strings.M302),
            OverviewTimeRange.Hours24 => (from.AddDays(-1), from, Strings.M300),
            OverviewTimeRange.Days7 => (from.AddDays(-7), from, Strings.M303),
            _ => (from - duration, from, Strings.M058),
        };
    }

    private (DateTime From, DateTime To, string Label) GetPreviousShiftComparison(DateTime now)
    {
        var shifts = _appSettings.Shifts;
        var (current, currentIndex) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (current == null || shifts == null || shifts.Count == 0)
            return (now.AddHours(-24), now, Strings.M300);
        var currentRange = current.ResolveRange(now);
        var previousIndex = (currentIndex - 1 + shifts.Count) % shifts.Count;
        var previous = shifts[previousIndex];
        var range = previous.ResolveRange(currentRange.Start.AddMinutes(-1));
        return (range.Start, range.End, string.Format(Strings.F055, previous.Name));
    }

    private (DateTime From, DateTime To) ResolveCurrentShiftRange(DateTime now)
    {
        var (shift, _) = HistoryQueryHelper.FindCurrentShift(_appSettings.Shifts, now.TimeOfDay);
        if (shift == null) return (now.AddHours(-24), now);
        var range = shift.ResolveRange(now);
        return (range.Start, now);
    }

    private (DateTime From, DateTime To) ResolvePreviousShiftRange(DateTime now)
    {
        var shifts = _appSettings.Shifts;
        var (current, currentIndex) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (current == null || shifts == null || shifts.Count == 0)
            return (now.AddHours(-24), now);

        var currentRange = current.ResolveRange(now);
        var previousIndex = (currentIndex - 1 + shifts.Count) % shifts.Count;
        var previous = shifts[previousIndex];
        var previousRange = previous.ResolveRange(currentRange.Start.AddMinutes(-1));
        return previousRange;
    }

    /// <summary>桶大小：1h→5分钟，8h/24h→1小时，7d→1天。</summary>
    private enum BucketSize { Minute5, Hour, Day }

    private BucketSize GetBucketSize()
    {
        return SelectedTimeRange switch
        {
            OverviewTimeRange.Hour1 => BucketSize.Minute5,
            OverviewTimeRange.Hours8 => BucketSize.Hour,
            OverviewTimeRange.Hours24 => BucketSize.Hour,
            OverviewTimeRange.Days7 => BucketSize.Day,
            _ => BucketSize.Hour,
        };
    }
    private static int CountPendingAlarms(List<AlarmEventRecord> events)
    {
        // 按 AlarmId 分组，最后一条是 Triggered 且无 Recovered → 待处理
        return events
            .GroupBy(e => e.AlarmId)
            .Count(g => g.OrderByDescending(e => e.EventTime).First().EventType == AlarmEventType.Triggered);
    }

    /// <summary>
    /// 计算 Triggered→Recovered 配对时长：按事件顺序扫描，每个 Triggered 配下一个 Recovered，算时长。
    /// P2-13 口径修复：① 未恢复 Triggered 用 min(now, windowTo) 截断（历史窗口不再算到"现在"）；
    /// ② 每个 Recovered 只消费一次（连续 T1,T2,R1 时 T1 配 R1、T2 未恢复按截断值，避免重复配对高估）。
    /// </summary>
    private static Dictionary<string, double> PairAlarmDurations(
        IEnumerable<AlarmEventRecord> events,
        Func<AlarmEventRecord, string> keySelector,
        DateTime windowTo)
    {
        return events
            .GroupBy(keySelector)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var sorted = g.OrderBy(e => e.EventTime).ToList();
                    double total = 0;
                    var consumed = new HashSet<AlarmEventRecord>();
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (sorted[i].EventType != AlarmEventType.Triggered) continue;
                        // 找下一个未消费的 Recovered；未恢复则截断到 min(now, windowTo)
                        DateTime end = windowTo < DateTime.Now ? windowTo : DateTime.Now;
                        for (int j = i + 1; j < sorted.Count; j++)
                        {
                            if (sorted[j].EventType == AlarmEventType.Recovered && consumed.Add(sorted[j]))
                            {
                                end = sorted[j].EventTime;
                                break;
                            }
                        }
                        total += (end - sorted[i].EventTime).TotalSeconds;
                    }
                    return total;
                });
    }

    /// <summary>
    /// 找持续时间最长的报警（按 AlarmName 分组取 Triggered→Recovered 配对总时长最大者）。
    /// 复用 PairAlarmDurations 避免 FindLongestAlarm 与 CalcAlarmDurationHours 重复实现配对逻辑。
    /// </summary>
    private static (string Name, double Seconds) FindLongestAlarm(List<AlarmEventRecord> events, DateTime windowTo)
    {
        if (events.Count == 0) return (string.Empty, 0);
        var durations = PairAlarmDurations(events, e => e.AlarmName, windowTo);
        if (durations.Count == 0) return (string.Empty, 0);
        var max = durations.Aggregate((a, b) => a.Value >= b.Value ? a : b);
        return (max.Key, max.Value);
    }

    // ──────────── 峰值/谷值 ────────────

    private static void FindPeakValleyHour(DateTime[] buckets, int[] okCounts, bool dayBuckets,
        out string peakHour, out int peakOk, out string valleyHour, out int valleyOk)
    {
        peakHour = "—"; valleyHour = "—";
        peakOk = 0; valleyOk = 0;
        if (buckets.Length == 0) return;

        int maxIdx = 0, minIdx = 0;
        for (int i = 1; i < okCounts.Length; i++)
        {
            if (okCounts[i] > okCounts[maxIdx]) maxIdx = i;
            if (okCounts[i] < okCounts[minIdx]) minIdx = i;
        }
        peakOk = okCounts[maxIdx];
        valleyOk = okCounts[minIdx];
        // 审查修复 2026-08-13：天粒度桶（近 7 天）的时间分量恒为 00:00——"HH:mm" 标签全部显示 00:00，
        // 用户无法知道峰值是哪一天；按桶粒度区分格式
        var format = dayBuckets ? "MM-dd" : "HH:mm";
        peakHour = buckets[maxIdx].ToString(format);
        valleyHour = buckets[minIdx].ToString(format);
    }

    /// <summary>
    /// 从内存中的 Defect.Count 聚合缺陷帕累托数据。
    /// 由于缺陷计数无历史持久化，此处取当前设备配置中各缺陷的累计 Count 快照。
    /// 按数量降序排列并计算累计占比。
    /// </summary>
    private List<DefectParetoSummary> BuildDefectParetos(Device device, DateTime from, DateTime to)
    {
        if (_defectHistoryStore == null)
            return [];

        var snapshots = _defectHistoryStore.QueryWindowBounds(from, to, device.Id);
        var list = new List<DefectParetoSummary>();
        foreach (var group in snapshots.GroupBy(snapshot => new { snapshot.DefectId, snapshot.ShiftName }))
        {
            var ordered = group.OrderBy(snapshot => snapshot.Timestamp).ToList();
            var window = ordered.Where(snapshot => snapshot.Timestamp >= from && snapshot.Timestamp <= to).ToList();
            if (window.Count == 0) continue;
            var first = window[0];
            var last = window[^1];
            var baseline = ordered.LastOrDefault(snapshot => snapshot.Timestamp < from);
            // 口径统一（2026-08-16）：一律按窗口内增量算——
            // 有窗口前基线用 末值-基线（完整窗口增量）；无基线用 窗口内末值-首值；
            // 窗口内仅一条且无基线时增量无法推算，取 0（原实现把累计值当增量，会高估）。
            var count = baseline != null
                ? Math.Max(0, last.Count - baseline.Count)
                : Math.Max(0, last.Count - first.Count);
            if (count <= 0) continue;
            list.Add(new DefectParetoSummary
            {
                DefectName = last.DefectName,
                DeviceName = last.DeviceName,
                Count = count,
            });
        }

        // 按数量降序取 Top3（2026-08-10 用户要求：卡片最多显示 3 条，突出头部缺陷）
        list = list.OrderByDescending(d => d.Count).Take(3).ToList();
        var total = list.Sum(d => d.Count);
        if (total <= 0) return list;

        double cum = 0;
        foreach (var d in list)
        {
            cum += d.Count;
            d.CumulativePercent = cum * 100.0 / total;
        }
        return list;
    }

    private static List<ReviewConclusion> BuildReviewConclusions(
        int totalOk,
        int totalNg,
        int totalAlarmCount,
        double longestDowntimeSec,
        string longestDowntimeDevice,
        string longestDowntimeAlarm,
        IReadOnlyList<ShiftComparisonSummary> shifts,
        IReadOnlyList<DefectParetoSummary> defects,
        double quality,
        double oee)
    {
        List<ReviewConclusion> result = [];
        var total = totalOk + totalNg;
        if (total == 0 && totalAlarmCount == 0)
            return [new ReviewConclusion { Text = Strings.M114, Kind = ReviewConclusionKind.Info, Met = ReviewConclusionMetState.Neutral }];

        if (total > 0)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F194, quality, (quality >= QualityTarget ? Strings.M112 : Strings.M113), QualityTarget),
                Kind = ReviewConclusionKind.Quality,
                Met = quality >= QualityTarget ? ReviewConclusionMetState.Met : ReviewConclusionMetState.NotMet,
            });
        }

        if (oee > 0)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F006, oee, (oee >= OeeTarget ? Strings.M112 : Strings.M113), OeeTarget),
                Kind = ReviewConclusionKind.Oee,
                Met = oee >= OeeTarget ? ReviewConclusionMetState.Met : ReviewConclusionMetState.NotMet,
            });
        }

        if (longestDowntimeSec > 0)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F138, longestDowntimeSec / 3600.0, longestDowntimeDevice, longestDowntimeAlarm),
                Kind = ReviewConclusionKind.Downtime,
                Met = ReviewConclusionMetState.Neutral,
            });
        }

        var topShift = shifts.Where(s => s.TotalCount > 0).OrderByDescending(s => s.TotalCount).FirstOrDefault();
        if (topShift != null)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F041, topShift.ShiftName, topShift.TotalCount, topShift.OkRatio),
                Kind = ReviewConclusionKind.BestShift,
                Met = ReviewConclusionMetState.Neutral,
            });
        }

        var topDefect = defects.FirstOrDefault();
        if (topDefect != null)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F059, topDefect.DefectName, topDefect.DeviceName, topDefect.Count, topDefect.CumulativePercent),
                Kind = ReviewConclusionKind.TopDefect,
                Met = ReviewConclusionMetState.Neutral,
            });
        }

        if (totalAlarmCount > 0 && result.Count < 5)
        {
            result.Add(new ReviewConclusion
            {
                Text = string.Format(Strings.F123, totalAlarmCount),
                Kind = ReviewConclusionKind.AlarmCount,
                Met = ReviewConclusionMetState.Neutral,
            });
        }

        return result.Take(5).ToList();
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
