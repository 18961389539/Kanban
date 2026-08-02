using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MainAPP.Data;
using MainAPP.Entities;
using MainAPP.Models;
using MainAPP.Services;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using Serilog;

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
    public string OutputText => $"产量 {OutputDelta:N0} · 报警 {AlarmCount} 次";
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
public partial class OverviewViewModel : ObservableObject
{
    private readonly IProductionReviewDataService _reviewDataService;
    private readonly DeviceRepository _deviceRepository;
    private readonly AppSettings _appSettings;
    private readonly IDialogService _dialog;
    private readonly IDeviceSelectionService _selection;
    private readonly IProductionReviewPdfService? _pdfService;
    private readonly DefectHistoryStore? _defectHistoryStore;
    private readonly WorkOrderRepository? _workOrderRepository;
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
        $"{ComparisonLabel}：产量 {FormatSigned(OutputDelta)} 件 · 良品率 {FormatPercentageDelta(QualityRateDelta)} · OEE {FormatPercentageDelta(OeeDelta)}";

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
    public string TargetStatusText => $"良品率 {QualityRate:P1} / 目标 {QualityTarget:P0} · OEE {Oee:P1} / 目标 {OeeTarget:P0}";

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
            _dialog.NotifySuccess($"生产复盘报表已导出：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出生产复盘报表失败");
            _dialog.NotifyError($"报表导出失败：{ex.Message}");
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
            _dialog.NotifySuccess($"生产复盘 PDF 已导出：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出生产复盘 PDF 失败");
            _dialog.NotifyError($"PDF 导出失败：{ex.Message}");
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
        DeviceRepository deviceRepository,
        AppSettings appSettings,
        IDialogService dialog,
        IDeviceSelectionService selection,
        IProductionReviewPdfService? pdfService,
        DefectHistoryStore? defectHistoryStore,
        WorkOrderRepository? workOrderRepository,
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
    /// 兼容旧测试和外部调用方的构造入口。生产 DI 使用上面的分层构造，
    /// 此入口仅负责把旧历史门面适配为复盘应用服务。
    /// </summary>
    public OverviewViewModel(
        IHistoryService historyService,
        DeviceRepository deviceRepository,
        AppSettings appSettings,
        IDialogService dialog,
        IDeviceSelectionService selection,
        IProductionReviewPdfService? pdfService = null,
        DefectHistoryStore? defectHistoryStore = null,
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
            _dialog.NotifyError($"复盘数据加载失败: {ex.Message}");
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
            AvailabilityLossText = $"可用率 {availability:P1}，损失 {(1 - availability):P1}";
            PerformanceLossText = $"性能率 {performance:P1}，损失 {(1 - performance):P1}";
            QualityLossText = $"良品率 {quality:P1}，损失 {(1 - quality):P1}";
            HealthScore = healthScore;
            CurrentWorkOrderText = workOrderText;
            CurrentProductText = productText;
            CurrentRecipeText = recipeText;
        }

        if (!_uiDispatcher.HasShutdownStarted)
            _uiDispatcher.Invoke(ApplyKpis);

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
        return (range.Start, range.End, $"上一班次（{previous.Name}）");
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

    // ──────────── 报警统计辅助 ────────────

    private List<AlarmOverviewSummary> BuildTopAlarms(
        List<AlarmEventRecord> events,
        List<ProductionLog> productionLogs)
    {
        var now = DateTime.Now;
        return events
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .GroupBy(e => new { e.AlarmName, e.DeviceName, e.PlcAddress })
            .Select(group =>
            {
                var triggers = group.OrderBy(e => e.EventTime).ToList();
                var intervals = triggers.Zip(triggers.Skip(1), (first, second) =>
                    (second.EventTime - first.EventTime).TotalMinutes).ToList();
                var averageInterval = intervals.Count > 0 ? intervals.Average() : 0;
                var outputBefore = 0;
                var outputAfter = 0;
                foreach (var trigger in triggers)
                {
                    outputBefore += CalculateProductionDelta(
                        productionLogs, trigger.EventTime.AddMinutes(-15), trigger.EventTime);
                    outputAfter += CalculateProductionDelta(
                        productionLogs, trigger.EventTime, trigger.EventTime.AddMinutes(15));
                }

                return new AlarmOverviewSummary
                {
                    AlarmName = group.Key.AlarmName,
                    DeviceName = group.Key.DeviceName,
                    PlcAddress = group.Key.PlcAddress,
                    TriggerCount = triggers.Count,
                    AverageIntervalMinutes = averageInterval,
                    IsHighFrequency = triggers.Count >= 3 || (triggers.Count >= 2 && averageInterval <= 30),
                    OutputBefore = outputBefore,
                    OutputAfter = outputAfter,
                    ShiftName = triggers[0].ShiftName,
                    TotalDurationHours = CalcAlarmDurationHours(events, group.Key.AlarmName, group.Key.DeviceName),
                };
            })
            .OrderByDescending(a => a.TriggerCount)
            .ThenBy(a => a.AverageIntervalMinutes == 0 ? double.MaxValue : a.AverageIntervalMinutes)
            .Take(5)
            .ToList();
    }

