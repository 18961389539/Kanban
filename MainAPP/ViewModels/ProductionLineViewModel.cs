using System.Collections.Generic;
using MainAPP.Resources;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using MainAPP.Helpers;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Serilog;

namespace MainAPP.ViewModels;

/// <summary>
/// 产线页 ViewModel：所有设备俯瞰视图。
/// 数据来自 DeviceRepository（DI 单例，与主页共享），无需独立定时器。
/// </summary>
public partial class ProductionLineViewModel : ObservableObject, IDisposable, INavigationPageLifecycle
{
    private static readonly ILogger _log = Log.ForContext<ProductionLineViewModel>();
    private readonly DeviceRepository _deviceRepository;
    private readonly IDeviceSelectionService _selection;
    private readonly IPlcDataAcquisitionService? _plcService;
    private readonly AppSettings? _appSettings;
    private readonly IDialogService? _dialog;

    /// <summary>设备项集合（与 Devices/Runtimes 同步）。</summary>
    public ObservableCollection<LineDeviceItem> LineDevices { get; } = new();

    /// <summary>无设备时显示空状态（BooleanToVisibilityConverter 直接绑定）。</summary>
    public bool HasNoDevices => LineDevices.Count == 0;

    /// <summary>
    /// 是否固定详细布局（产线页布局重构后：单一详细卡片模板，不再按设备数切换
    /// 大卡/中卡/表格三态）。恒为 true，供测试与扩展断言使用。
    /// </summary>
    public bool IsDetailedLayout => true;

    /// <summary>当前选中设备 Id（镜像自共享 IDeviceSelectionService.SelectedDeviceId），用于卡片选中态高亮。</summary>
    [ObservableProperty]
    private string? _selectedDeviceId;

    /// <summary>
    /// 跳转主页请求事件：参数为 deviceId。
    /// MainWindowViewModel 订阅后切换 SelectedIndex=0（选中态由共享 IDeviceSelectionService 驱动）。
    /// </summary>
    public event Action<string>? FocusDeviceRequested;

    public ProductionLineViewModel(DeviceRepository deviceRepository, IDeviceSelectionService selection, IPlcDataAcquisitionService? plcService = null, AppSettings? appSettings = null, IDialogService? dialog = null)
    {
        _deviceRepository = deviceRepository;
        _selection = selection;
        _plcService = plcService;
        _appSettings = appSettings;
        _dialog = dialog;
        _log.Information("ProductionLineViewModel 构造：Devices.Count={DeviceCount}, Runtimes.Count={RuntimeCount}",
            _deviceRepository.Devices.Count, _deviceRepository.Runtimes.Count);

        SyncLineDevices();
        SeedBatchTargetFromDevices();
        _deviceRepository.Devices.CollectionChanged += OnDevicesCollectionChanged;
        _deviceRepository.Runtimes.CollectionChanged += OnRuntimesCollectionChanged;
        _selection.PropertyChanged += OnSelectionChanged;
        SelectedDeviceId = _selection.SelectedDeviceId;
        if (_appSettings != null)
            _appSettings.Shifts.CollectionChanged += OnShiftsChanged;
        _kpiTimer = new PageRefreshTimer(TimeSpan.FromMilliseconds(500), OnKpiTimerTick);
        _shiftProgressTimer = new PageRefreshTimer(TimeSpan.FromSeconds(1), OnShiftProgressTick);
        // 筛选/排序视图：ListCollectionView（源 = LineDevices）+ Filter + CustomSort。
        // 原实现 getter 每次 ToList() 返回新 List 实例 → ItemsSource 收到新引用 → Reset →
        // VirtualizingWrapPanel 全量重建容器、虚拟化失效（排序/筛选态下每台设备每次属性变化都触发）。
        FilteredLineDevices = new ListCollectionView(LineDevices);
        FilteredLineDevices.Filter = FilterDevice;
        ApplySort();
        _log.Information("ProductionLineViewModel 构造完成：LineDevices.Count={LineCount}", LineDevices.Count);
    }

    private bool _pageActive;

    // ── KPI 合批刷新状态：Runtime 属性变化只置脏标记，由 Background 定时器合并后一次刷新，
    //    避免 6N 次/帧的 O(N²) 重算（N 台设备 × 每台 6 个属性 × 17 个 O(N) KPI getter）。──

