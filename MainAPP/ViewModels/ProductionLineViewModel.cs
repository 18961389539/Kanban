using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Helpers;
using Kanban.Core.Services;
using MainAPP.Services;
using Serilog;

namespace MainAPP.ViewModels;

/// <summary>
/// 产线页 ViewModel：所有设备俯瞰视图。
/// 设备数量驱动 LayoutMode 自适应切换：1-4 大卡片、5-8 中卡片、9+ 紧凑表格。
/// 数据来自 DeviceRepository（DI 单例，与主页共享），无需独立定时器。
/// </summary>
public partial class ProductionLineViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger _log = Log.ForContext<ProductionLineViewModel>();
    private readonly DeviceRepository _deviceRepository;
    private readonly IDeviceSelectionService _selection;
    private readonly IPlcDataAcquisitionService? _plcService;
    private readonly AppSettings? _appSettings;

    /// <summary>设备项集合（与 Devices/Runtimes 同步）。</summary>
    public ObservableCollection<LineDeviceItem> LineDevices { get; } = new();

    /// <summary>当前布局模式（UI 通过 DataTrigger 切换模板）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLargeCardsLayout))]
    [NotifyPropertyChangedFor(nameof(IsMediumCardsLayout))]
    [NotifyPropertyChangedFor(nameof(IsTableLayout))]
    private LineLayoutMode _layoutMode;

    /// <summary>3 个布局布尔可见性辅助属性（避免 enum DataTrigger 匹配问题）。</summary>
    public bool IsLargeCardsLayout => LayoutMode == LineLayoutMode.LargeCards;
    public bool IsMediumCardsLayout => LayoutMode == LineLayoutMode.MediumCards;
    public bool IsTableLayout => LayoutMode == LineLayoutMode.Table;
    /// <summary>无设备时显示空状态（BooleanToVisibilityConverter 直接绑定）。</summary>
    public bool HasNoDevices => LineDevices.Count == 0;

    /// <summary>当前选中设备 Id（镜像自共享 IDeviceSelectionService.SelectedDeviceId），用于卡片选中态高亮。</summary>
    [ObservableProperty]
    private string? _selectedDeviceId;

    /// <summary>
    /// 跳转主页请求事件：参数为 deviceId。
    /// MainWindowViewModel 订阅后切换 SelectedIndex=0（选中态由共享 IDeviceSelectionService 驱动）。
    /// </summary>
    public event Action<string>? FocusDeviceRequested;

    public ProductionLineViewModel(DeviceRepository deviceRepository, IDeviceSelectionService selection, IPlcDataAcquisitionService? plcService = null, AppSettings? appSettings = null)
    {
        _deviceRepository = deviceRepository;
        _selection = selection;
        _plcService = plcService;
        _appSettings = appSettings;
        _log.Information("ProductionLineViewModel 构造：Devices.Count={DeviceCount}, Runtimes.Count={RuntimeCount}",
            _deviceRepository.Devices.Count, _deviceRepository.Runtimes.Count);

        SyncLineDevices();
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
        _deviceRepository.Runtimes.CollectionChanged += OnRuntimesCollectionChanged;
        _selection.PropertyChanged += OnSelectionChanged;
        SelectedDeviceId = _selection.SelectedDeviceId;
        RefreshLastShiftComparison();
        UpdateCurrentShift();
        if (_appSettings != null)
            _appSettings.Shifts.CollectionChanged += OnShiftsChanged;
        _log.Information("ProductionLineViewModel 构造完成：LineDevices.Count={LineCount}, LayoutMode={Mode}",
            LineDevices.Count, LayoutMode);
    }

    // ──────────── 汇总 KPI（顶部条） ────────────

    public int DeviceCount => LineDevices.Count;
    /// <summary>中卡片布局列数：9-12 台使用 4 列，13-15 台使用 5 列。</summary>
    public int MediumColumns => _deviceRepository.Devices.Count <= 12 ? 4 : 5;
    public int RunningCount => LineDevices.Count(d => d.Runtime.StatusWord == (int)DeviceStatus.Running);
    public int AlarmCount => LineDevices.Count(d => d.Runtime.StatusWord == (int)DeviceStatus.Alarm);
    public int PausedCount => LineDevices.Count(d => d.Runtime.StatusWord == (int)DeviceStatus.Paused);
    public int IdleCount => LineDevices.Count(d => d.Runtime.StatusWord == (int)DeviceStatus.Unknown);
    public int TotalOkProduction => LineDevices.Sum(d => d.Runtime.TotalOkProduction);
    public int TotalNgProduction => LineDevices.Sum(d => d.Runtime.TotalNgProduction);
    public int TotalOutput => TotalOkProduction + TotalNgProduction;
    /// <summary>整体合格率 = 总 OK / 总产量。</summary>
    public double OverallQualityRate => TotalOutput > 0 ? (double)TotalOkProduction / TotalOutput : 0;
    /// <summary>产线加权 OEE = 设备 OEE 按产量加权平均（避免少量产量设备拉高均值）。</summary>
    public double WeightedOee
    {
        get
        {
            var totalWeight = LineDevices.Sum(d => d.Runtime.TotalOkProduction + d.Runtime.TotalNgProduction);
            if (totalWeight == 0) return 0;
            return LineDevices.Sum(d => d.Runtime.Oee * (d.Runtime.TotalOkProduction + d.Runtime.TotalNgProduction)) / totalWeight;
        }
    }

    // ──────────── 趋势对比（vs 上一班次，复用 PlcDataAcquisitionService 内存缓存） ────────────

    private int _lastShiftTotalOk;
    private int _lastShiftTotalNg;
    private string _lastShiftName = "";

    /// <summary>是否存在上一班次快照（应用启动后尚未经历班次切换时为 false）。</summary>
    public bool HasLastShift => !string.IsNullOrEmpty(_lastShiftName);

    /// <summary>本班次总产量相比上一班次的增量（正=增长，负=下降）。</summary>
    public int OutputDiff => HasLastShift ? TotalOutput - (_lastShiftTotalOk + _lastShiftTotalNg) : 0;
    /// <summary>本班次不良数相比上一班次的增量（正=增加=坏，负=减少=好）。</summary>
    public int NgDiff => HasLastShift ? TotalNgProduction - _lastShiftTotalNg : 0;

    public string OutputDiffText => FormatHelper.FormatDiff(OutputDiff);
    public string NgDiffText => FormatHelper.FormatDiff(NgDiff);

    /// <summary>
    /// 刷新上一班次对比基线：遍历所有设备调用 PlcDataAcquisitionService.GetLastShiftSummary 汇总。
    /// 仅在构造和设备增减时调用；产量变化时只需触发 diff 属性变更通知（基线不变）。
    /// </summary>
    private void RefreshLastShiftComparison()
    {
        if (_plcService == null || LineDevices.Count == 0)
        {
            _lastShiftName = "";
            _lastShiftTotalOk = 0;
            _lastShiftTotalNg = 0;
            OnPropertyChanged(nameof(HasLastShift));
            OnPropertyChanged(nameof(OutputDiff));
            OnPropertyChanged(nameof(NgDiff));
            OnPropertyChanged(nameof(OutputDiffText));
            OnPropertyChanged(nameof(NgDiffText));
            return;
        }
        var shiftName = "";
        var totalOk = 0;
        var totalNg = 0;
        foreach (var item in LineDevices)
        {
            var (ok, ng, name) = _plcService.GetLastShiftSummary(item.Device.Id);
            if (!string.IsNullOrEmpty(name)) shiftName = name;
            totalOk += ok;
            totalNg += ng;
        }
        _lastShiftName = shiftName;
        _lastShiftTotalOk = totalOk;
        _lastShiftTotalNg = totalNg;
        OnPropertyChanged(nameof(HasLastShift));
        OnPropertyChanged(nameof(OutputDiff));
        OnPropertyChanged(nameof(NgDiff));
        OnPropertyChanged(nameof(OutputDiffText));
        OnPropertyChanged(nameof(NgDiffText));
    }

    // ──────────── 同步逻辑 ────────────

    /// <summary>
    /// Devices 集合变更：增量同步 LineDevices（避免全量重建引起闪烁）。
    /// 设备删除时同步取消 Runtime 事件订阅。
    /// </summary>
    private void OnDevicesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Devices 可能被后台线程（DeviceManagerViewModel.RemoveDevice 等）修改，
        // CollectionChanged 会在修改方所在线程触发。此处操作绑定的 ObservableCollection
        // 必须切回 UI 线程，否则会抛跨线程 InvalidOperationException
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                foreach (var item in LineDevices)
                    item.Runtime.PropertyChanged -= OnRuntimePropertyChanged;
                LineDevices.Clear();
                UpdateLayoutMode();
                RefreshSummaryKpis();
                RefreshLastShiftComparison();
                OnPropertyChanged(nameof(FilteredLineDevices));
                return;
            }

            if (e.NewItems != null)
                foreach (Device dev in e.NewItems)
                    TryAddLineDevice(dev);

            if (e.OldItems != null)
                foreach (Device dev in e.OldItems)
                    TryRemoveLineDevice(dev.Id);

            UpdateLayoutMode();
            RefreshSummaryKpis();
            RefreshLastShiftComparison();
            OnPropertyChanged(nameof(FilteredLineDevices));
        }));
    }

    /// <summary>
    /// Runtimes 集合变更：DeviceRepository.AddRuntime 在 Devices.Add 之后调用，
    /// 此时 LineDeviceItem 可能尚未创建（OnDevicesCollectionChanged 找不到 runtime），
    /// 故此处兜底尝试创建。已存在则跳过（幂等）。
    /// </summary>
    private void OnRuntimesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;
        // 与 OnDevicesCollectionChanged 同理：操作绑定的 ObservableCollection 需切回 UI 线程
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.NewItems == null) return;
            foreach (DeviceRuntime rt in e.NewItems)
            {
                var dev = _deviceRepository.Devices.FirstOrDefault(d => d.Id == rt.DeviceId);
                if (dev != null)
                    TryAddLineDevice(dev);
            }
        }));
    }

    /// <summary>
    /// 幂等添加：若 LineDevices 中已存在同 Id 项则跳过。
    /// 创建 LineDeviceItem 并订阅 Runtime.PropertyChanged（用于刷新汇总 KPI）。
    /// </summary>
    private void TryAddLineDevice(Device dev)
    {
        if (LineDevices.Any(x => x.Device.Id == dev.Id)) return;
        var runtime = _deviceRepository.Runtimes.FirstOrDefault(r => r.DeviceId == dev.Id);
        if (runtime == null)
        {
            _log.Warning("TryAddLineDevice 跳过：设备 {Name}({Id}) 未找到对应 Runtime", dev.Name, dev.Id);
            return; // runtime 尚未添加，等 OnRuntimesCollectionChanged 兜底
        }

        var item = new LineDeviceItem(dev, runtime);
        item.Runtime.PropertyChanged += OnRuntimePropertyChanged;
        LineDevices.Add(item);
        _log.Information("TryAddLineDevice 成功：{Name}({Id})，当前 LineDevices.Count={Count}", dev.Name, dev.Id, LineDevices.Count);
    }

    /// <summary>移除并取消 Runtime 事件订阅。</summary>
    private void TryRemoveLineDevice(string deviceId)
    {
        var item = LineDevices.FirstOrDefault(x => x.Device.Id == deviceId);
        if (item == null) return;
        item.Runtime.PropertyChanged -= OnRuntimePropertyChanged;
        item.Dispose();
        LineDevices.Remove(item);
    }

    /// <summary>初次构造：全量构建 LineDevices。</summary>
    private void SyncLineDevices()
    {
        foreach (var item in LineDevices)
        {
            item.Runtime.PropertyChanged -= OnRuntimePropertyChanged;
            item.Dispose();
        }
        LineDevices.Clear();

        foreach (var dev in _deviceRepository.Devices)
            TryAddLineDevice(dev);

        UpdateLayoutMode();
        RefreshSummaryKpis();
    }

    /// <summary>Runtime 属性变化：刷新汇总 KPI（任一设备 OEE/产量变化都会影响整体）。</summary>
    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeviceRuntime.StatusWord):
                OnPropertyChanged(nameof(RunningCount));
                OnPropertyChanged(nameof(AlarmCount));
                OnPropertyChanged(nameof(PausedCount));
                OnPropertyChanged(nameof(IdleCount));
                if (_lineStatusFilter != LineStatusFilter.All)
                    OnPropertyChanged(nameof(FilteredLineDevices));
                NotifyItemTransient(sender);
                break;
            case nameof(DeviceRuntime.TotalOkProduction):
            case nameof(DeviceRuntime.TotalNgProduction):
            case nameof(DeviceRuntime.Oee):
            case nameof(DeviceRuntime.QualityRate):
            case nameof(DeviceRuntime.PerformanceRate):
            case nameof(DeviceRuntime.AvailabilityRate):
                RefreshSummaryKpis();
                if (_lineSortBy != LineSortBy.Default)
                    OnPropertyChanged(nameof(FilteredLineDevices));
                NotifyItemTransient(sender);
                break;
        }
    }

    /// <summary>运行时状态/缺陷集合变化后，刷新对应卡片的报警/缺陷派生文本。</summary>
    private void NotifyItemTransient(object? sender)
    {
        var item = LineDevices.FirstOrDefault(x => ReferenceEquals(x.Runtime, sender));
        item?.RefreshTransientTexts();
    }

    private void RefreshSummaryKpis()
    {
        OnPropertyChanged(nameof(TotalOkProduction));
        OnPropertyChanged(nameof(TotalNgProduction));
        OnPropertyChanged(nameof(TotalOutput));
        OnPropertyChanged(nameof(OverallQualityRate));
        OnPropertyChanged(nameof(WeightedOee));
        // 产量变化 → diff（current - lastShift）需重算，基线不变故不调 RefreshLastShiftComparison
        OnPropertyChanged(nameof(OutputDiff));
        OnPropertyChanged(nameof(NgDiff));
        OnPropertyChanged(nameof(OutputDiffText));
        OnPropertyChanged(nameof(NgDiffText));
    }

    /// <summary>共享选中服务变化 → 同步本视图的选中镜像，触发卡片选中态刷新。</summary>
    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IDeviceSelectionService.SelectedDeviceId))
            SelectedDeviceId = _selection.SelectedDeviceId;
    }

    private void UpdateLayoutMode()
    {
        var oldMode = LayoutMode;
        LayoutMode = _deviceRepository.Devices.Count switch
        {
            <= 8 => LineLayoutMode.LargeCards,
            <= 15 => LineLayoutMode.MediumCards,
            _ => LineLayoutMode.Table,
        };
        _log.Information("UpdateLayoutMode：Devices.Count={Count}, {Old} → {New}, IsLarge={IsLarge}, IsMedium={IsMedium}, IsTable={IsTable}",
            _deviceRepository.Devices.Count, oldMode, LayoutMode, IsLargeCardsLayout, IsMediumCardsLayout, IsTableLayout);
        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(AlarmCount));
        OnPropertyChanged(nameof(PausedCount));
        OnPropertyChanged(nameof(IdleCount));
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(MediumColumns));
        OnPropertyChanged(nameof(FilteredLineDevices));
    }

    /// <summary>
    /// 点击设备卡片/行：设置主页聚焦设备并发起跳转请求。
    /// MainWindowViewModel 订阅 FocusDeviceRequested 完成导航。
    /// </summary>
    [RelayCommand]
    private void FocusDevice(object? parameter)
    {
        if (parameter is not string deviceId || string.IsNullOrEmpty(deviceId)) return;
        _selection.SelectedDeviceId = deviceId;
        FocusDeviceRequested?.Invoke(deviceId);
    }

    // ──────────── P0：筛选 / 排序 / 搜索 / 班次标注 ────────────

    private LineStatusFilter _lineStatusFilter = LineStatusFilter.All;
    public LineStatusFilter LineStatusFilter
    {
        get => _lineStatusFilter;
        set
        {
            if (SetProperty(ref _lineStatusFilter, value))
            {
                OnPropertyChanged(nameof(FilteredLineDevices));
                OnPropertyChanged(nameof(LineStatusFilterText));
            }
        }
    }

    private LineSortBy _lineSortBy = LineSortBy.Default;
    public LineSortBy LineSortBy
    {
        get => _lineSortBy;
        set
        {
            if (SetProperty(ref _lineSortBy, value))
                OnPropertyChanged(nameof(FilteredLineDevices));
        }
    }

    private string? _lineSearchKeyword;
    public string? LineSearchKeyword
    {
        get => _lineSearchKeyword;
        set
        {
            if (SetProperty(ref _lineSearchKeyword, value))
                OnPropertyChanged(nameof(FilteredLineDevices));
        }
    }

    /// <summary>当前状态筛选的中文描述（按钮高亮对比用）。</summary>
    public string LineStatusFilterText => _lineStatusFilter switch
    {
        LineStatusFilter.Running => "运行",
        LineStatusFilter.Alarm => "报警",
        LineStatusFilter.Paused => "待机",
        _ => "全部",
    };

    [RelayCommand]
    private void SetStatusFilter(LineStatusFilter? filter)
        => LineStatusFilter = filter ?? LineStatusFilter.All;

    [RelayCommand]
    private void SetSort(LineSortBy? sort)
        => LineSortBy = sort ?? LineSortBy.Default;

    /// <summary>
    /// 经筛选/排序/搜索后的设备集合（XAML ItemsControl 绑定此属性而非 LineDevices）。
    /// 仅在筛选条件或底层集合变更时通过 OnPropertyChanged(nameof(FilteredLineDevices)) 触发重建。
    /// </summary>
    public IEnumerable<LineDeviceItem> FilteredLineDevices
    {
        get
        {
            IEnumerable<LineDeviceItem> q = LineDevices;
            if (!string.IsNullOrWhiteSpace(_lineSearchKeyword))
                q = q.Where(x => (x.Device.Name ?? string.Empty).Contains(_lineSearchKeyword, StringComparison.OrdinalIgnoreCase));

            q = _lineStatusFilter switch
            {
                LineStatusFilter.Running => q.Where(x => x.Runtime.StatusWord == (int)DeviceStatus.Running),
                LineStatusFilter.Alarm => q.Where(x => x.Runtime.StatusWord == (int)DeviceStatus.Alarm),
                LineStatusFilter.Paused => q.Where(x => x.Runtime.StatusWord == (int)DeviceStatus.Paused),
                _ => q,
            };

            q = _lineSortBy switch
            {
                LineSortBy.AlarmFirst => q.OrderByDescending(x => x.Runtime.StatusWord == (int)DeviceStatus.Alarm)
                    .ThenByDescending(x => x.Runtime.StatusWord == (int)DeviceStatus.Paused)
                    .ThenByDescending(x => x.Runtime.AlarmTime),
                LineSortBy.OeeDesc => q.OrderByDescending(x => x.Runtime.Oee),
                LineSortBy.OutputDesc => q.OrderByDescending(x => x.TotalOutput),
                _ => q,
            };

            return q.ToList();
        }
    }

    // ───── 班次标注（P0-4）─────

    private string _currentShiftName = "未配置班次";
    public string CurrentShiftName
    {
        get => _currentShiftName;
        private set => SetProperty(ref _currentShiftName, value);
    }

    private string _currentShiftTimeRange = string.Empty;
    public string CurrentShiftTimeRange
    {
        get => _currentShiftTimeRange;
        private set => SetProperty(ref _currentShiftTimeRange, value);
    }

    private void OnShiftsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateCurrentShift();

    /// <summary>根据当前时间计算所属班次名称与起止时间范围（复用 HistoryQueryHelper.FindCurrentShift）。</summary>
    private void UpdateCurrentShift()
    {
        var now = DateTime.Now;
        var shifts = _appSettings?.Shifts;
        var (shift, _) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (shift == null)
        {
            CurrentShiftName = (shifts == null || shifts.Count == 0) ? "未配置班次" : "未匹配班次";
            CurrentShiftTimeRange = string.Empty;
            return;
        }
        CurrentShiftName = shift.Name;
        CurrentShiftTimeRange = $"{shift.StartTime:hh\\:mm}-{shift.EndTime:hh\\:mm}";
    }

    /// <summary>
    /// 解绑所有外部事件订阅，防止 ViewModel 被 DI 容器释放后仍持有
    /// DeviceRepository/Runtimes/SelectionService/AppSettings 的事件引用，
    /// 避免因事件未解绑导致的内存泄漏与僵尸回调。
    /// </summary>
    /// <remarks>
    /// 订阅点：构造函数中订阅 Devices/Runtimes.CollectionChanged、Selection.PropertyChanged、
    /// AppSettings.Shifts.CollectionChanged；TryAddLineDevice 中订阅 Runtime.PropertyChanged。
    /// 此处统一解绑，并对已添加的 LineDeviceItem 解绑其 Runtime 事件。
    /// </remarks>
    public void Dispose()
    {
        _deviceRepository.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        _deviceRepository.Runtimes.CollectionChanged -= OnRuntimesCollectionChanged;
        _selection.PropertyChanged -= OnSelectionChanged;
        if (_appSettings != null)
            _appSettings.Shifts.CollectionChanged -= OnShiftsChanged;

        // LineDeviceItem 自身持有 Runtime 引用并订阅了 PropertyChanged，统一解绑
        foreach (var item in LineDevices)
        {
            item.Runtime.PropertyChanged -= OnRuntimePropertyChanged;
            item.Dispose();
        }
        LineDevices.Clear();
    }
}

/// <summary>产线页布局模式（按设备数量自适应）。</summary>
public enum LineLayoutMode
{
    /// <summary>1-8 设备：大卡片 2×2 网格</summary>
    LargeCards,
    /// <summary>9-15 设备：中卡片响应式网格</summary>
    MediumCards,
    /// <summary>16+ 设备：紧凑表格</summary>
    Table,
}

/// <summary>产线页状态筛选维度。</summary>
public enum LineStatusFilter
{
    /// <summary>全部设备</summary>
    All,
    /// <summary>仅运行（StatusWord=Running）</summary>
    Running,
    /// <summary>仅报警（StatusWord=Alarm）</summary>
    Alarm,
    /// <summary>仅待机（StatusWord=Paused）</summary>
    Paused,
}

/// <summary>产线页排序维度。</summary>
public enum LineSortBy
{
    /// <summary>默认顺序（按设备配置序）</summary>
    Default,
    /// <summary>报警优先，其次待机，再按报警时长排序</summary>
    AlarmFirst,
    /// <summary>OEE 从高到低</summary>
    OeeDesc,
    /// <summary>总产量从高到低</summary>
    OutputDesc,
}