    private List<ReviewStatusSegment> BuildStatusTimeline(
        Device device,
        List<StatusTransitionRecord> transitions,
        List<ProductionLog> productionLogs,
        DateTime from,
        DateTime to,
        List<AlarmEventRecord> alarms)
    {
        var ordered = transitions
            .Where(t => t.EventTime >= from && t.EventTime <= to)
            .OrderBy(t => t.EventTime)
            .ToList();
            var initialState = _reviewDataService.GetLatestStatusBefore(device.Id, from)?.CurrentState
            ?? (int)DeviceStatus.Unknown;
        var segments = new List<ReviewStatusSegment>();
        var cursor = from;
        var state = initialState;

        foreach (var transition in ordered)
        {
            var transitionTime = transition.EventTime < from ? from : transition.EventTime > to ? to : transition.EventTime;
            if (transitionTime > cursor)
                segments.Add(CreateStatusSegment(state, cursor, transitionTime, productionLogs, alarms));
            cursor = transitionTime;
            state = transition.CurrentState;
        }

        if (to > cursor)
            segments.Add(CreateStatusSegment(state, cursor, to, productionLogs, alarms));
        return segments.Where(segment => segment.DurationMinutes >= 0.1).ToList();
    }

    private static ReviewStatusSegment CreateStatusSegment(
        int state,
        DateTime start,
        DateTime end,
        List<ProductionLog> productionLogs,
        List<AlarmEventRecord> alarms)
    {
        var output = CalculateProductionDelta(productionLogs, start, end);
        var alarmCount = alarms.Count(alarm =>
            alarm.EventType == AlarmEventType.Triggered
            && alarm.EventTime >= start
            && alarm.EventTime < end);
        return new ReviewStatusSegment
        {
            Start = start,
            End = end,
            StatusWord = state,
            StatusText = state switch
            {
                (int)DeviceStatus.Running => "运行",
                (int)DeviceStatus.Paused => "暂停",
                (int)DeviceStatus.Alarm => "报警",
                0 => "断线",
                _ => "未知",
            },
            OutputDelta = output,
            AlarmCount = alarmCount,
            HasNoOutput = state == (int)DeviceStatus.Running && output == 0,
        };
    }

    private List<DefectConcentrationSummary> BuildDefectConcentrations(Device device, DateTime from, DateTime to)
    {
        if (_defectHistoryStore == null) return [];
        var snapshots = _defectHistoryStore.Query(from.AddDays(-1), to, device.Id);
        var cells = new List<(string Name, string Shift, DateTime Bucket, int Count)>();
        foreach (var group in snapshots.GroupBy(snapshot => new { snapshot.DefectId, snapshot.ShiftName }))
        {
            var ordered = group.OrderBy(snapshot => snapshot.Timestamp).ToList();
            var previous = ordered.LastOrDefault(snapshot => snapshot.Timestamp < from);
            foreach (var snapshot in ordered.Where(snapshot => snapshot.Timestamp >= from && snapshot.Timestamp <= to))
            {
                var count = previous == null
                    ? Math.Max(0, snapshot.Count)
                    : Math.Max(0, snapshot.Count - previous.Count);
                if (count > 0)
                {
                    var bucket = new DateTime(snapshot.Timestamp.Year, snapshot.Timestamp.Month, snapshot.Timestamp.Day, snapshot.Timestamp.Hour, 0, 0);
                    cells.Add((snapshot.DefectName, snapshot.ShiftName, bucket, count));
                }
                previous = snapshot;
            }
        }

        var total = cells.Sum(cell => cell.Count);
        return cells
            .GroupBy(cell => new { cell.Name, cell.Shift, cell.Bucket })
            .Select(group => new DefectConcentrationSummary
            {
                DefectName = group.Key.Name,
                ShiftName = group.Key.Shift,
                TimeRangeText = group.Key.Bucket.ToString("MM-dd HH:00"),
                Count = group.Sum(cell => cell.Count),
                Share = total > 0 ? (double)group.Sum(cell => cell.Count) / total : 0,
            })
            .OrderByDescending(item => item.Count)
            .Take(10)
            .ToList();
    }

