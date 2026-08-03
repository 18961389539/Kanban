using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using Serilog;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 概览页时间范围枚举。
/// </summary>
public enum OverviewTimeRange
{
    CurrentShift,
    PreviousShift,
    Today,
    Hour1,
    Hours8,
    Hours24,
    Days7,
}

/// <summary>
/// 设备概览明细（一行）：用于概览页底部紧凑表格。
/// </summary>
public class DeviceOverviewSummary
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int StatusWord { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public double OkRatio => TotalCount > 0 ? (double)OkCount / TotalCount : 0;
    public double NgRatio => TotalCount > 0 ? (double)NgCount / TotalCount : 0;
    public int TotalCount => OkCount + NgCount;
    public double QualityRate { get; set; }
    public double Oee { get; set; }
    public double RunTimeHours { get; set; }
    public double PausedTimeHours { get; set; }
    public double AlarmDurationHours { get; set; }
    public PlotModel StatusDistributionChart { get; set; } = ChartService.BuildStatusDistributionBarChart(0, 0, 0);
    public int AlarmCount { get; set; }
    public string TopAlarmName { get; set; } = string.Empty;
    public IReadOnlyList<int> HourlyOk { get; set; } = Array.Empty<int>();
}

/// <summary>
/// Top 报警摘要：用于概览页右侧 Top 5 报警列表。
/// </summary>
public class AlarmOverviewSummary
{
    public string AlarmName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string PlcAddress { get; set; } = string.Empty;
    public int TriggerCount { get; set; }
    public int RepeatCount => Math.Max(0, TriggerCount - 1);
    public double AverageIntervalMinutes { get; set; }
    public bool IsHighFrequency { get; set; }
    public int OutputBefore { get; set; }
    public int OutputAfter { get; set; }
    public int OutputDelta => OutputAfter - OutputBefore;
    public string ShiftName { get; set; } = string.Empty;
    public double TotalDurationHours { get; set; }
}

/// <summary>当前设备状态时间段：支持点击查看该时段的报警与产量。</summary>
public sealed class ReviewStatusSegment
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public int StatusWord { get; init; }
    public int OutputDelta { get; init; }
    public int AlarmCount { get; init; }
    public bool HasNoOutput { get; init; }
    public double DurationMinutes => Math.Max(0, (End - Start).TotalMinutes);
    public string TimeRangeText => $"{Start:HH:mm:ss} - {End:HH:mm:ss}";
    public string DurationText => DurationMinutes >= 60
        ? $"{DurationMinutes / 60:F1} h"
        : $"{DurationMinutes:F0} min";
    public string OutputText => string.Format(Strings.F061, OutputDelta, AlarmCount);
}

/// <summary>缺陷按班次和时间段聚合的集中度摘要。</summary>
public sealed class DefectConcentrationSummary
{
    public string DefectName { get; init; } = string.Empty;
    public string ShiftName { get; init; } = string.Empty;
    public string TimeRangeText { get; init; } = string.Empty;
    public int Count { get; init; }
    public double Share { get; init; }
}

/// <summary>
/// 班次对比摘要（一行）：用于概览页班次对比表格。
/// 按班次聚合时间窗口内的产量/报警等核心指标，让管理者横向对比班次表现。
/// </summary>
public class ShiftComparisonSummary
{
    public string ShiftName { get; set; } = string.Empty;
    public int OkCount { get; set; }
    public int NgCount { get; set; }
    public int TotalCount => OkCount + NgCount;
    public int AlarmCount { get; set; }
    public double OkRatio => TotalCount > 0 ? (double)OkCount / TotalCount : 0;
    public double NgRatio => TotalCount > 0 ? (double)NgCount / TotalCount : 0;
    public double AlarmRate => TotalCount > 0 ? (double)AlarmCount / TotalCount : 0;
    public double Oee { get; set; }
    public double RunTimeHours { get; set; }
    public double AlarmDurationHours { get; set; }
    public double TargetAchievementRate { get; set; }
}

/// <summary>
/// 缺陷帕累托摘要（一行）：用于概览页缺陷帕累托图。
/// 按缺陷名称聚合数量，按数量降序排列，累计占比用于帕累托折线。
/// </summary>
public class DefectParetoSummary
{
    public string DefectName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int Count { get; set; }
    public double CumulativePercent { get; set; }
}