    private readonly PageRefreshTimer _kpiTimer;
    private readonly PageRefreshTimer _shiftProgressTimer;
    private bool _kpisDirty;
    private bool _filterDirty;
    /// <summary>Runtime 反查 LineDeviceItem（O(1)，替代原 FirstOrDefault O(N) 线性查找）。</summary>
    private readonly Dictionary<DeviceRuntime, LineDeviceItem> _runtimeToItem = new();
    /// <summary>合批期间被标记为"派生文本需刷新"的设备项。</summary>
    private readonly HashSet<LineDeviceItem> _dirtyTransientItems = new();

    /// <inheritdoc />
    public void OnPageEnter()
    {
        _pageActive = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_pageActive) return;
            FlushPendingRefresh();
            UpdateCurrentShift();
            RefreshLastShiftComparison();
            RefreshAllShiftProgress();
            _shiftProgressTimer.Start();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <inheritdoc />
    public void OnPageExit()
    {
        _pageActive = false;
        _kpiTimer.Stop();
        _shiftProgressTimer.Stop();
    }

    /// <summary>应用积压的 KPI / 筛选 / 瞬态文本刷新（一次重算，批量通知）。</summary>
    private void FlushPendingRefresh()
    {
        var notifyKpis = _kpisDirty;
        var notifyFilter = _filterDirty;
        LineDeviceItem[] transientItems = [.. _dirtyTransientItems];
        _kpisDirty = false;
        _filterDirty = false;
        _dirtyTransientItems.Clear();

        if (notifyKpis)
        {
            RecalculateKpis();
            NotifyAllKpis();
        }
        if (notifyFilter)
            NotifyFilteredLineDevicesChanged();
        foreach (var item in transientItems)
            item.RefreshTransientTexts();
    }

    private void OnKpiTimerTick()
    {
        _kpiTimer.Stop(); // 单次语义：积压刷新合并为一次即停，dirty 后再由属性变化触发 Start
        if (!_pageActive) return;
        FlushPendingRefresh();
    }

    private void OnShiftProgressTick()
    {
        if (!_pageActive) return;
        RefreshAllShiftProgress();
    }

    private void RefreshAllShiftProgress()
    {
        foreach (var item in LineDevices)
            item.RefreshShiftProgress();
    }

    // ──────────── 汇总 KPI（顶部条） ────────────

    // KPI 聚合缓存：由 RecalculateKpis 单次 O(N) 遍历填充，getter 零分配零 LINQ。
    // 避免原实现每次通知都触发 17 个 O(N) LINQ getter 的 O(N²) 放大。
    private int _runningCount;
    private int _alarmCount;
    private int _pausedCount;
    private int _offlineCount;
    private int _totalOkProduction;
    private int _totalNgProduction;
    private double _overallQualityRate;

    public int DeviceCount => LineDevices.Count;
    public int RunningCount => _runningCount;
    public int AlarmCount => _alarmCount;
    public int PausedCount => _pausedCount;
    /// <summary>离线设备数（<see cref="DeviceStatus.Offline"/>）。</summary>
    public int OfflineCount => _offlineCount;
    public int TotalOkProduction => _totalOkProduction;
    public int TotalNgProduction => _totalNgProduction;
    public int TotalOutput => _totalOkProduction + _totalNgProduction;
    /// <summary>整体合格率 = 总 OK / 总产量。</summary>
    public double OverallQualityRate => _overallQualityRate;

    /// <summary>
    /// 单次 O(N) 遍历累加汇总 KPI（四态计数 + 产量，供班次对比）。
    /// 四个状态精确等值匹配，其它状态值不计入任何计数。
    /// </summary>
    private void RecalculateKpis()
    {
        int running = 0, alarm = 0, paused = 0, offline = 0, ok = 0, ng = 0;
        foreach (var item in LineDevices)
        {
            var runtime = item.Runtime;
            switch (runtime.StatusWord)
            {
                case (int)DeviceStatus.Running: running++; break;
                case (int)DeviceStatus.Alarm: alarm++; break;
                case (int)DeviceStatus.Paused: paused++; break;
                case (int)DeviceStatus.Offline: offline++; break;
            }
            ok += runtime.TotalOkProduction;
            ng += runtime.TotalNgProduction;
        }
        _runningCount = running;
        _alarmCount = alarm;
        _pausedCount = paused;
        _offlineCount = offline;
        _totalOkProduction = ok;
        _totalNgProduction = ng;
        var total = ok + ng;
        _overallQualityRate = total > 0 ? (double)ok / total : 0;
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
                _runtimeToItem.Clear();
                _dirtyTransientItems.Clear();
                LineDevices.Clear();
                RefreshSummaryKpis();
                RefreshLastShiftComparison();
                NotifyFilteredLineDevicesChanged();
                return;
            }

