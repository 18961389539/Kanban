using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
using MainAPP.Resources;
using MainAPP.Helpers;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using System.Windows.Threading;

namespace MainAPP.ViewModels;

public enum DataSourceMonitorStatus
{
    Normal,
    Alarm,
    Stale,
    ReadFailed,
    NotSampled,
}

public enum DataSourceMonitorDisplayMode
{
    Table,
    CompactCards,
    Trend,
}

public sealed record DataSourceMonitorFilterItem(string Key, string Name);

public sealed class DataSourceMonitorRow : ObservableObject
{
    public string DeviceId { get; internal set; } = string.Empty;
    public string SourceId { get; internal set; } = string.Empty;
    public string ValueId { get; internal set; } = string.Empty;
    public string DeviceName { get; internal set; } = string.Empty;
    public string SourceName { get; internal set; } = string.Empty;
    public string SourceType { get; internal set; } = string.Empty;
    public string ValueName { get; internal set; } = string.Empty;
    public DataSourceValueType DataType { get; internal set; }
    public string DataTypeText { get; internal set; } = string.Empty;
    public string CurrentValueText { get; internal set; } = string.Empty;
    public string CriteriaText { get; internal set; } = string.Empty;
    public string Unit { get; internal set; } = string.Empty;
    public string PlcAddress { get; internal set; } = string.Empty;
    public string TriggerModeText { get; internal set; } = string.Empty;
    public string TriggerAddressText { get; internal set; } = string.Empty;
    public string TriggerValueText { get; internal set; } = string.Empty;
    public string AckValueText { get; internal set; } = string.Empty;
    public DataSourceMonitorStatus Status { get; internal set; }
    public string StatusText { get; internal set; } = string.Empty;
    public bool IsSampled { get; internal set; }
    public bool IsStale { get; internal set; }
    public bool IsReadFailed { get; internal set; }
    public bool IsEnum { get; internal set; }
    public bool BooleanValue { get; internal set; }
    public double? NumericValue { get; internal set; }
    public double? LowerLimit { get; internal set; }
    public double? UpperLimit { get; internal set; }
    public bool HasLimits => LowerLimit.HasValue && UpperLimit.HasValue;
    public bool HasCurrentValue => IsSampled && !IsReadFailed;
    public bool IsNumeric => DataType is DataSourceValueType.Int32 or DataSourceValueType.Float32;
    public bool IsFloat => DataType == DataSourceValueType.Float32;
    public bool IsBoolean => DataType == DataSourceValueType.Bool;
    public bool IsString => DataType == DataSourceValueType.String;
    public bool IsNotSampled => Status == DataSourceMonitorStatus.NotSampled;
    public bool IsNumericValueVisible => HasCurrentValue && IsNumeric;
    public bool IsBooleanValueVisible => HasCurrentValue && IsBoolean;
    public bool IsStringValueVisible => HasCurrentValue && IsString;
    public bool IsContinuousTrend => DataType == DataSourceValueType.Float32 || (DataType == DataSourceValueType.Int32 && !IsEnum);
    public bool IsStateTrend => IsEnum || DataType == DataSourceValueType.Bool;
    public bool IsTrendSupported => IsContinuousTrend || IsStateTrend;
    public string BooleanDisplayText => BooleanValue ? Strings.Dsm_BoolTrue : Strings.Dsm_BoolFalse;
    public string ValueToolTip => CurrentValueText;
    public string LastValidValueText { get; internal set; } = string.Empty;
    public DateTime? LastUpdatedAt { get; internal set; }
    public DateTime? LastReadAttemptAt { get; internal set; }
    public string LastUpdatedText => LastUpdatedAt is { } time
        ? time.ToString("HH:mm:ss")
        : Strings.Dsm_NotSampled;
    public string FreshnessText => IsReadFailed
        ? Strings.Dsm_ReadFailed
        : !IsSampled
        ? Strings.Dsm_NotSampled
        : IsStale
            ? Strings.Dsm_Stale
            : Strings.Dsm_Fresh;
    public int StatusSortOrder => Status switch
    {
        DataSourceMonitorStatus.ReadFailed => 0,
        DataSourceMonitorStatus.Alarm => 1,
        DataSourceMonitorStatus.Stale => 2,
        DataSourceMonitorStatus.NotSampled => 3,
        _ => 4,
    };
    public DateTime LastUpdatedSortValue => LastUpdatedAt ?? DateTime.MinValue;