    private List<string> BuildHealthIssues(
        Device device,
        List<ProductionLog> productionLogs,
        List<AlarmEventRecord> alarms,
        List<ReviewStatusSegment> timeline,
        List<DefectConcentrationSummary> concentrations,
        DateTime from,
        DateTime to)
    {
        List<string> issues = [];
        var totalOutput = CalculateProductionDelta(productionLogs, from, to);
        var runHours = timeline.Where(segment => segment.StatusWord == (int)DeviceStatus.Running)
            .Sum(segment => segment.DurationMinutes) / 60.0;
        if (device.TargetCycle > 0 && runHours > 0 && totalOutput / runHours < device.TargetCycle * 0.8)
            issues.Add($"节拍异常：实际 {totalOutput / runHours:F1} 件/小时，低于目标 {device.TargetCycle:F0} 件/小时的 80%");

        var currentAlarmCount = alarms.Count(alarm => alarm.EventType == AlarmEventType.Triggered);
        var comparison = GetComparisonRange(from, to);
        var previousAlarmCount = _reviewDataService.QueryAlarmEvents(comparison.From, comparison.To, device.Id)
            .Count(alarm => alarm.EventType == AlarmEventType.Triggered);
        if (currentAlarmCount >= 3 && (previousAlarmCount == 0 || currentAlarmCount > previousAlarmCount * 1.5))
            issues.Add($"报警突增：当前 {currentAlarmCount} 次，上一周期 {previousAlarmCount} 次");

        var defectCount = concentrations.Sum(item => item.Count);
        var previousDefectCount = 0;
        if (_defectHistoryStore != null)
        {
            var previousConcentrations = BuildDefectConcentrations(device, comparison.From, comparison.To);
            previousDefectCount = previousConcentrations.Sum(item => item.Count);
        }
        if (defectCount >= 3 && (previousDefectCount == 0 || defectCount > previousDefectCount * 1.5))
            issues.Add($"缺陷率突增：当前 {defectCount} 个，上一周期 {previousDefectCount} 个");

        var idleRunning = timeline.FirstOrDefault(segment => segment.HasNoOutput && segment.DurationMinutes >= 30);
        if (idleRunning != null)
            issues.Add($"运行无产量：{idleRunning.TimeRangeText} 持续 {idleRunning.DurationText}");
        return issues;
    }

    private static int CalculateHealthScore(IReadOnlyList<string> issues)
    {
        var score = 100;
        foreach (var issue in issues)
        {
            score -= issue.StartsWith("运行无产量", StringComparison.Ordinal) ? 30
                : issue.StartsWith("缺陷率突增", StringComparison.Ordinal) ? 25
                : issue.StartsWith("报警突增", StringComparison.Ordinal) ? 20
                : 25;
        }
        return Math.Clamp(score, 0, 100);
    }

    private static int CalculateProductionDelta(List<ProductionLog> logs, DateTime from, DateTime to)
    {
        var inWindow = logs.Where(log => log.Timestamp >= from && log.Timestamp <= to).ToList();
        var baseline = logs.Where(log => log.Timestamp < from).ToList();
        var (ok, ng) = HistoryQueryHelper.SumWindowProduction(inWindow, baseline, from);
        return ok + ng;
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

    /// <summary>
    /// 计算指定报警名+设备名的 Triggered→Recovered 配对总时长（小时）。
    /// 复用 PairAlarmDurations，按 "AlarmName|DeviceName" 复合键分组后取指定 key。
    /// </summary>
    private static double CalcAlarmDurationHours(List<AlarmEventRecord> events, string alarmName, string deviceName)
    {
        var key = $"{alarmName}|{deviceName}";
        var durations = PairAlarmDurations(events, e => $"{e.AlarmName}|{e.DeviceName}", DateTime.Now);
        return durations.TryGetValue(key, out double sec) ? sec / 3600.0 : 0;
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
            result.Add($"良品率为 {quality:P1}，{(quality >= QualityTarget ? "达到" : "低于")} {QualityTarget:P0} 目标");
        }

        if (oee > 0)
        {
            result.Add($"OEE 为 {oee:P1}，{(oee >= OeeTarget ? "达到" : "低于")} {OeeTarget:P0} 目标");
        }

        if (longestDowntimeSec > 0)
        {
            result.Add($"最长停机为 {longestDowntimeSec / 3600.0:F1} 小时，设备：{longestDowntimeDevice}，报警：{longestDowntimeAlarm}");
        }

        var topShift = shifts.Where(s => s.TotalCount > 0).OrderByDescending(s => s.TotalCount).FirstOrDefault();
        if (topShift != null)
            result.Add($"{topShift.ShiftName}产量最高，共 {topShift.TotalCount:N0} 件，良品率 {topShift.OkRatio:P1}");

        var topDefect = defects.FirstOrDefault();
        if (topDefect != null)
            result.Add($"主要缺陷为 {topDefect.DefectName}（{topDefect.DeviceName}），当前累计 {topDefect.Count:N0} 件，占当前缺陷 Top10 的 {topDefect.CumulativePercent:F1}%");

        if (totalAlarmCount > 0 && result.Count < 5)
            result.Add($"当前范围共触发 {totalAlarmCount:N0} 次报警，请结合 Top 5 报警确认主要停机来源");

        return result.Take(5).ToList();
    }