            if (e.NewItems != null)
                foreach (Device dev in e.NewItems)
                    TryAddLineDevice(dev);

            if (e.OldItems != null)
                foreach (Device dev in e.OldItems)
                    TryRemoveLineDevice(dev.Id);

            RefreshSummaryKpis();
            RefreshLastShiftComparison();
            NotifyFilteredLineDevicesChanged();
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

        var item = new LineDeviceItem(dev, runtime, _appSettings);
        item.Runtime.PropertyChanged += OnRuntimePropertyChanged;
        _runtimeToItem[runtime] = item;
        LineDevices.Add(item);
        _log.Information("TryAddLineDevice 成功：{Name}({Id})，当前 LineDevices.Count={Count}", dev.Name, dev.Id, LineDevices.Count);
    }

    /// <summary>移除并取消 Runtime 事件订阅。</summary>
    private void TryRemoveLineDevice(string deviceId)
    {
        var item = LineDevices.FirstOrDefault(x => x.Device.Id == deviceId);
        if (item == null) return;
        item.Runtime.PropertyChanged -= OnRuntimePropertyChanged;
        _runtimeToItem.Remove(item.Runtime);
        _dirtyTransientItems.Remove(item);
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
        _runtimeToItem.Clear();
        _dirtyTransientItems.Clear();
        LineDevices.Clear();

        foreach (var dev in _deviceRepository.Devices)
            TryAddLineDevice(dev);

        RefreshSummaryKpis();
    }

    /// <summary>
    /// Runtime 属性变化：仅置脏标记，由 Background 定时器合批后一次刷新（500ms 合并窗口）。
    /// 原实现每台设备每次属性变化即全量重算 17 个 O(N) KPI getter，O(N²) 级放大。
    /// </summary>
    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeviceRuntime.StatusWord):
                _kpisDirty = true;
                if (_lineStatusFilter != LineStatusFilter.All)
                    _filterDirty = true;
                break;
            case nameof(DeviceRuntime.TotalOkProduction):
            case nameof(DeviceRuntime.TotalNgProduction):
            case nameof(DeviceRuntime.Oee):
            case nameof(DeviceRuntime.QualityRate):
            case nameof(DeviceRuntime.PerformanceRate):
            case nameof(DeviceRuntime.AvailabilityRate):
                _kpisDirty = true;
                if (_lineSortBy != LineSortBy.Default)
                    _filterDirty = true;
                break;
            default:
                return;
        }

        if (sender is DeviceRuntime runtime && _runtimeToItem.TryGetValue(runtime, out var item))
            _dirtyTransientItems.Add(item);

        if (!_pageActive) return; // 页面不可见时不启动定时器，进入页面时 FlushPendingRefresh 统一应用
        if (!_kpiTimer.IsEnabled) _kpiTimer.Start();
    }

    /// <summary>重算 KPI 并批量通知所有 KPI 属性（设备增删等低频路径同步调用）。</summary>
    private void RefreshSummaryKpis()
    {
        RecalculateKpis();
        NotifyAllKpis();
    }

    private void NotifyAllKpis()
    {
        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(AlarmCount));
        OnPropertyChanged(nameof(PausedCount));
        OnPropertyChanged(nameof(OfflineCount));
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(TotalOkProduction));
        OnPropertyChanged(nameof(TotalNgProduction));
        OnPropertyChanged(nameof(TotalOutput));
        OnPropertyChanged(nameof(OverallQualityRate));
        // 产量变化 → diff（current - lastShift）需重算，基线不变故不调 RefreshLastShiftComparison
        OnPropertyChanged(nameof(OutputDiff));
        OnPropertyChanged(nameof(NgDiff));
        OnPropertyChanged(nameof(OutputDiffText));
        OnPropertyChanged(nameof(NgDiffText));
        ApplyBatchTargetCycleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>共享选中服务变化 → 同步本视图的选中镜像，触发卡片选中态刷新。</summary>
    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IDeviceSelectionService.SelectedDeviceId))
            SelectedDeviceId = _selection.SelectedDeviceId;
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

    /// <summary>批量目标产能（件/小时）。各机相同时预填该值，否则为 0（需用户输入后才能应用）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyBatchTargetCycleCommand))]
    private int _batchTargetPcsPerHour;

    private void SeedBatchTargetFromDevices()
    {
        if (LineDevices.Count == 0)
        {
            BatchTargetPcsPerHour = 0;
            return;
        }

        var first = LineDevices[0].Device.TargetCycle;
        BatchTargetPcsPerHour = first > 0 && LineDevices.All(item => item.Device.TargetCycle == first)
            ? first
            : 0;
    }

    private bool CanApplyBatchTargetCycle()
        => BatchTargetPcsPerHour > 0 && LineDevices.Count > 0;

    /// <summary>将输入框中的目标产能写入全部设备配置与运行时，并持久化。</summary>
    [RelayCommand(CanExecute = nameof(CanApplyBatchTargetCycle))]
    private async Task ApplyBatchTargetCycle()
    {
        var pcs = BatchTargetPcsPerHour;
        var count = LineDevices.Count;
        if (pcs <= 0 || count == 0) return;

        if (_dialog != null)
        {
            var confirm = _dialog.Show(
                string.Format(Strings.Ln_ApplyTargetCycleConfirm, count, pcs),
                Strings.Ln_ApplyTargetCycleTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        try
        {
            foreach (var item in LineDevices)
            {
                item.Device.TargetCycle = pcs;
                item.Runtime.SyncTargetCycle(pcs);
                _deviceRepository.SyncTargetCycle(item.Device.Id, pcs);
            }

            RefreshAllShiftProgress();
            await _deviceRepository.SaveAllAsync();

            var done = string.Format(Strings.Ln_ApplyTargetCycleDone, count, pcs);
            AuditLog.Record("Device.Update", "Device", null,
                detail: done,
                after: new { targetCycle = pcs, deviceCount = count });
            _dialog?.NotifySuccess(done);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to apply batch target capacity");
            _dialog?.NotifyError(string.Format(Strings.Ln_ApplyTargetCycleFailed, ex.Message));
        }
    }

    /// <summary>
    /// 筛选/排序变化后刷新视图：Filter 委托读取字段最新状态，Refresh 重跑过滤 + CustomSort。
    /// 与结构变化（设备增删）不同——结构变化由 ListCollectionView 自动感知源集合 CollectionChanged，
    /// 无需此处刷新。
    /// </summary>
    private void NotifyFilteredLineDevicesChanged()
    {
        FilteredLineDevices.Refresh();
        OnPropertyChanged(nameof(HasNoFilteredDevices));
    }

    // ──────────── P0：筛选 / 排序 / 搜索 / 班次标注 ────────────

    /// <summary>排序下拉选项（与 SetSortCommand / LineSortBy 同源）。</summary>
    public IReadOnlyList<LineSortOption> LineSortOptions { get; } =
    [
        new() { Value = LineSortBy.Default, Label = Strings.K410 },
        new() { Value = LineSortBy.AlarmFirst, Label = Strings.K409 },
        new() { Value = LineSortBy.OeeDesc, Label = Strings.Web_Ln_SortOee },
        new() { Value = LineSortBy.OutputDesc, Label = Strings.K411 },
    ];

    private LineStatusFilter _lineStatusFilter = LineStatusFilter.All;
    public LineStatusFilter LineStatusFilter
    {
        get => _lineStatusFilter;
        set
        {
            if (SetProperty(ref _lineStatusFilter, value))
            {
                NotifyFilteredLineDevicesChanged();
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
            {
                ApplySort();
                OnPropertyChanged(nameof(HasNoFilteredDevices));
            }
        }
    }

    private string? _lineSearchKeyword;
    public string? LineSearchKeyword
    {
        get => _lineSearchKeyword;
        set
        {
            if (SetProperty(ref _lineSearchKeyword, value))
                NotifyFilteredLineDevicesChanged();
        }
    }

    /// <summary>当前状态筛选的中文描述（按钮高亮对比用）；文本委托多语言资源。</summary>
    public string LineStatusFilterText => _lineStatusFilter switch
    {
        LineStatusFilter.Running => Strings.Status_Running,
        LineStatusFilter.Alarm => Strings.Status_Alarm,
        LineStatusFilter.Paused => Strings.Status_Paused,
        LineStatusFilter.Offline => Strings.Status_Offline,
        _ => Strings.M040,
    };

    [RelayCommand]
    private void SetStatusFilter(LineStatusFilter? filter)
        => LineStatusFilter = filter ?? LineStatusFilter.All;

    [RelayCommand]
    private void SetSort(LineSortBy? sort)
        => LineSortBy = sort ?? LineSortBy.Default;

    /// <summary>筛选后无匹配设备（有设备但当前条件为空）。</summary>
    public bool HasNoFilteredDevices => !HasNoDevices && FilteredLineDevices.IsEmpty;

    /// <summary>
    /// 经筛选/排序/搜索后的设备集合视图（XAML ItemsControl 绑定此属性而非 LineDevices）。
    /// ListCollectionView：单一视图实例 + Filter 谓词 + CustomSort 比较器，筛选/排序条件变化
    /// 时仅 Refresh 内部重排，ItemsSource 引用恒定 → VirtualizingWrapPanel 不重建容器，虚拟化始终生效。
    /// 原实现 getter 每次返回新 List 实例，触发 ItemsSource Reset 导致整表容器重建（O(N²)）。
    /// </summary>
    public ListCollectionView FilteredLineDevices { get; private set; } = null!;

    /// <summary>过滤谓词：合并关键词搜索（忽略大小写）+ 状态筛选。读字段最新状态，Refresh 时重跑。</summary>
    private bool FilterDevice(object item)
    {
        if (item is not LineDeviceItem x) return false;
        if (!string.IsNullOrWhiteSpace(_lineSearchKeyword)
            && !(x.Device.Name ?? string.Empty).Contains(_lineSearchKeyword, StringComparison.OrdinalIgnoreCase))
            return false;
        return _lineStatusFilter switch
        {
            LineStatusFilter.Running => x.Runtime.StatusWord == (int)DeviceStatus.Running,
            LineStatusFilter.Alarm => x.Runtime.StatusWord == (int)DeviceStatus.Alarm,
            LineStatusFilter.Paused => x.Runtime.StatusWord == (int)DeviceStatus.Paused,
            LineStatusFilter.Offline => x.Runtime.StatusWord == (int)DeviceStatus.Offline,
            _ => true,
        };
    }

    /// <summary>应用排序比较器（DeferRefresh 内整体重排，避免多次中间刷新）。</summary>
    private void ApplySort()
    {
        using var defer = FilteredLineDevices.DeferRefresh();
        FilteredLineDevices.CustomSort = _lineSortBy switch
        {
            LineSortBy.AlarmFirst => new AlarmFirstComparer(),
            LineSortBy.OeeDesc => new OeeDescComparer(),
            LineSortBy.OutputDesc => new OutputDescComparer(),
            _ => null,
        };
    }

    /// <summary>报警优先，其次待机，再按报警时长降序（与原 OrderByDescending 链等价，键值降序比较）。</summary>
    private sealed class AlarmFirstComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not LineDeviceItem a || y is not LineDeviceItem b) return 0;
            var aKey = a.Runtime.StatusWord == (int)DeviceStatus.Alarm ? 2
                     : a.Runtime.StatusWord == (int)DeviceStatus.Paused ? 1 : 0;
            var bKey = b.Runtime.StatusWord == (int)DeviceStatus.Alarm ? 2
                     : b.Runtime.StatusWord == (int)DeviceStatus.Paused ? 1 : 0;
            var c = bKey.CompareTo(aKey);
            return c != 0 ? c : b.Runtime.AlarmTime.CompareTo(a.Runtime.AlarmTime);
        }
    }

    /// <summary>OEE 从高到低。</summary>
    private sealed class OeeDescComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not LineDeviceItem a || y is not LineDeviceItem b) return 0;
            return b.Runtime.Oee.CompareTo(a.Runtime.Oee);
        }
    }

    /// <summary>总产量从高到低。</summary>
    private sealed class OutputDescComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not LineDeviceItem a || y is not LineDeviceItem b) return 0;
            return b.TotalOutput.CompareTo(a.TotalOutput);
        }
    }

    // ───── 班次标注 ─────

    private string _currentShiftName = Strings.M110;
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
        var shifts = _appSettings?.GetShiftsSnapshot(); // P0-1 修复 2026-09-02：锁内快照，禁止直接枚举
        var (shift, _) = HistoryQueryHelper.FindCurrentShift(shifts, now.TimeOfDay);
        if (shift == null)
        {
            CurrentShiftName = (shifts == null || shifts.Count == 0) ? Strings.M110 : Strings.M111;
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
        _kpiTimer.Dispose();
        _shiftProgressTimer.Dispose();
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
        _runtimeToItem.Clear();
        _dirtyTransientItems.Clear();
        LineDevices.Clear();
    }
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
    /// <summary>仅离线（StatusWord=Offline）</summary>
    Offline,
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

/// <summary>产线页排序下拉项（值 + 本地化标签）。</summary>
public class LineSortOption
{
    public LineSortBy Value { get; set; }
    public string Label { get; set; } = string.Empty;
}