/// <summary>
/// 最近 N 小时生产概览 ViewModel。
/// 汇总全厂产量/报警，按小时聚合趋势，列出 Top 报警和设备明细（每台设备独立 OEE）。
/// 数据来源：HistoryService（历史快照）+ DeviceRepository.Runtimes（实时状态色条）。
/// </summary>
public partial class OverviewViewModel : ObservableObject, IDisposable
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

    public string SelectedDeviceName => DeviceFilterItems.FirstOrDefault(d => d.Id == SelectedDeviceId)?.Name ?? "未选择设备";

    [ObservableProperty] private int _targetOutput;
    [ObservableProperty] private double _outputAchievementRate;
    [ObservableProperty] private string _comparisonLabel = "上一周期";
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
    [ObservableProperty] private string _currentWorkOrderText = "暂无运行工单";
    [ObservableProperty] private string _currentProductText = "暂无产品信息";
    [ObservableProperty] private string _currentRecipeText = "暂无配方信息";
    [ObservableProperty] private ReviewStatusSegment? _selectedTimelineSegment;

    public string HealthScoreText => $"{HealthScore} / 100";
    public string TimelineSelectionText => SelectedTimelineSegment == null
        ? "点击任一状态时段查看对应报警与产量"
        : $"{SelectedTimelineSegment.TimeRangeText} · {SelectedTimelineSegment.StatusText} · {SelectedTimelineSegment.OutputText}";

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

    public const double QualityTarget = 0.95;
    public const double OeeTarget = 0.85;

    public bool HasProductionData => TotalOk > 0 || TotalNg > 0;
    public bool HasAlarmData => AlarmCount > 0;
    public bool HasStatusData => RunTimeHours > 0 || AlarmDurationHours > 0 || PausedTimeHours > 0;
    public bool HasAnyHistoryData => HasProductionData || HasAlarmData || HasStatusData;
    public string DataCoverageText => !HasAnyHistoryData
        ? "当前范围未采集到历史数据"
        : !HasProductionData
            ? "当前范围仅有状态数据，暂无产量记录"
            : !HasAlarmData
                ? "当前范围有产量记录，暂无报警记录"
                : "产量、状态和报警数据均已采集";
    public string TargetStatusText => string.Format(Strings.F192, QualityRate, QualityTarget, Oee, OeeTarget);

    private static string FormatSigned(int value) => value > 0 ? $"+{value:N0}" : value.ToString("N0");
    private static string FormatPercentageDelta(double value) => value > 0 ? $"+{value:P1}" : value.ToString("P1");

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
        new OverviewTimeRangeOption(OverviewTimeRange.CurrentShift, "当前班次"),
        new OverviewTimeRangeOption(OverviewTimeRange.PreviousShift, "上一班次"),
        new OverviewTimeRangeOption(OverviewTimeRange.Today, "今日"),
        new OverviewTimeRangeOption(OverviewTimeRange.Hour1, "近1小时"),
        new OverviewTimeRangeOption(OverviewTimeRange.Hours8, "近8小时"),
        new OverviewTimeRangeOption(OverviewTimeRange.Hours24, "近24小时"),
        new OverviewTimeRangeOption(OverviewTimeRange.Days7, "近7天"),
    };

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExportingReport;

    public bool HasData => HasAnyHistoryData;

    [ObservableProperty] private double _runTimeHours;
    [ObservableProperty] private double _pausedTimeHours;
    [ObservableProperty] private double _alarmDurationHours;

    partial void OnTotalOkChanged(int value)
    {
        NotifyDataCoverageChanged();
        ExportReportPdfCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ComparisonSummaryText));
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
    partial void OnSelectedTimelineSegmentChanged(ReviewStatusSegment? value)
        => OnPropertyChanged(nameof(TimelineSelectionText));

    [RelayCommand]
    private void SelectTimelineSegment(ReviewStatusSegment? segment)
        => SelectedTimelineSegment = segment;

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
            "导出生产复盘报表",
            $"生产复盘_{DateTime.Now:yyyyMMddHHmm}.csv",
            "CSV 文件|*.csv|所有文件|*.*");
        if (string.IsNullOrWhiteSpace(path)) return;

        var (from, to) = GetTimeRange();
        var csv = _csvExportService.Build(new ProductionReviewCsvData(
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
            TopAlarms.ToList()));
        IsExportingReport = true;
        try
        {
            await Task.Run(() => File.WriteAllText(path, csv, new UTF8Encoding(true)));
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
            "导出生产复盘 PDF",
            $"生产复盘_{DateTime.Now:yyyyMMddHHmm}.pdf",
            "PDF 文件|*.pdf|所有文件|*.*");
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
    public ObservableCollection<DefectConcentrationSummary> DefectConcentrations { get; } = new();
    public ObservableCollection<string> HealthIssues { get; } = new();

    /// <summary>基于当前时间范围生成的事实型复盘结论，不包含无法追溯的主观判断。</summary>
    public ObservableCollection<string> ReviewConclusions { get; } = new();

    /// <summary>OEE 瀑布图模型（P→A→Q→OEE 损失拆解）。</summary>
    public PlotModel? OeeWaterfallChart { get; private set; }

    /// <summary>时段产量热力图模型（设备 × 时段）。</summary>
    public PlotModel? ProductionHeatmapChart { get; private set; }

    /// <summary>
    /// 点击设备行：设置主页聚焦设备并发起跳转请求。
    /// MainWindowViewModel 订阅 FocusDeviceRequested 完成导航到主页。
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
    /// 兼容旧测试和外部调用方的构造入口。生产 DI 使用上面的分层构造，
    /// 此入口仅负责把旧历史门面适配为复盘应用服务。
    /// </summary>
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
                ? "未配置班次"
                : "未匹配班次";
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
            await Task.Run(() => QueryData(devices, refreshVersion));
            if (!_uiDispatcher.HasShutdownStarted)
                await _uiDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            LastUpdateTime = DateTime.Now;
            UpdateCurrentShiftName();
            OnPropertyChanged(nameof(HasData));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "概览页数据查询失败");
            _dialog.NotifyError(string.Format(Strings.F085, ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

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
        List<AlarmEventRecord> allAlarmEvents = [];

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
        var reviewData = _reviewDataService.QueryWindow(from, to, deviceIds);
        var prodLogsByDevice = reviewData.ProductionLogsByDevice;
        var statusByDevice = reviewData.StatusTransitionsByDevice;
        var alarmByDevice = reviewData.AlarmEventsByDevice;

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
            int initialState = lastBefore?.CurrentState ?? (int)DeviceStatus.Unknown;
            var (runSec, alarmSec, pauseSec) = OeeCalculator.CalculateStateDurations(
                statusTransitions, from, to, initialState);
            totalRunSec += runSec;
            totalAlarmSec += alarmSec;
            totalPauseSec += pauseSec;

            // ── 报警事件 ──
            alarmByDevice.TryGetValue(device.Id, out var alarmEvents);
            alarmEvents ??= [];
            allAlarmEvents.AddRange(alarmEvents);
            int devAlarmCount = alarmEvents.Count(e => e.EventType == AlarmEventType.Triggered);
            totalAlarmCount += devAlarmCount;

            // 待处理报警：最后一条是 Triggered 且无对应 Recovered
            var pendingCount = CountPendingAlarms(alarmEvents);
            totalPendingAlarmCount += pendingCount;

            // 最长停机报警（按报警 Id 分组，计算 Triggered 到 Recovered 的时长）
            var (topAlarmName, topDurationSec) = FindLongestAlarm(alarmEvents);
            if (topDurationSec > maxDowntimeSec)
            {
                maxDowntimeSec = topDurationSec;
                longestDowntimeDevice = device.Name;
                longestDowntimeAlarm = topAlarmName;
            }

            // 实时状态（从 RuntimeMap）
            int statusWord = (int)DeviceStatus.Unknown;
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
        FindPeakValleyHour(buckets, bucketOk, out var peakHour, out int peakOk, out var valleyHour, out int valleyOk);

        // ── 单设备复盘分析：由应用服务计算，ViewModel 只映射为绑定模型 ──
        prodLogsByDevice.TryGetValue(selectedDevice.Id, out var selectedProductionLogs);
        selectedProductionLogs ??= [];
        var analysis = _analysisService.Analyze(
            selectedDevice,
            statusByDevice.GetValueOrDefault(selectedDevice.Id) ?? [],
            allAlarmEvents,
            selectedProductionLogs,
            from,
            to,
            comparisonRange.From,
            comparisonRange.To);
        var topAlarms = analysis.Alarms.Select(item => new AlarmOverviewSummary
        {
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
        var defectConcentrations = analysis.DefectConcentrations.Select(item => new DefectConcentrationSummary
        {
            DefectName = item.DefectName,
            ShiftName = item.ShiftName,
            TimeRangeText = item.TimeRangeText,
            Count = item.Count,
            Share = item.Share,
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

        // ── 班次对比：复用 QueryData 已查的批量数据，按 ShiftName 内存分组，不再重新查询 ──
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

        // ── 缺陷帕累托：从内存 Defect.Count 快照聚合（无历史持久化，取当前累计值） ──
        var defectParetos = BuildDefectParetos(devices[0], from, to);
        var reviewConclusions = BuildReviewConclusions(
            totalOk, totalNg, totalAlarmCount, maxDowntimeSec,
            longestDowntimeDevice, longestDowntimeAlarm,
            shiftComparisons, defectParetos, quality, oee);

        // ── 更新集合与图表（ObservableCollection 修改必须在 UI 线程）──
        var sortedSummaries = deviceSummaries.OrderByDescending(d => d.Oee).ToList();
        // 用 BeginInvoke 替代 Invoke：RefreshAsync 由后台线程调用，Invoke 会阻塞后台线程直到 UI 排队任务执行，
        // 长时间运行会导致采集线程被卡住；BeginInvoke 异步派发到 UI 线程，不阻塞调用方。
        // 应用关闭时 Dispatcher 可能已终止，HasShutdownStarted 守卫避免 DispatcherOperation 创建异常。
        if (!_uiDispatcher.HasShutdownStarted)
        {
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                if (refreshVersion != Volatile.Read(ref _refreshVersion)) return;
                DeviceSummaries.Clear();
                foreach (var ds in sortedSummaries)
                    DeviceSummaries.Add(ds);

                TopAlarms.Clear();
                foreach (var a in topAlarms)
                    TopAlarms.Add(a);

                ShiftComparisons.Clear();
                foreach (var s in shiftComparisons)
                    ShiftComparisons.Add(s);

                DefectParetos.Clear();
                foreach (var d in defectParetos)
                    DefectParetos.Add(d);

                StatusTimeline.Clear();
                foreach (var segment in statusTimeline)
                    StatusTimeline.Add(segment);

                DefectConcentrations.Clear();
                foreach (var concentration in defectConcentrations)
                    DefectConcentrations.Add(concentration);

                HealthIssues.Clear();
                foreach (var issue in healthIssues)
                    HealthIssues.Add(issue);
                SelectedTimelineSegment = null;

                ReviewConclusions.Clear();
                foreach (var conclusion in reviewConclusions)
                    ReviewConclusions.Add(conclusion);

                // 图表签名比对：数据未变时跳过 PlotModel 重建（避免 60s 定时刷新的无谓 CPU 开销与 UI 闪烁）
                // 时间戳不纳入签名（允许时间标签最多滞后一个刷新周期），仅比较产量/率值/设备明细
                var signature = ComputeChartSignature(bucketOk, bucketNg, oee, performance,
                    availability, quality, avgTargetCycle, SelectedTimeRange, sortedSummaries);
                if (signature != _cachedChartSignature)
                {
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

                    OeeWaterfallChart = _chartService.BuildOeeWaterfall(
                        performance,
                        availability,
                        quality,
                        oee,
                        CreateChartPalette());
                    OnPropertyChanged(nameof(OeeWaterfallChart));

                    ProductionHeatmapChart = _chartService.BuildHeatmap(
                        buckets,
                        sortedSummaries
                            .Select(summary => new ProductionReviewHeatmapRow(summary.DeviceName, summary.HourlyOk))
                            .ToList(),
                        (ProductionReviewBucketSize)bucketSize,
                        CreateChartPalette());
                    OnPropertyChanged(nameof(ProductionHeatmapChart));

                    _cachedChartSignature = signature;
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
                DefectConcentrations.Clear();
                HealthIssues.Clear();
                ReviewConclusions.Clear();
                ReviewConclusions.Add("当前时间范围未配置设备，无法生成复盘结论");
                HealthScore = 100;
                CurrentWorkOrderText = "暂无运行工单";
                CurrentProductText = "暂无产品信息";
                CurrentRecipeText = "暂无配方信息";
                SelectedTimelineSegment = null;
                TrendChart = null;
                OnPropertyChanged(nameof(TrendChart));
                OeeWaterfallChart = null;
                OnPropertyChanged(nameof(OeeWaterfallChart));
                ProductionHeatmapChart = null;
                OnPropertyChanged(nameof(ProductionHeatmapChart));
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
        IReadOnlyList<DeviceOverviewSummary> deviceSummaries)
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
            OverviewTimeRange.PreviousShift => (from - duration, from, "更早上一班次"),
            OverviewTimeRange.Today => (from.AddDays(-1), from, "昨日"),
            OverviewTimeRange.Hour1 => (from.AddHours(-1), from, "前一小时"),
            OverviewTimeRange.Hours8 => (from.AddHours(-8), from, "前 8 小时"),
            OverviewTimeRange.Hours24 => (from.AddDays(-1), from, "前 24 小时"),
            OverviewTimeRange.Days7 => (from.AddDays(-7), from, "前 7 天"),
            _ => (from - duration, from, "上一周期"),
        };
    }

    private (DateTime From, DateTime To, string Label) GetPreviousShiftComparison(DateTime now)
    {
        var shifts = _appSettings.Shifts;
        var (current, currentIndex) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (current == null || shifts == null || shifts.Count == 0)
            return (now.AddHours(-24), now, "前 24 小时");
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
    /// 返回每组的总时长（秒）。未恢复的 Triggered 用 now 近似。
    /// </summary>
    private static Dictionary<string, double> PairAlarmDurations(
        IEnumerable<AlarmEventRecord> events,
        Func<AlarmEventRecord, string> keySelector,
        DateTime now)
    {
        return events
            .GroupBy(keySelector)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var sorted = g.OrderBy(e => e.EventTime).ToList();
                    double total = 0;
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (sorted[i].EventType != AlarmEventType.Triggered) continue;
                        // 找下一个 Recovered
                        DateTime end = now;
                        for (int j = i + 1; j < sorted.Count; j++)
                        {
                            if (sorted[j].EventType == AlarmEventType.Recovered)
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
    private static (string Name, double Seconds) FindLongestAlarm(List<AlarmEventRecord> events)
    {
        if (events.Count == 0) return (string.Empty, 0);
        var durations = PairAlarmDurations(events, e => e.AlarmName, DateTime.Now);
        if (durations.Count == 0) return (string.Empty, 0);
        var max = durations.Aggregate((a, b) => a.Value >= b.Value ? a : b);
        return (max.Key, max.Value);
    }

    // ──────────── 峰值/谷值 ────────────

    private static void FindPeakValleyHour(DateTime[] buckets, int[] okCounts,
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
        peakHour = buckets[maxIdx].ToString("HH:mm");
        valleyHour = buckets[minIdx].ToString("HH:mm");
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

        var snapshots = _defectHistoryStore.Query(from.AddDays(-1), to, device.Id);
        var list = new List<DefectParetoSummary>();
        foreach (var group in snapshots.GroupBy(snapshot => new { snapshot.DefectId, snapshot.ShiftName }))
        {
            var ordered = group.OrderBy(snapshot => snapshot.Timestamp).ToList();
            var window = ordered.Where(snapshot => snapshot.Timestamp >= from && snapshot.Timestamp <= to).ToList();
            if (window.Count == 0) continue;
            var first = window[0];
            var last = window[^1];
            var baseline = ordered.LastOrDefault(snapshot => snapshot.Timestamp < from);
            var count = baseline != null
                ? Math.Max(0, last.Count - baseline.Count)
                : window.Count > 1 ? Math.Max(0, last.Count - first.Count) : Math.Max(0, last.Count);
            if (count <= 0) continue;
            list.Add(new DefectParetoSummary
            {
                DefectName = last.DefectName,
                DeviceName = last.DeviceName,
                Count = count,
            });
        }

        list = list.OrderByDescending(d => d.Count).Take(10).ToList();
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

    private static List<string> BuildReviewConclusions(
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
        List<string> result = [];
        var total = totalOk + totalNg;
        if (total == 0 && totalAlarmCount == 0)
            return ["当前时间范围没有足够的产量或报警数据生成复盘结论"];

        if (total > 0)
        {
            result.Add(string.Format(Strings.F194, quality, (quality >= QualityTarget ? "达到" : "低于"), QualityTarget));
        }

        if (oee > 0)
        {
            result.Add(string.Format(Strings.F006, oee, (oee >= OeeTarget ? "达到" : "低于"), OeeTarget));
        }

        if (longestDowntimeSec > 0)
        {
            result.Add(string.Format(Strings.F138, longestDowntimeSec / 3600.0, longestDowntimeDevice, longestDowntimeAlarm));
        }

        var topShift = shifts.Where(s => s.TotalCount > 0).OrderByDescending(s => s.TotalCount).FirstOrDefault();
        if (topShift != null)
            result.Add(string.Format(Strings.F041, topShift.ShiftName, topShift.TotalCount, topShift.OkRatio));

        var topDefect = defects.FirstOrDefault();
        if (topDefect != null)
            result.Add(string.Format(Strings.F059, topDefect.DefectName, topDefect.DeviceName, topDefect.Count, topDefect.CumulativePercent));

        if (totalAlarmCount > 0 && result.Count < 5)
            result.Add(string.Format(Strings.F123, totalAlarmCount));

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