    /// <summary>把快照写回这一行。字段都没变时不通知绑定，避免每秒把整行重算一遍。</summary>
    internal bool UpdateFrom(DataSourceMonitorRow snapshot)
    {
        if (DeviceId == snapshot.DeviceId
            && SourceId == snapshot.SourceId
            && ValueId == snapshot.ValueId
            && DeviceName == snapshot.DeviceName
            && SourceName == snapshot.SourceName
            && SourceType == snapshot.SourceType
            && ValueName == snapshot.ValueName
            && DataType == snapshot.DataType
            && DataTypeText == snapshot.DataTypeText
            && CurrentValueText == snapshot.CurrentValueText
            && CriteriaText == snapshot.CriteriaText
            && Unit == snapshot.Unit
            && PlcAddress == snapshot.PlcAddress
            && TriggerModeText == snapshot.TriggerModeText
            && TriggerAddressText == snapshot.TriggerAddressText
            && TriggerValueText == snapshot.TriggerValueText
            && AckValueText == snapshot.AckValueText
            && Status == snapshot.Status
            && StatusText == snapshot.StatusText
            && IsSampled == snapshot.IsSampled
            && IsStale == snapshot.IsStale
            && IsReadFailed == snapshot.IsReadFailed
            && IsEnum == snapshot.IsEnum
            && BooleanValue == snapshot.BooleanValue
            && NumericValue == snapshot.NumericValue
            && LowerLimit == snapshot.LowerLimit
            && UpperLimit == snapshot.UpperLimit
            && LastValidValueText == snapshot.LastValidValueText
            && LastUpdatedAt == snapshot.LastUpdatedAt
            && LastReadAttemptAt == snapshot.LastReadAttemptAt)
            return false;

        DeviceId = snapshot.DeviceId;
        SourceId = snapshot.SourceId;
        ValueId = snapshot.ValueId;
        DeviceName = snapshot.DeviceName;
        SourceName = snapshot.SourceName;
        SourceType = snapshot.SourceType;
        ValueName = snapshot.ValueName;
        DataType = snapshot.DataType;
        DataTypeText = snapshot.DataTypeText;
        CurrentValueText = snapshot.CurrentValueText;
        CriteriaText = snapshot.CriteriaText;
        Unit = snapshot.Unit;
        PlcAddress = snapshot.PlcAddress;
        TriggerModeText = snapshot.TriggerModeText;
        TriggerAddressText = snapshot.TriggerAddressText;
        TriggerValueText = snapshot.TriggerValueText;
        AckValueText = snapshot.AckValueText;
        Status = snapshot.Status;
        StatusText = snapshot.StatusText;
        IsSampled = snapshot.IsSampled;
        IsStale = snapshot.IsStale;
        IsReadFailed = snapshot.IsReadFailed;
        IsEnum = snapshot.IsEnum;
        BooleanValue = snapshot.BooleanValue;
        NumericValue = snapshot.NumericValue;
        LowerLimit = snapshot.LowerLimit;
        UpperLimit = snapshot.UpperLimit;
        LastValidValueText = snapshot.LastValidValueText;
        LastUpdatedAt = snapshot.LastUpdatedAt;
        LastReadAttemptAt = snapshot.LastReadAttemptAt;
        OnPropertyChanged(string.Empty);
        return true;
    }
}

internal readonly record struct DataSourceMonitorTrendPoint(DateTime Timestamp, double? Value);