    /// <summary>
    /// 构建 OEE 瀑布图：从 100% 逐步扣减性能损失、可用率损失、质量损失，得到最终 OEE。
    /// 用 RectangleBarSeries 画 4 个柱：起始 100%、性能后、可用后、质量后(OEE)。
    /// 损失部分用红色柱向下显示。
    /// </summary>
    private static PlotModel BuildOeeWaterfallChart(double performance, double availability, double quality, double oee)
    {
        var textColor = ChartPalette.Text;
        var gridColor = ChartPalette.Grid;
        var baseColor = ChartPalette.Base;   // 起始/最终柱（蓝）
        var lossColor = ChartPalette.Loss;    // 损失柱（红）
        var remainColor = ChartPalette.Remain;  // 剩余柱（灰）

        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = textColor,
        };

        var catAxis = new CategoryAxis
        {
            Position = AxisPosition.Bottom,
            TextColor = textColor,
            TicklineColor = gridColor,
            AxislineColor = gridColor,
            MajorGridlineStyle = LineStyle.None,
        };
        catAxis.Key = "wfCat";
        catAxis.Labels.Add("起始");
        catAxis.Labels.Add("性能损失");
        catAxis.Labels.Add("可用损失");
        catAxis.Labels.Add("质量损失");
        catAxis.Labels.Add("OEE");
        model.Axes.Add(catAxis);