/// <summary>
/// 跨设备数据采集实时监控：只读展示启用的数据源值项及其最新采样状态。
/// </summary>
public sealed partial class DataSourceMonitoringViewModel : ObservableObject, INavigationPageLifecycle, IDisposable
{
    private const string FloatDisplayFormat = "0.###";
    private const int MaxExceptionRows = 6;
    private const int MaxTrendPoints = 600;
    private const int DefaultPollingIntervalMs = 200;
    /// <summary>连续大约 3 个扫描周期没有新采样，才把定时采集值判为过期。</summary>
    private const int StalePollMultiple = 3;
    /// <summary>过期下限。扫描实际耗时经常长于配置的轮询间隔，低于 5 秒会把正常值刷成过期。</summary>
    private static readonly TimeSpan MinimumStaleAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxTrendAge = TimeSpan.FromMinutes(10);
    private readonly IDeviceRepository _deviceRepository;
    private readonly AppSettings? _appSettings;
    private readonly PageRefreshTimer _refreshTimer;
    private readonly Dictionary<string, List<DataSourceMonitorTrendPoint>> _trendPoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DataSourceMonitorRow> _rowCache = new(StringComparer.Ordinal);
    private readonly LineSeries _trendLineSeries;
    private readonly StairStepSeries _trendStateSeries;
    private readonly LineSeries _trendLowerLimitSeries;
    private readonly LineSeries _trendUpperLimitSeries;
    private bool _isRefreshing;
    private bool _disposed;
    private bool _pageActive;
    private int _trendSeriesVersion;
    private int _drawnTrendVersion = -1;
    private string? _drawnTrendKey;
    private double? _drawnLowerLimit;
    private double? _drawnUpperLimit;
    private bool _drawnStateTrend;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(EmptyStateHint))]
    private string? _selectedDeviceId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(IsExceptionFilterActive))]
    [NotifyPropertyChangedFor(nameof(EmptyStateHint))]
    private string _selectedStateKey = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(EmptyStateHint))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTableView))]
    [NotifyPropertyChangedFor(nameof(IsCompactCardsView))]
    [NotifyPropertyChangedFor(nameof(IsTrendView))]
    [NotifyPropertyChangedFor(nameof(HasDisplayRows))]
    [NotifyPropertyChangedFor(nameof(HasNoDisplayRows))]
    private DataSourceMonitorDisplayMode _selectedDisplayMode = DataSourceMonitorDisplayMode.Table;

    [ObservableProperty]
    private DateTime _lastRefreshTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasConfiguredValues))]
    [NotifyPropertyChangedFor(nameof(EmptyStateTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyStateHint))]
    private int _totalValues;

    [ObservableProperty]
    private int _alarmValues;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadFailedValues))]
    private int _invalidValues;

    [ObservableProperty]
    private int _staleValues;

    [ObservableProperty]
    private int _notSampledValues;

    public int ReadFailedValues => InvalidValues;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExceptionSummaryText))]
    private int _exceptionValues;

    [ObservableProperty]
    private int _normalValues;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingSummaryText))]
    private bool _hasRows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingSummaryText))]
    private int _showingValues;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    private DataSourceMonitorRow? _selectedRow;

    [ObservableProperty]
    private PlotModel _trendChart = CreateTrendChartModel();

    public ObservableCollection<DeviceFilterItem> DeviceFilterItems { get; } = new();
    public ObservableCollection<DataSourceMonitorFilterItem> StateFilterItems { get; } = new();
    public ObservableCollection<DataSourceMonitorRow> Rows { get; } = new();
    public ObservableCollection<DataSourceMonitorRow> TrendRows { get; } = new();
    public ObservableCollection<DataSourceMonitorRow> ExceptionRows { get; } = new();
    public bool HasNoRows => !HasRows;
    public bool HasDisplayRows => HasRows;
    public bool HasNoDisplayRows => !HasDisplayRows;
    public bool HasExceptionRows => ExceptionRows.Count > 0;
    public bool HasConfiguredValues => TotalValues > 0;
    public bool HasSelectedRow => SelectedRow is not null;
    public bool HasActiveFilters => SelectedDeviceId is not null
        || !string.Equals(SelectedStateKey, "All", StringComparison.Ordinal)
        || SearchText.Trim().Length > 0;
    public bool IsExceptionFilterActive => string.Equals(SelectedStateKey, "Exceptions", StringComparison.Ordinal);
    public bool IsTableView
    {
        get => SelectedDisplayMode == DataSourceMonitorDisplayMode.Table;
        set { if (value) SelectedDisplayMode = DataSourceMonitorDisplayMode.Table; }
    }
    public bool IsCompactCardsView
    {
        get => SelectedDisplayMode == DataSourceMonitorDisplayMode.CompactCards;
        set { if (value) SelectedDisplayMode = DataSourceMonitorDisplayMode.CompactCards; }
    }
    public bool IsTrendView
    {
        get => SelectedDisplayMode == DataSourceMonitorDisplayMode.Trend;
        set { if (value) SelectedDisplayMode = DataSourceMonitorDisplayMode.Trend; }
    }
    public bool HasTrendChartData => SelectedRow is { IsTrendSupported: true } row
        && _trendPoints.TryGetValue(GetRowKey(row), out var points)
        && points.Any(point => point.Value.HasValue);
    public bool HasTrendDataGap => SelectedRow is { IsTrendSupported: true } row
        && _trendPoints.TryGetValue(GetRowKey(row), out var points)
        && points.Any(point => !point.Value.HasValue);
    public string TrendSessionHint => string.Format(
        Strings.Dsm_TrendSessionHint,
        (int)MaxTrendAge.TotalMinutes,
        MaxTrendPoints);
    public string TrendStateTitle => SelectedRow is null
        ? Strings.Dsm_TrendSelectValue
        : !SelectedRow.IsTrendSupported
            ? Strings.Dsm_TrendUnsupported
            : Strings.Dsm_TrendNoData;
    public string TrendStateHint => SelectedRow is null
        ? Strings.Dsm_TrendSelectHint
        : !SelectedRow.IsTrendSupported
            ? Strings.Dsm_TrendSelectHint
            : Strings.Dsm_TrendNoDataHint;
    public string ShowingSummaryText => string.Format(Strings.Dsm_ShowingSummary, ShowingValues, TotalValues);
    public string ExceptionSummaryText => string.Format(Strings.Dsm_ExceptionSummary, ExceptionValues);
    public string EmptyStateTitle => HasConfiguredValues ? Strings.Dsm_NoMatch : Strings.Dsm_Empty;
    public string EmptyStateHint => HasConfiguredValues
        ? Strings.Dsm_NoMatchHint
        : Strings.Dsm_EmptyHint;

    public DataSourceMonitoringViewModel(IDeviceRepository deviceRepository, AppSettings? appSettings = null)
    {
        _deviceRepository = deviceRepository;
        _appSettings = appSettings;
        _trendLineSeries = (LineSeries)TrendChart.Series[0];
        _trendStateSeries = (StairStepSeries)TrendChart.Series[1];
        _trendLowerLimitSeries = (LineSeries)TrendChart.Series[2];
        _trendUpperLimitSeries = (LineSeries)TrendChart.Series[3];
        StateFilterItems.Add(new("All", Strings.Dsm_AllStates));
        StateFilterItems.Add(new("Normal", Strings.Dsm_Normal));
        StateFilterItems.Add(new("Alarm", Strings.Dsm_Alarm));
        StateFilterItems.Add(new("Stale", Strings.Dsm_Stale));
        StateFilterItems.Add(new("ReadFailed", Strings.Dsm_ReadFailed));
        StateFilterItems.Add(new("NotSampled", Strings.Dsm_NotSampled));
        StateFilterItems.Add(new("Exceptions", Strings.Dsm_Exceptions));

        _refreshTimer = new PageRefreshTimer(TimeSpan.FromSeconds(1), OnRefreshTimerTick);
    }

    public void OnPageEnter()
    {
        if (_refreshTimer.IsEnabled) return;
        _pageActive = true;
        ResetTrendSession();
        UpdateTrendChart();
        _refreshTimer.Start();
        UiDispatcher.PostOrDrop(() =>
        {
            if (!_pageActive) return;
            Refresh();
        }, DispatcherPriority.Background);
    }

    public void OnPageExit()
    {
        _pageActive = false;
        _refreshTimer.Stop();
        ResetTrendSession();
        UpdateTrendChart();
    }

    private void ResetTrendSession()
    {
        _trendPoints.Clear();
        _trendSeriesVersion++;
        _trendLineSeries.Points.Clear();
        _trendStateSeries.Points.Clear();
        _trendLowerLimitSeries.Points.Clear();
        _trendUpperLimitSeries.Points.Clear();
    }

    [RelayCommand]
    private void Refresh()
    {
        var devices = _deviceRepository.GetDevicesSnapshot();
        _isRefreshing = true;
        try
        {
            RefreshDeviceFilterItems(devices);
        }
        finally
        {
            _isRefreshing = false;
        }

        RefreshRows(devices);
        LastRefreshTime = DateTime.Now;
    }

    [RelayCommand]
    private void SelectState(string? stateKey)
    {
        SelectedStateKey = string.IsNullOrWhiteSpace(stateKey) ? "All" : stateKey;
    }

    [RelayCommand]
    private void ShowExceptions() => SelectState("Exceptions");

    [RelayCommand]
    private void ClearFilters()
    {
        _isRefreshing = true;
        try
        {
            SelectedDeviceId = null;
            SelectedStateKey = "All";
            SearchText = string.Empty;
        }
        finally
        {
            _isRefreshing = false;
        }

        RefreshRows();
    }

    [RelayCommand]
    private void SelectRow(DataSourceMonitorRow? row)
    {
        if (row is not null)
            SelectedRow = row;
    }

    private void OnRefreshTimerTick()
    {
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            // 1 秒定时器回调里的异常会直通 Dispatcher，未接住就会终止进程。
            Trace.TraceError(ex.ToString());
        }
    }

    private void RefreshDeviceFilterItems(IReadOnlyList<Device> devices)
    {
        var selectedId = SelectedDeviceId;
        var ordered = devices
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!DeviceFilterMatches(ordered))
        {
            DeviceFilterItems.Clear();
            DeviceFilterItems.Add(new DeviceFilterItem(null, Strings.Dsm_AllDevices));
            foreach (var device in ordered)
                DeviceFilterItems.Add(new DeviceFilterItem(device.Id, device.Name));
        }

        var nextId = selectedId is not null && ordered.Any(device => string.Equals(device.Id, selectedId, StringComparison.Ordinal))
            ? selectedId
            : null;
        if (!string.Equals(SelectedDeviceId, nextId, StringComparison.Ordinal))
            SelectedDeviceId = nextId;
    }

    private bool DeviceFilterMatches(IReadOnlyList<Device> ordered)
    {
        if (DeviceFilterItems.Count != ordered.Count + 1 || DeviceFilterItems[0].Id is not null)
            return false;

        for (var index = 0; index < ordered.Count; index++)
        {
            var item = DeviceFilterItems[index + 1];
            var device = ordered[index];
            if (!string.Equals(item.Id, device.Id, StringComparison.Ordinal)
                || !string.Equals(item.Name, device.Name, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private void RefreshRows(IReadOnlyList<Device>? devices = null)
    {
        devices ??= _deviceRepository.GetDevicesSnapshot();
        var now = DateTime.Now;
        var staleAge = GetStaleAge();
        var selectedRowKey = SelectedRow is null ? null : GetRowKey(SelectedRow);
        var allRows = devices
            .SelectMany(device => device.Sources
                .Where(source => source.Enabled)
                .SelectMany(source => source.Values
                    .Where(value => value.Enabled)
                    .Select(value => GetOrUpdateRow(device, source, value, now, staleAge))))
            .OrderBy(row => row.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.SourceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ValueName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var activeRowKeys = allRows.Select(GetRowKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _rowCache.Keys.Where(key => !activeRowKeys.Contains(key)).ToList())
            _rowCache.Remove(key);

        TotalValues = allRows.Count;
        AlarmValues = allRows.Count(row => row.Status == DataSourceMonitorStatus.Alarm);
        InvalidValues = allRows.Count(row => row.Status == DataSourceMonitorStatus.ReadFailed);
        StaleValues = allRows.Count(row => row.Status == DataSourceMonitorStatus.Stale);
        NotSampledValues = allRows.Count(row => row.Status == DataSourceMonitorStatus.NotSampled);
        NormalValues = allRows.Count(row => row.Status == DataSourceMonitorStatus.Normal);
        ExceptionValues = allRows.Count(row => row.Status != DataSourceMonitorStatus.Normal);

        var exceptionRows = allRows
            .Where(row => row.Status != DataSourceMonitorStatus.Normal)
            .OrderBy(row => row.StatusSortOrder)
            .ThenBy(row => row.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.SourceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ValueName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxExceptionRows)
            .ToList();
        ReconcileRows(ExceptionRows, exceptionRows);
        OnPropertyChanged(nameof(HasExceptionRows));

        var filteredRows = allRows.Where(MatchesFilter).ToList();
        ReconcileRows(Rows, filteredRows);

        ReconcileRows(TrendRows, filteredRows.Where(row => row.IsTrendSupported).ToList());

        OnPropertyChanged(nameof(HasDisplayRows));
        OnPropertyChanged(nameof(HasNoDisplayRows));

        ShowingValues = Rows.Count;
        HasRows = Rows.Count > 0;
        SelectedRow = selectedRowKey is null
            ? null
            : Rows.FirstOrDefault(row => string.Equals(GetRowKey(row), selectedRowKey, StringComparison.Ordinal))
                ?? allRows.FirstOrDefault(row => string.Equals(GetRowKey(row), selectedRowKey, StringComparison.Ordinal));

        if (SelectedDisplayMode == DataSourceMonitorDisplayMode.Trend
            && (SelectedRow is null || !SelectedRow.IsTrendSupported))
        {
            SelectedRow = GetDefaultTrendRow();
        }

        UpdateTrendPoints(allRows);
        UpdateTrendChart();
    }

    private DataSourceMonitorRow GetOrUpdateRow(
        Device device,
        DataSource source,
        DataSourceValue value,
        DateTime now,
        TimeSpan staleAge)
    {
        var key = GetRowKey(device.Id, source.Id, value.Id);
        var snapshot = CreateRow(device, source, value, now, staleAge);
        if (_rowCache.TryGetValue(key, out var row))
        {
            row.UpdateFrom(snapshot);
            return row;
        }

        _rowCache[key] = snapshot;
        return snapshot;
    }

    private static void ReconcileRows(
        ObservableCollection<DataSourceMonitorRow> target,
        IReadOnlyList<DataSourceMonitorRow> desired)
    {
        var commonCount = Math.Min(target.Count, desired.Count);
        for (var index = 0; index < commonCount; index++)
        {
            if (!ReferenceEquals(target[index], desired[index]))
                target[index] = desired[index];
        }

        for (var index = commonCount; index < desired.Count; index++)
            target.Add(desired[index]);

        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    private bool MatchesFilter(DataSourceMonitorRow row)
    {
        if (SelectedDeviceId is not null && !string.Equals(row.DeviceId, SelectedDeviceId, StringComparison.Ordinal))
            return false;

        var stateMatches = SelectedStateKey switch
        {
            "Normal" => row.Status == DataSourceMonitorStatus.Normal,
            "Alarm" => row.Status == DataSourceMonitorStatus.Alarm,
            "Stale" => row.Status == DataSourceMonitorStatus.Stale,
            "ReadFailed" => row.Status == DataSourceMonitorStatus.ReadFailed,
            "NotSampled" => row.Status == DataSourceMonitorStatus.NotSampled,
            "Exceptions" => row.Status != DataSourceMonitorStatus.Normal,
            _ => true,
        };
        if (!stateMatches) return false;

        var query = SearchText.Trim();
        if (query.Length == 0) return true;

        return row.DeviceName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || row.SourceName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || row.SourceType.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || row.ValueName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || row.CurrentValueText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || row.Unit.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || row.PlcAddress.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private TimeSpan GetStaleAge()
    {
        var intervalMs = _appSettings?.PollingIntervalMs ?? DefaultPollingIntervalMs;
        if (intervalMs < 10 || intervalMs > 60_000)
            intervalMs = DefaultPollingIntervalMs;
        var scaled = TimeSpan.FromMilliseconds((long)intervalMs * StalePollMultiple);
        return scaled > MinimumStaleAge ? scaled : MinimumStaleAge;
    }

    private static DataSourceMonitorRow CreateRow(
        Device device,
        DataSource source,
        DataSourceValue value,
        DateTime now,
        TimeSpan staleAge)
    {
        var isSampled = value.LastUpdatedAt.HasValue;
        var isReadFailed = value.HasReadAttempt && !value.IsValid;
        // 触发采集只在电平到来时更新，长时间不触发不是过期。
        var isStale = isSampled
            && !isReadFailed
            && !source.HasTrigger
            && now - value.LastUpdatedAt!.Value > staleAge;
        var status = !value.HasReadAttempt
            ? DataSourceMonitorStatus.NotSampled
            : isReadFailed
                ? DataSourceMonitorStatus.ReadFailed
                : value.IsTriggered
                    ? DataSourceMonitorStatus.Alarm
                    : isStale
                        ? DataSourceMonitorStatus.Stale
                        : DataSourceMonitorStatus.Normal;
        var lastValidValueText = isSampled ? FormatCurrentValue(value) : Strings.Dsm_NotSampled;
        var currentValueText = isReadFailed
            ? Strings.Dsm_ReadFailed
            : !isSampled
                ? Strings.Dsm_NotSampled
                : lastValidValueText;
        var numericValue = isSampled && !isReadFailed
            ? value.DataType switch
            {
                DataSourceValueType.Int32 => (double)value.CurrentValue,
                DataSourceValueType.Float32 => value.CurrentFloatValue,
                DataSourceValueType.Bool => value.CurrentBoolValue ? 1d : 0d,
                _ => (double?)null,
            }
            : null;
        double? lowerLimit = value.DataType switch
        {
            DataSourceValueType.Float32 when value.HasLimits => value.FloatLimitMin,
            DataSourceValueType.Int32 when value.HasLimits => value.LimitMin,
            _ => null,
        };
        double? upperLimit = value.DataType switch
        {
            DataSourceValueType.Float32 when value.HasLimits => value.FloatLimitMax,
            DataSourceValueType.Int32 when value.HasLimits => value.LimitMax,
            _ => null,
        };

        return new DataSourceMonitorRow
        {
            DeviceId = device.Id,
            SourceId = source.Id,
            ValueId = value.Id,
            DeviceName = device.Name,
            SourceName = string.IsNullOrWhiteSpace(source.DisplayName) ? Strings.Dsm_Unspecified : source.DisplayName,
            SourceType = string.IsNullOrWhiteSpace(source.Type) ? Strings.Dsm_Unspecified : source.Type,
            ValueName = string.IsNullOrWhiteSpace(value.Name) ? Strings.Dsm_Unspecified : value.Name,
            DataType = value.DataType,
            DataTypeText = FormatDataType(value.DataType),
            CurrentValueText = currentValueText,
            CriteriaText = FormatCriteria(value),
            Unit = string.IsNullOrWhiteSpace(value.Unit) ? "-" : value.Unit,
            PlcAddress = string.IsNullOrWhiteSpace(value.PlcAddress) ? "-" : value.PlcAddress,
            TriggerModeText = source.HasTrigger ? Strings.Dsm_Triggered : Strings.Dsm_Periodic,
            TriggerAddressText = source.HasTrigger ? source.TriggerAddress : "-",
            TriggerValueText = source.HasTrigger ? source.TriggerValue.ToString() : "-",
            AckValueText = source.HasTrigger ? source.AckValue.ToString() : "-",
            Status = status,
            StatusText = FormatStatus(status),
            IsSampled = isSampled,
            IsStale = isStale,
            IsReadFailed = isReadFailed,
            IsEnum = value.DataType == DataSourceValueType.Int32 && value.EnumValues.Count > 0,
            LastValidValueText = lastValidValueText,
            BooleanValue = value.CurrentBoolValue,
            NumericValue = numericValue,
            LowerLimit = lowerLimit,
            UpperLimit = upperLimit,
            LastUpdatedAt = value.LastUpdatedAt,
            LastReadAttemptAt = value.LastReadAttemptAt,
        };
    }

    private void UpdateTrendPoints(IEnumerable<DataSourceMonitorRow> rows)
    {
        var rowList = rows.ToList();
        var activeKeys = rowList.Select(GetRowKey).ToHashSet(StringComparer.Ordinal);
        var removedTrend = false;
        foreach (var key in _trendPoints.Keys.Where(key => !activeKeys.Contains(key)).ToList())
        {
            _trendPoints.Remove(key);
            removedTrend = true;
        }

        if (removedTrend)
            _trendSeriesVersion++;

        foreach (var row in rowList)
        {
            if (!row.IsTrendSupported)
                continue;

            var key = GetRowKey(row);
            if (!_trendPoints.TryGetValue(key, out var points))
            {
                points = new List<DataSourceMonitorTrendPoint>();
                _trendPoints[key] = points;
            }

            if (row.IsReadFailed && row.LastReadAttemptAt is { } failedAt)
            {
                if (AppendTrendPoint(points, new DataSourceMonitorTrendPoint(failedAt, null)))
                    _trendSeriesVersion++;
                continue;
            }

            if (row.NumericValue is not { } numericValue
                || row.LastUpdatedAt is not { } timestamp)
                continue;

            if (AppendTrendPoint(points, new DataSourceMonitorTrendPoint(timestamp, numericValue)))
                _trendSeriesVersion++;
        }
    }

    /// <summary>
    /// 只接受不早于末点的采样。失败点用读取尝试时间，成功点用更新时间，
    /// 乱序点如果直接入队，曲线会回折，按时间丢弃旧点也会删错。
    /// </summary>
    private static bool AppendTrendPoint(
        List<DataSourceMonitorTrendPoint> points,
        DataSourceMonitorTrendPoint point)
    {
        if (points.Count > 0)
        {
            var last = points[^1];
            if (point.Timestamp < last.Timestamp)
                return false;
            if (point.Timestamp == last.Timestamp)
            {
                if (last.Value == point.Value)
                    return false;
                points[^1] = point;
                return true;
            }
        }

        points.Add(point);
        var newestTimestamp = points[^1].Timestamp;
        var removeCount = 0;
        while (removeCount < points.Count - 1 && newestTimestamp - points[removeCount].Timestamp > MaxTrendAge)
            removeCount++;
        if (removeCount > 0)
            points.RemoveRange(0, removeCount);
        if (points.Count > MaxTrendPoints)
            points.RemoveRange(0, points.Count - MaxTrendPoints);
        return true;
    }

    private void UpdateTrendChart()
    {
        if (SelectedDisplayMode != DataSourceMonitorDisplayMode.Trend)
            return;

        var selectedTrendRow = SelectedRow;
        var trendKey = selectedTrendRow is { IsTrendSupported: true } supported ? GetRowKey(supported) : null;
        var lowerLimit = selectedTrendRow?.LowerLimit;
        var upperLimit = selectedTrendRow?.UpperLimit;
        var stateTrend = selectedTrendRow?.IsStateTrend == true;
        if (_drawnTrendVersion == _trendSeriesVersion
            && _drawnTrendKey == trendKey
            && _drawnLowerLimit == lowerLimit
            && _drawnUpperLimit == upperLimit
            && _drawnStateTrend == stateTrend)
            return;

        _trendLineSeries.Points.Clear();
        _trendStateSeries.Points.Clear();
        _trendLowerLimitSeries.Points.Clear();
        _trendUpperLimitSeries.Points.Clear();
        _trendLineSeries.IsVisible = selectedTrendRow?.IsContinuousTrend == true;
        _trendStateSeries.IsVisible = stateTrend;
        var hasLimits = selectedTrendRow?.IsContinuousTrend == true
            && lowerLimit.HasValue
            && upperLimit.HasValue;
        _trendLowerLimitSeries.IsVisible = hasLimits;
        _trendUpperLimitSeries.IsVisible = hasLimits;
        TrendChart.IsLegendVisible = hasLimits;
        if (trendKey is not null
            && selectedTrendRow is not null
            && _trendPoints.TryGetValue(trendKey, out var points))
        {
            foreach (var point in points)
            {
                var dataPoint = DateTimeAxis.CreateDataPoint(point.Timestamp, point.Value ?? double.NaN);
                if (stateTrend)
                    _trendStateSeries.Points.Add(dataPoint);
                else
                    _trendLineSeries.Points.Add(dataPoint);
            }

            if (hasLimits && points.Count > 0)
            {
                var firstTimestamp = points[0].Timestamp;
                var lastTimestamp = points[^1].Timestamp;
                if (firstTimestamp == lastTimestamp)
                {
                    firstTimestamp = firstTimestamp.AddSeconds(-1);
                    lastTimestamp = lastTimestamp.AddSeconds(1);
                }

                _trendLowerLimitSeries.Title = $"{Strings.Dsm_TrendLowerLimit} {FormatTrendLimit(lowerLimit!.Value)}";
                _trendUpperLimitSeries.Title = $"{Strings.Dsm_TrendUpperLimit} {FormatTrendLimit(upperLimit!.Value)}";
                _trendLowerLimitSeries.Points.Add(DateTimeAxis.CreateDataPoint(firstTimestamp, lowerLimit.Value));
                _trendLowerLimitSeries.Points.Add(DateTimeAxis.CreateDataPoint(lastTimestamp, lowerLimit.Value));
                _trendUpperLimitSeries.Points.Add(DateTimeAxis.CreateDataPoint(firstTimestamp, upperLimit.Value));
                _trendUpperLimitSeries.Points.Add(DateTimeAxis.CreateDataPoint(lastTimestamp, upperLimit.Value));
            }
        }

        TrendChart.InvalidatePlot(true);
        _drawnTrendVersion = _trendSeriesVersion;
        _drawnTrendKey = trendKey;
        _drawnLowerLimit = lowerLimit;
        _drawnUpperLimit = upperLimit;
        _drawnStateTrend = stateTrend;
        OnPropertyChanged(nameof(HasTrendChartData));
        OnPropertyChanged(nameof(HasTrendDataGap));
        OnPropertyChanged(nameof(TrendStateTitle));
        OnPropertyChanged(nameof(TrendStateHint));
    }

    private DataSourceMonitorRow? GetDefaultTrendRow() =>
        TrendRows.FirstOrDefault(row => row.NumericValue is not null) ?? TrendRows.FirstOrDefault();

    private static PlotModel CreateTrendChartModel()
    {
        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            IsLegendVisible = false,
            PlotAreaBorderColor = ChartPalette.Axis,
            PlotAreaBorderThickness = new OxyThickness(0, 0, 0, 1),
            TextColor = ChartPalette.Text,
            DefaultFont = "Microsoft YaHei",
        };
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "HH:mm:ss",
            TextColor = ChartPalette.MutedText,
            AxislineColor = OxyColors.Transparent,
            MajorGridlineColor = ChartPalette.Grid,
            MajorGridlineStyle = LineStyle.Solid,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = Strings.Dsm_TrendYAxis,
            StringFormat = "0.###",
            TextColor = ChartPalette.MutedText,
            TitleColor = ChartPalette.MutedText,
            AxislineColor = OxyColors.Transparent,
            MajorGridlineColor = ChartPalette.Grid,
            MajorGridlineStyle = LineStyle.Solid,
        });
        model.Series.Add(new LineSeries
        {
            Color = ChartPalette.Base,
            StrokeThickness = 2,
            MarkerType = MarkerType.Circle,
            MarkerSize = 2.5,
        });
        model.Series.Add(new StairStepSeries
        {
            Color = ChartPalette.Base,
            StrokeThickness = 2,
            MarkerType = MarkerType.Circle,
            MarkerSize = 2.5,
            IsVisible = false,
        });
        model.Series.Add(new LineSeries
        {
            Color = ChartPalette.Pause,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1.5,
            MarkerType = MarkerType.None,
            IsVisible = false,
        });
        model.Series.Add(new LineSeries
        {
            Color = ChartPalette.Alarm,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1.5,
            MarkerType = MarkerType.None,
            IsVisible = false,
        });
        model.Legends.Add(new Legend
        {
            IsLegendVisible = true,
            LegendPlacement = LegendPlacement.Inside,
            LegendPosition = LegendPosition.TopLeft,
            LegendOrientation = LegendOrientation.Horizontal,
            LegendBackground = ChartPalette.LegendBackground,
            LegendTextColor = ChartPalette.MutedText,
            LegendPadding = 6,
            LegendMargin = 6,
            LegendItemSpacing = 12,
        });
        return model;
    }

    private static string FormatCurrentValue(DataSourceValue value) => value.DataType switch
    {
        DataSourceValueType.Float32 => FormatFloat(value.CurrentFloatValue),
        DataSourceValueType.Bool => FormatBool(value.CurrentBoolValue),
        DataSourceValueType.String => FormatString(value.CurrentStringValue),
        _ => value.CurrentDisplayText,
    };

    private static string FormatFloat(float value) => value.ToString(FloatDisplayFormat, System.Globalization.CultureInfo.CurrentCulture);

    private static string FormatTrendLimit(double value) => value.ToString(FloatDisplayFormat, System.Globalization.CultureInfo.CurrentCulture);

    private static string FormatBool(bool value) => value ? Strings.Dsm_BoolTrue : Strings.Dsm_BoolFalse;

    private static string FormatString(string? value) => string.IsNullOrEmpty(value) ? "-" : value;

    private static string FormatCriteria(DataSourceValue value)
    {
        if (value.HasLimits)
        {
            return value.DataType == DataSourceValueType.Float32
                ? $"{FormatFloat(value.FloatLimitMin)} ~ {FormatFloat(value.FloatLimitMax)}"
                : $"{value.LimitMin} ~ {value.LimitMax}";
        }

        if (!value.HasExpectedValue) return "-";
        return value.DataType switch
        {
            DataSourceValueType.Float32 => value.FloatExpectedValue is { } expectedFloat ? FormatFloat(expectedFloat) : "-",
            DataSourceValueType.Bool => value.BoolExpectedValue is { } expectedBool ? FormatBool(expectedBool) : "-",
            DataSourceValueType.String => FormatString(value.StringExpectedValue),
            _ => value.ExpectedValue?.ToString() ?? "-",
        };
    }

    private static string GetRowKey(DataSourceMonitorRow row) => GetRowKey(row.DeviceId, row.SourceId, row.ValueId);

    private static string GetRowKey(string deviceId, string sourceId, string valueId) => $"{deviceId}|{sourceId}|{valueId}";

    private static string FormatDataType(DataSourceValueType dataType) => dataType switch
    {
        DataSourceValueType.Float32 => Strings.Dsm_Float32,
        DataSourceValueType.Bool => Strings.Dsm_Bool,
        DataSourceValueType.String => Strings.Dsm_String,
        _ => Strings.Dsm_Int32,
    };

    private static string FormatStatus(DataSourceMonitorStatus status) => status switch
    {
        DataSourceMonitorStatus.Alarm => Strings.Dsm_Alarm,
        DataSourceMonitorStatus.Stale => Strings.Dsm_Stale,
        DataSourceMonitorStatus.ReadFailed => Strings.Dsm_ReadFailed,
        DataSourceMonitorStatus.NotSampled => Strings.Dsm_NotSampled,
        _ => Strings.Dsm_Normal,
    };

    partial void OnSelectedDeviceIdChanged(string? value)
    {
        if (!_isRefreshing) RefreshRows();
    }

    partial void OnSelectedStateKeyChanged(string value)
    {
        if (!_isRefreshing) RefreshRows();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!_isRefreshing) RefreshRows();
    }

    partial void OnHasRowsChanged(bool value) => OnPropertyChanged(nameof(HasNoRows));

    partial void OnSelectedRowChanged(DataSourceMonitorRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedRow));
        UpdateTrendChart();
    }

    partial void OnSelectedDisplayModeChanged(DataSourceMonitorDisplayMode value)
    {
        if (value == DataSourceMonitorDisplayMode.Trend
            && (SelectedRow is null || !SelectedRow.IsTrendSupported))
        {
            SelectedRow = GetDefaultTrendRow();
        }

        UpdateTrendChart();
    }

    partial void OnTotalValuesChanged(int value)
    {
        OnPropertyChanged(nameof(HasConfiguredValues));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateHint));
    }

    partial void OnShowingValuesChanged(int value) => OnPropertyChanged(nameof(ShowingSummaryText));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Dispose();
        _rowCache.Clear();
        _trendPoints.Clear();
        _trendLineSeries.Points.Clear();
        _trendStateSeries.Points.Clear();
        _trendLowerLimitSeries.Points.Clear();
        _trendUpperLimitSeries.Points.Clear();
    }
}