        var valAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            Maximum = 100,
            Title = "百分比(%)",
            TextColor = textColor,
            TitleColor = textColor,
            TicklineColor = gridColor,
            MajorGridlineColor = gridColor,
            MajorGridlineStyle = LineStyle.Solid,
            LabelFormatter = v => $"{v:F0}%",
        };
        valAxis.Key = "wfVal";
        model.Axes.Add(valAxis);

        // 瀑布数据：起始100 → 扣性能损失 → 扣可用损失 → 扣质量损失 → OEE
        var perfLoss = (1 - performance) * 100;
        var availLoss = performance * (1 - availability) * 100;
        var qualLoss = performance * availability * (1 - quality) * 100;

        // 用 RectangleBarSeries 画柱（OxyPlot 2.2 无 ColumnSeries）
        var series = new RectangleBarSeries
        {
            FillColor = baseColor,
            StrokeColor = OxyColors.Transparent,
            XAxisKey = "wfCat",
            YAxisKey = "wfVal",
        };

        // 起始柱：0 → 100
        series.Items.Add(new RectangleBarItem(0.1, 0, 0.9, 100) { Color = baseColor });
        // 性能损失后：0 → (100 - perfLoss)
        var afterPerf = 100 - perfLoss;
        series.Items.Add(new RectangleBarItem(1.1, 0, 1.9, afterPerf) { Color = remainColor });
        // 可用损失后：0 → (afterPerf - availLoss)
        var afterAvail = afterPerf - availLoss;
        series.Items.Add(new RectangleBarItem(2.1, 0, 2.9, afterAvail) { Color = remainColor });
        // 质量损失后：0 → (afterAvail - qualLoss) = OEE
        var afterQual = afterAvail - qualLoss;
        series.Items.Add(new RectangleBarItem(3.1, 0, 3.9, afterQual) { Color = remainColor });
        // OEE 最终柱
        series.Items.Add(new RectangleBarItem(4.1, 0, 4.9, oee * 100) { Color = baseColor });
        model.Series.Add(series);

        // 损失标注（TextAnnotation 显示损失值）
        void AddLossLabel(int catIdx, double fromVal, double toVal, string text, OxyColor color)
        {
            if (Math.Abs(fromVal - toVal) < 0.1) return;
            model.Annotations.Add(new TextAnnotation
            {
                Text = text,
                TextColor = color,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Stroke = OxyColors.Transparent,
                Background = OxyColors.Transparent,
                TextPosition = new DataPoint(catIdx, (fromVal + toVal) / 2),
                TextVerticalAlignment = VerticalAlignment.Middle,
                TextHorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        AddLossLabel(1, 100, afterPerf, $"-{perfLoss:F1}%", lossColor);
        AddLossLabel(2, afterPerf, afterAvail, $"-{availLoss:F1}%", lossColor);
        AddLossLabel(3, afterAvail, afterQual, $"-{qualLoss:F1}%", lossColor);

        // OEE 最终值标注
        model.Annotations.Add(new TextAnnotation
        {
            Text = $"{oee * 100:F1}%",
            TextColor = baseColor,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Stroke = OxyColors.Transparent,
            Background = OxyColors.Transparent,
            TextPosition = new DataPoint(4, oee * 100),
            TextVerticalAlignment = VerticalAlignment.Bottom,
            TextHorizontalAlignment = HorizontalAlignment.Center,
        });

        return model;
    }

    /// <summary>
    /// 构建时段产量热力图：设备（Y 轴）× 时段桶（X 轴），颜色深浅表示产量。
    /// 用 RectangleBarSeries 每格一个矩形，颜色按产量线性映射（浅→深）。
    /// 直接复用 QueryData 已计算的 deviceSummaries.HourlyOk，避免再次查询 ProductionLogs。
    /// </summary>
    private PlotModel BuildProductionHeatmap(IReadOnlyList<DeviceOverviewSummary> devices, DateTime[] buckets, BucketSize bucketSize)
    {
        if (buckets.Length == 0 || devices.Count == 0)
        {
            return new PlotModel { Background = OxyColors.Transparent };
        }

        var textColor = ChartPalette.Text;
        var gridColor = ChartPalette.Grid;

        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = textColor,
        };

        // X 轴：时间
        var xAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            TicklineColor = gridColor,
            MajorGridlineColor = gridColor,
            MajorGridlineStyle = LineStyle.Solid,
            AxislineColor = gridColor,
            TextColor = textColor,
            StringFormat = SelectedTimeRange == OverviewTimeRange.Days7 ? "MM-dd" : "HH:mm",
        };
        model.Axes.Add(xAxis);

        // Y 轴：设备（CategoryAxis）
        var catAxis = new CategoryAxis
        {
            Position = AxisPosition.Left,
            TextColor = textColor,
            TicklineColor = gridColor,
            AxislineColor = gridColor,
            MajorGridlineStyle = LineStyle.None,
        };
        foreach (var d in devices)
            catAxis.Labels.Add(d.DeviceName);
        model.Axes.Add(catAxis);

        // 收集每台设备每个桶的产量：直接复用 deviceSummaries.HourlyOk（已在 QueryData 中差分计算）
        var heatData = new int[devices.Count, buckets.Length];
        var maxOk = 1;
        for (int di = 0; di < devices.Count; di++)
        {
            var hourly = devices[di].HourlyOk;
            if (hourly == null) continue;
            for (int bi = 0; bi < buckets.Length && bi < hourly.Count; bi++)
            {
                heatData[di, bi] = hourly[bi];
                if (hourly[bi] > maxOk) maxOk = hourly[bi];
            }
        }

        // 用 RectangleBarSeries 画热力格
        var series = new RectangleBarSeries
        {
            StrokeColor = ChartPalette.HeatmapBorder,
            StrokeThickness = 0.5,
        };
        var bucketSpanTicks = bucketSize switch
        {
            BucketSize.Minute5 => TimeSpan.FromMinutes(5).Ticks,
            BucketSize.Hour => TimeSpan.FromHours(1).Ticks,
            BucketSize.Day => TimeSpan.FromDays(1).Ticks,
            _ => TimeSpan.FromHours(1).Ticks,
        };

        for (int di = 0; di < devices.Count; di++)
        {
            for (int bi = 0; bi < buckets.Length; bi++)
            {
                var val = heatData[di, bi];
                var x0 = DateTimeAxis.ToDouble(buckets[bi]);
                var x1 = DateTimeAxis.ToDouble(buckets[bi].AddTicks(bucketSpanTicks));
                var y0 = di - 0.4;
                var y1 = di + 0.4;
                // 颜色：0 值用深灰，否则按产量比例从深蓝到亮蓝
                OxyColor color;
                color = val <= 0
                    ? ChartPalette.HeatmapZero
                    : ChartPalette.Heatmap((double)val / maxOk);
                series.Items.Add(new RectangleBarItem(x0, y0, x1, y1) { Color = color });
            }
        }
        model.Series.Add(series);

        return model;
    }

    // ──────────── 设备跳转 ────────────

    /// <summary>
    /// 点击设备行：设置共享选中设备并触发跳转主页请求。
    /// </summary>
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
